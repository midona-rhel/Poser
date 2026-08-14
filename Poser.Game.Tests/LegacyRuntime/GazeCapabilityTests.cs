using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Core;
using Poser.Entities;
using Poser.Game;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class GazeCapabilityTests
{
    [Fact]
    public void Missing_update_signature_keeps_gaze_registered_but_unavailable()
    {
        var factory = new TestNativeFactory
        {
            UpdateScan = () => throw new InvalidOperationException("missing"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "update signature");
        Assert.Equal(0, factory.HookCreateCount);
        Assert.Empty(factory.Hooks);
    }

    [Fact]
    public void Missing_loop_signature_does_not_create_a_hook()
    {
        var factory = new TestNativeFactory
        {
            LoopScan = () => throw new InvalidOperationException("missing"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "loop signature");
        Assert.Equal(0, factory.HookCreateCount);
    }

    [Fact]
    public void Hook_creation_failure_is_fail_soft()
    {
        var factory = new TestNativeFactory
        {
            CreateHook = (_, _) => throw new InvalidOperationException("create"),
        };

        using var service = Create(factory);

        AssertUnavailable(service, "hook creation");
        Assert.Equal(1, factory.HookCreateCount);
    }

    [Fact]
    public void Hook_enable_failure_disposes_the_partial_hook()
    {
        var hook = new TestHook { EnableFailure = true };
        var factory = new TestNativeFactory { Hook = hook };

        using var service = Create(factory);

        AssertUnavailable(service, "hook enable");
        Assert.Equal(1, hook.DisposeCount);
        Assert.Equal(0, factory.EventBusSubscriptions);
    }

    [Fact]
    public void Unavailable_service_refuses_all_mutations_without_events()
    {
        var factory = new TestNativeFactory
        {
            UpdateScan = () => throw new InvalidOperationException("missing"),
        };
        using var service = Create(factory);
        var actor = NewProxy<IActor>();

        // Refusals stay typed and carry their reason — the fail-soft contract
        // is unchanged, only made legible.
        var mode = service.SetGazeMode(actor, GazeTargetMode.Camera);
        var parts = service.SetGazeParts(actor, GazeTargetType.Eyes);
        var target = service.SetGazeTarget(actor, actor);
        Assert.False(mode.Success);
        Assert.False(parts.Success);
        Assert.False(target.Success);
        Assert.NotNull(mode.Detail);
        Assert.NotNull(parts.Detail);
        Assert.NotNull(target.Detail);
        Assert.Empty(factory.TargetWrites);

        service.SetGazePosition(actor, new(1, 2, 3));
        service.SetPartPosition(actor, GazeTargetType.Eyes, new(4, 5, 6));
        service.SnapPartToCamera(actor, GazeTargetType.Eyes);
        service.SetPartLock(actor, GazeTargetType.Eyes, true);
        service.ResetGaze(actor);

        Assert.Equal(GazeTargetMode.None, service.GetGazeState(actor).Mode);
        Assert.Equal(GazeTargetType.All, service.GetGazeState(actor).TargetType);
        Assert.Equal((nint)0, service.GetGazeTargetAddress(actor));
        Assert.False(service.IsPartLocked(actor, GazeTargetType.Eyes));
        Assert.False(service.IsGazeEnabled(actor));
        Assert.Equal(0, factory.EventBus.PublishedCount);
    }

    [Fact]
    public void Successful_construction_preserves_registration_and_idempotent_dispose()
    {
        var hook = new TestHook();
        var factory = new TestNativeFactory { Hook = hook };
        var service = Create(factory);

        Assert.True(service.IsAvailable);
        Assert.Null(service.UnavailableDetail);
        Assert.Equal(1, hook.EnableCount);
        Assert.Equal(2, factory.EventBusSubscriptions);

        service.Dispose();
        service.Dispose();

        Assert.Equal(1, hook.DisposeCount);
        Assert.Equal(2, factory.EventBusUnsubscriptions);
    }

    // ── channel release: Brio ActorLookAtService.cs:89-98 ────────────────
    // A channel outside the mask gets no _updateLookAt call, and the original
    // loop runs unconditionally afterwards, so the game re-takes it. Release is
    // cessation — nothing is restored and no override flag is cleared.

    [Fact]
    public void Disabling_one_channel_stops_writing_only_that_channel()
    {
        using var scene = GazeScene.Create();

        scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Camera);
        Assert.Equal(GazeTargetType.All, scene.Written());

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Head | GazeTargetType.Body);

        Assert.Equal(GazeTargetType.Head | GazeTargetType.Body, scene.Written());
    }

    [Fact]
    public void Disabling_every_channel_writes_nothing_so_the_game_owns_all_three()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Camera);

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);

        Assert.Equal(GazeTargetType.None, scene.Written());
        Assert.False(scene.Service.IsGazeEnabled(scene.Actor));
        Assert.False(scene.Service.GetGazeState(scene.Actor).Active);
    }

    // ── target retention: Brio SetTargetType rewrites the mask and nothing
    // else (ActorLookAtService.cs:164-170), so TargetMode and the stored
    // LookAtSource survive an empty mask.

    [Fact]
    public void Untoggling_every_channel_keeps_the_remembered_mode_and_target()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);

        var state = scene.Service.GetGazeState(scene.Actor);
        Assert.Equal(GazeTargetMode.Entity, state.Mode);
        Assert.Equal(GazeScene.TargetId, state.TargetId);
        Assert.False(state.Active);
        Assert.False(state.TargetStale);
    }

    [Fact]
    public void Retoggling_a_channel_reapplies_the_remembered_target()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);

        var result = scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Eyes);

        Assert.True(result.Success);
        Assert.Equal(GazeTargetType.Eyes, scene.Written());
        var state = scene.Service.GetGazeState(scene.Actor);
        Assert.Equal(GazeTargetMode.Entity, state.Mode);
        Assert.Equal(GazeScene.TargetId, state.TargetId);
        Assert.True(state.Active);
    }

    [Fact]
    public void Off_mode_keeps_the_remembered_target_and_reselecting_actor_mode_restores_it()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.None);
        Assert.Equal(GazeScene.TargetId, scene.Service.GetGazeState(scene.Actor).TargetId);

        Assert.True(scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Entity).Success);

        Assert.Equal(GazeTargetType.All, scene.Written());
        Assert.Equal(GazeScene.TargetId, scene.Service.GetGazeState(scene.Actor).TargetId);
    }

    // ── the character's imposed target id: Brio sets it at
    // ActorDynamicPoseWidget.cs:201 and writes 0 back at :218 through
    // ActorLookAtService.cs:194. Without the clear the game's own look-at keeps
    // pointing at the actor Poser chose.

    [Fact]
    public void Untoggling_every_channel_clears_the_characters_imposed_target_id()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        Assert.Equal(new[] { GazeScene.TargetId }, scene.Factory.WrittenTargetIds());

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);

        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Head);

        Assert.Equal(
            new ulong[] { GazeScene.TargetId, 0, GazeScene.TargetId },
            scene.Factory.WrittenTargetIds());
    }

    [Fact]
    public void Leaving_actor_mode_clears_the_characters_imposed_target_id()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Camera);

        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());
    }

    [Fact]
    public void Resetting_gaze_forgets_the_target_and_clears_the_imposed_id()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        // Brio's RemoveObjectFromLook — the ONE path that forgets.
        scene.Service.ResetGaze(scene.Actor);

        var state = scene.Service.GetGazeState(scene.Actor);
        Assert.Equal(GazeTargetMode.None, state.Mode);
        Assert.Equal(0ul, state.TargetId);
        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());
    }

    // ── stale remembered target ──────────────────────────────────────────

    [Fact]
    public void A_despawned_remembered_target_is_kept_by_id_and_stops_enforcing()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        scene.DespawnTarget();

        var state = scene.Service.GetGazeState(scene.Actor);
        Assert.True(state.TargetStale);
        Assert.Equal(GazeScene.TargetId, state.TargetId);
        Assert.Equal(GazeTargetMode.Entity, state.Mode);
        Assert.False(state.Active);
        Assert.Equal(GazeTargetType.None, scene.Written());
        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());
    }

    [Fact]
    public void Reapplying_a_stale_remembered_target_is_refused_typed()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);
        scene.DespawnTarget();

        var parts = scene.Service.SetGazeParts(scene.Actor, GazeTargetType.All);
        var mode = scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Entity);

        Assert.False(parts.Success);
        Assert.False(mode.Success);
        Assert.NotNull(parts.Detail);
        Assert.NotNull(mode.Detail);
        Assert.Equal(GazeTargetType.None, scene.Written());
    }

    [Fact]
    public void Relinquishing_a_channel_is_never_refused_while_the_target_is_stale()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.DespawnTarget();

        Assert.True(scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Head).Success);
        Assert.True(scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None).Success);
    }

    [Fact]
    public void Choosing_a_live_target_lifts_the_stale_mark()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.DespawnTarget();

        Assert.True(scene.Service.SetGazeTarget(scene.Actor, scene.Second).Success);

        var state = scene.Service.GetGazeState(scene.Actor);
        Assert.False(state.TargetStale);
        Assert.Equal(GazeScene.SecondId, state.TargetId);
        Assert.Equal(GazeTargetType.All, scene.Written());
    }

    private static GazeService Create(TestNativeFactory factory)
    {
        return new GazeService(
            NewProxy<IGPoseService>(),
            NewProxy<ICameraService>(),
            NewProxy<IObjectTable>(),
            factory.EventBus,
            NewProxy<ISigScanner>(),
            NewProxy<IGameInteropProvider>(),
            NewProxy<IPluginLog>(),
            framework: null, // null passes OnOwnerThread (ActorSpawnService shape)
            factory);
    }

    /// <summary>
    /// A resolvable three-actor GPose scene: the source plus two candidate
    /// targets. Addresses are real zeroed allocations, because the service
    /// reads the native GameObject for its Position/Rotation seeds; every
    /// native CALL goes through the injected factory instead.
    /// </summary>
    private sealed class GazeScene : IDisposable
    {
        public const ulong ActorId = 0x1001;
        public const ulong TargetId = 0x1002;
        public const ulong SecondId = 0x1003;

        private readonly List<nint> _blocks = new();

        /// <summary>The SAME dictionary instance the object-table proxy reads,
        /// so removing a row here is what the service observes.</summary>
        private Dictionary<ulong, FakeGameObject> _table = new();

        public required GazeService Service { get; init; }
        public required TestNativeFactory Factory { get; init; }
        public required IActor Actor { get; init; }
        public required IActor Target { get; init; }
        public required IActor Second { get; init; }

        public static GazeScene Create()
        {
            var factory = new TestNativeFactory();
            var scene = new GazeSceneBuilder(factory);
            return scene.Build();
        }

        internal static GazeScene From(
            GazeService service,
            TestNativeFactory factory,
            List<nint> blocks,
            Dictionary<ulong, FakeGameObject> table,
            IActor actor,
            IActor target,
            IActor second)
        {
            var scene = new GazeScene
            {
                Service = service,
                Factory = factory,
                Actor = actor,
                Target = target,
                Second = second,
            };
            scene._blocks.AddRange(blocks);
            scene._table = table;
            return scene;
        }

        /// <summary>The channels the detour would enforce on its next pass.</summary>
        public GazeTargetType Written() => Service.WrittenParts(ActorId);

        /// <summary>Removes the chosen target from the object table and runs the
        /// reconciliation pass, exactly as a despawn does.</summary>
        public void DespawnTarget()
        {
            _table.Remove(TargetId);
            Factory.EventBus.Publish(new ActorListChangedEvent(Array.Empty<IActor>()));
        }

        public void Dispose()
        {
            Service.Dispose();
            foreach (var block in _blocks)
                Marshal.FreeHGlobal(block);
            _blocks.Clear();
        }
    }

    /// <summary>Builds the scene's proxies; separated so the shared table
    /// instance is captured by the object-table proxy before construction.</summary>
    private sealed class GazeSceneBuilder(TestNativeFactory factory)
    {
        public GazeScene Build()
        {
            var blocks = new List<nint>();
            var table = new Dictionary<ulong, FakeGameObject>();
            var byAddress = new Dictionary<nint, FakeGameObject>();

            IActor Add(ulong id, ushort index)
            {
                // Zeroed native storage: the service reads GameObject
                // Position/Rotation for its Position/Forward seeds.
                var block = Marshal.AllocHGlobal(0x2000);
                for (int i = 0; i < 0x2000; i++)
                    Marshal.WriteByte(block, i, 0);
                blocks.Add(block);

                var obj = new FakeGameObject(id, index, block);
                table[id] = obj;
                byAddress[block] = obj;

                var actor = NewProxy<IActor>();
                ((DefaultProxy)(object)actor).Overrides["get_Address"] = block;
                return actor;
            }

            var actor = Add(GazeScene.ActorId, 201);
            var target = Add(GazeScene.TargetId, 202);
            var second = Add(GazeScene.SecondId, 203);

            var objectTable = NewProxy<IObjectTable>();
            var proxy = (DefaultProxy)(object)objectTable;
            proxy.Handlers["CreateObjectReference"] = args =>
                args?[0] is nint address && byAddress.TryGetValue(address, out var found)
                    ? found.Wrapper
                    : null;
            proxy.Handlers["SearchById"] = args =>
                args?[0] is ulong id && table.TryGetValue(id, out var found)
                    ? found.Wrapper
                    : null;

            var service = new GazeService(
                NewProxy<IGPoseService>(),
                NewProxy<ICameraService>(),
                objectTable,
                factory.EventBus,
                NewProxy<ISigScanner>(),
                NewProxy<IGameInteropProvider>(),
                NewProxy<IPluginLog>(),
                framework: null,
                factory);

            return GazeScene.From(service, factory, blocks, table, actor, target, second);
        }
    }

    /// <summary>One object-table row: a stable id, a GPose object index and the
    /// address the service resolves it by.</summary>
    internal sealed class FakeGameObject
    {
        public FakeGameObject(ulong id, ushort index, nint address)
        {
            Wrapper = NewProxy<IGameObject>();
            var proxy = (DefaultProxy)(object)Wrapper;
            proxy.Overrides["get_GameObjectId"] = id;
            proxy.Overrides["get_ObjectIndex"] = index;
            proxy.Overrides["get_Address"] = address;
            proxy.Overrides["IsValid"] = true;
        }

        public IGameObject Wrapper { get; }
    }

    private static void AssertUnavailable(GazeService service, string detail)
    {
        Assert.False(service.IsAvailable);
        Assert.NotNull(service.UnavailableDetail);
        Assert.Contains(detail, service.UnavailableDetail!, StringComparison.OrdinalIgnoreCase);
    }

    private static T NewProxy<T>() where T : class =>
        DispatchProxy.Create<T, DefaultProxy>();

    public class DefaultProxy : DispatchProxy
    {
        /// <summary>Constant answers by member name (property getters included
        /// as get_Xxx).</summary>
        public Dictionary<string, object?> Overrides { get; } = new();

        /// <summary>Answers computed from the call's arguments.</summary>
        public Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is { } name)
            {
                if (Overrides.TryGetValue(name, out var constant))
                    return constant;
                if (Handlers.TryGetValue(name, out var handler))
                    return handler(args);
            }
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType is { IsValueType: true } type)
                return Activator.CreateInstance(type);
            return null;
        }
    }

    internal sealed class TestNativeFactory : IGazeNativeFactory
    {
        public Func<nint>? UpdateScan { get; init; }
        public Func<nint>? LoopScan { get; init; }
        public Func<nint, GazeLoopDelegate, IGazeHook>? CreateHook { get; init; }
        public TestHook? Hook { get; init; }
        public TestEventBus EventBus { get; } = new();
        public List<IGazeHook> Hooks { get; } = new();
        public int HookCreateCount { get; private set; }
        public int EventBusSubscriptions => EventBus.SubscribedCount;
        public int EventBusUnsubscriptions => EventBus.UnsubscribedCount;

        /// <summary>Every character-target-id write, in order — the observable
        /// form of Brio's set-at-:201 / clear-to-0-at-:218 pair.</summary>
        public List<(nint Address, ulong TargetId)> TargetWrites { get; } = new();

        public ulong[] WrittenTargetIds() =>
            TargetWrites.ConvertAll(write => write.TargetId).ToArray();

        public void SetCharacterTargetId(nint characterAddress, ulong targetId) =>
            TargetWrites.Add((characterAddress, targetId));

        public nint ScanUpdateLookAt(ISigScanner scanner) =>
            UpdateScan?.Invoke() ?? (nint)1;

        public nint ScanActorLookAtLoop(ISigScanner scanner) =>
            LoopScan?.Invoke() ?? (nint)2;

        public IGazeHook CreateActorLookAtHook(
            IGameInteropProvider hooks,
            nint address,
            GazeLoopDelegate detour)
        {
            HookCreateCount++;
            if (CreateHook is { } create)
                return create(address, detour);
            var hook = Hook ?? new TestHook();
            Hooks.Add(hook);
            return hook;
        }
    }

    internal sealed class TestHook : IGazeHook
    {
        public bool EnableFailure { get; init; }
        public int EnableCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Enable()
        {
            EnableCount++;
            if (EnableFailure)
                throw new InvalidOperationException("enable");
        }

        public unsafe nint Original(ContainerInterface* args) => 0;

        public void Dispose() => DisposeCount++;
    }

    internal sealed class TestEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public int SubscribedCount { get; private set; }
        public int UnsubscribedCount { get; private set; }
        public int PublishedCount { get; private set; }

        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            SubscribedCount++;
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            UnsubscribedCount++;
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        // Real dispatch: the reconciliation pass is only reachable by actually
        // delivering ActorListChangedEvent.
        public void Publish<T>(T evt) where T : IEvent
        {
            PublishedCount++;
            if (!_handlers.TryGetValue(typeof(T), out var list))
                return;
            foreach (var handler in list.ToArray())
                ((Action<T>)handler)(evt);
        }

        public void Dispose() { }
    }
}
