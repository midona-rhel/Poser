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
    // loop runs unconditionally afterwards. Ceasing is not enough on its own:
    // _updateLookAt copies into the controller's persistent per-channel slot,
    // so a dropped channel is additionally owed ONE inactive write — Brio's
    // released value (StopLookAt, ActorLookAtService.cs:101-108, LookMode.None
    // on every part), which Ktisis calls GazeMode.Disabled.





    // ── the hand-back debt ───────────────────────────────────────────────

    [Fact]
    public void Untoggling_one_channel_owes_that_channel_a_hand_back()
    {
        using var scene = GazeScene.Create();
        // The reported case: an actor target is set, so every channel is
        // aiming at it, and only head is untoggled.
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        Assert.Equal(GazeTargetType.None, scene.Released());

        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Eyes | GazeTargetType.Body);

        Assert.Equal(GazeTargetType.Head, scene.Released());
        // The other two keep tracking, and the remembered target survives.
        Assert.Equal(GazeTargetType.Eyes | GazeTargetType.Body, scene.Written());
        Assert.Equal(GazeScene.TargetId, scene.Service.GetGazeState(scene.Actor).TargetId);
    }













    // ── target retention: Brio SetTargetType rewrites the mask and nothing
    // else (ActorLookAtService.cs:164-170), so TargetMode and the stored
    // LookAtSource survive an empty mask.







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

    // ── the 201-439 clone gate ───────────────────────────────────────────
    // A GPose clone SHARES its GameObjectId with the overworld original, so an
    // id never names a writable body on its own. Every native gaze write is
    // gated at one funnel, and the reconciliation pass resolves the clone by
    // scanning the GPose range instead of trusting SearchById, which scans from
    // index 0 and answers with the original.





    [Fact]
    public void An_actor_outside_the_gpose_range_is_never_written()
    {
        using var scene = GazeScene.Create();

        Assert.False(scene.Service.SetGazeTarget(scene.Ungated, scene.Target).Success);
        scene.Service.SetGazeMode(scene.Ungated, GazeTargetMode.Camera);
        scene.Service.SetGazeParts(scene.Ungated, GazeTargetType.None);
        scene.Service.SetGazeParts(scene.Ungated, GazeTargetType.All);
        scene.Service.SetGazeMode(scene.Ungated, GazeTargetMode.None);
        scene.Service.ResetGaze(scene.Ungated);
        scene.Reconcile();

        Assert.Empty(scene.Factory.TargetWrites);
    }

    // ── stale is sticky ──────────────────────────────────────────────────

    [Fact]
    public void A_target_returning_under_the_same_id_does_not_resume_by_itself()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.DespawnTarget();

        scene.RespawnTargetUnderTheSameId();

        Assert.True(scene.Service.GetGazeState(scene.Actor).TargetStale);
        Assert.Equal(GazeTargetType.None, scene.Written());
        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());
    }





    [Fact]
    public void Leaving_GPose_and_reset_then_dispose_release_everything_once()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.Factory.EventBus.Publish(new GPoseStateChangedEvent(false));

        Assert.Equal(new ulong[] { GazeScene.TargetId }, scene.Factory.WrittenTargetIds());
        Assert.Equal(GazeTargetType.None, scene.Released());

        scene.Service.ResetGaze(scene.Actor);
        Assert.Equal(GazeTargetMode.None, scene.Service.GetGazeState(scene.Actor).Mode);
        scene.Service.Dispose();
        scene.Service.Dispose();
        Assert.Equal(1, Assert.IsType<TestHook>(Assert.Single(scene.Factory.Hooks)).DisposeCount);
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
    /// A resolvable GPose scene, keyed by OBJECT INDEX because that is the
    /// distinction that matters: the source actor exists twice — as the
    /// overworld original at index 3 and as the GPose clone at index 201 —
    /// sharing one GameObjectId at two different addresses, which is the
    /// collision every native gaze write has to survive. Addresses are real
    /// zeroed allocations, because the service reads the native GameObject for
    /// its Position/Rotation seeds; every native CALL goes through the
    /// injected factory.
    /// </summary>
    private sealed class GazeScene : IDisposable
    {
        public const ulong ActorId = 0x1001;
        public const ulong TargetId = 0x1002;
        public const ulong SecondId = 0x1003;
        public const ulong UngatedId = 0x1004;

        public const int OriginalIndex = 3;   // overworld original of the source
        public const int UngatedIndex = 5;    // outside 201..439 entirely
        public const int CloneIndex = 201;
        public const int TargetIndex = 202;
        public const int SecondIndex = 203;

        private readonly List<nint> _blocks = new();

        /// <summary>The SAME dictionary instance the object-table proxy reads,
        /// so removing a row here is what the service observes.</summary>
        private Dictionary<int, FakeGameObject> _slots = new();

        public required GazeService Service { get; init; }
        public required TestNativeFactory Factory { get; init; }

        /// <summary>The source actor, addressed at its GPose CLONE.</summary>
        public required IActor Actor { get; init; }
        public required IActor Target { get; init; }
        public required IActor Second { get; init; }

        /// <summary>An actor outside the GPose index range — nothing may ever
        /// write to it.</summary>
        public required IActor Ungated { get; init; }

        /// <summary>The table itself, so a test can prove the collision trap is
        /// live rather than asserting against a harness that never had one.</summary>
        public required IObjectTable ObjectTable { get; init; }

        internal required SearchProbe Probe { get; init; }

        /// <summary>Runs on every <c>SearchById</c>, which is the one table read
        /// the reconciliation pass performs between its snapshot and its apply.
        /// Installing a handler here is how a test interleaves a caller with
        /// that pass on a harness that has no second thread.</summary>
        public Action<ulong>? OnSearchById
        {
            set => Probe.OnProbe = value;
        }

        public nint CloneAddress => _slots[CloneIndex].Address;
        public nint OriginalAddress => _slots[OriginalIndex].Address;

        public static GazeScene Create() =>
            new GazeSceneBuilder(new TestNativeFactory()).Build();

        internal static GazeScene From(
            GazeService service,
            TestNativeFactory factory,
            List<nint> blocks,
            Dictionary<int, FakeGameObject> slots,
            IActor actor,
            IActor target,
            IActor second,
            IActor ungated,
            nint targetBlock,
            IObjectTable objectTable,
            SearchProbe probe)
        {
            var scene = new GazeScene
            {
                Service = service,
                Factory = factory,
                Actor = actor,
                Target = target,
                Second = second,
                Ungated = ungated,
                TargetBlock = targetBlock,
                ObjectTable = objectTable,
                Probe = probe,
            };
            scene._blocks.AddRange(blocks);
            scene._slots = slots;
            return scene;
        }

        /// <summary>The channels the detour would enforce on its next pass.</summary>
        public GazeTargetType Written() => Service.WrittenParts(ActorId);

        /// <summary>The channels owed a one-shot inactive write on that same
        /// pass — the hand-back the detour is the only place to deliver.</summary>
        public GazeTargetType Released() => Service.PendingRelease(ActorId);

        /// <summary>Every address a character-target write landed on.</summary>
        public nint[] WrittenAddresses() =>
            Factory.TargetWrites.ConvertAll(write => write.Address).ToArray();

        public void Reconcile() =>
            Factory.EventBus.Publish(new ActorListChangedEvent(Array.Empty<IActor>()));

        /// <summary>Removes the chosen target and runs the reconciliation pass,
        /// exactly as a despawn does.</summary>
        public void DespawnTarget()
        {
            _slots.Remove(TargetIndex);
            Reconcile();
        }

        /// <summary>Puts a fresh object carrying the SAME GameObjectId back in
        /// the target slot — id reuse, which must not resume anything.</summary>
        public void RespawnTargetUnderTheSameId()
        {
            _slots[TargetIndex] = new FakeGameObject(TargetId, TargetIndex, TargetBlock);
            Reconcile();
        }

        internal nint TargetBlock { get; init; }

        public void Dispose()
        {
            Service.Dispose();
            foreach (var block in _blocks)
                Marshal.FreeHGlobal(block);
            _blocks.Clear();
        }
    }

    /// <summary>Builds the scene's proxies; separated so the shared slot table
    /// is captured by the object-table proxy before construction.</summary>
    private sealed class GazeSceneBuilder(TestNativeFactory factory)
    {
        public GazeScene Build()
        {
            var blocks = new List<nint>();
            var slots = new Dictionary<int, FakeGameObject>();
            var byAddress = new Dictionary<nint, FakeGameObject>();
            var probe = new SearchProbe();

            FakeGameObject Add(ulong id, int index)
            {
                // Zeroed native storage: the service reads GameObject
                // Position/Rotation for its Position/Forward seeds.
                var block = Marshal.AllocHGlobal(0x2000);
                for (int i = 0; i < 0x2000; i++)
                    Marshal.WriteByte(block, i, 0);
                blocks.Add(block);

                var obj = new FakeGameObject(id, index, block);
                slots[index] = obj;
                byAddress[block] = obj;
                return obj;
            }

            static IActor ActorAt(FakeGameObject obj)
            {
                var actor = NewProxy<IActor>();
                ((DefaultProxy)(object)actor).Overrides["get_Address"] = obj.Address;
                return actor;
            }

            // The source exists twice under ONE GameObjectId: the overworld
            // original first (lower index, so SearchById reaches it first) and
            // the GPose clone second.
            Add(GazeScene.ActorId, GazeScene.OriginalIndex);
            var clone = Add(GazeScene.ActorId, GazeScene.CloneIndex);
            var target = Add(GazeScene.TargetId, GazeScene.TargetIndex);
            var second = Add(GazeScene.SecondId, GazeScene.SecondIndex);
            var ungated = Add(GazeScene.UngatedId, GazeScene.UngatedIndex);

            var objectTable = NewProxy<IObjectTable>();
            var proxy = (DefaultProxy)(object)objectTable;
            proxy.Handlers["CreateObjectReference"] = args =>
                args?[0] is nint address && byAddress.TryGetValue(address, out var found)
                    ? found.Wrapper
                    : null;
            // Dalamud's SearchById scans from index 0, so a shared id answers
            // with the OVERWORLD ORIGINAL. Reproduced exactly, because the
            // service must never take a write address from it.
            proxy.Handlers["SearchById"] = args =>
            {
                if (args?[0] is not ulong id)
                    return null;
                probe.OnProbe?.Invoke(id);
                var indices = new List<int>(slots.Keys);
                indices.Sort();
                foreach (var index in indices)
                    if (slots[index].Id == id)
                        return slots[index].Wrapper;
                return null;
            };
            proxy.Handlers["get_Item"] = args =>
                args?[0] is int index && slots.TryGetValue(index, out var slot)
                    ? slot.Wrapper
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

            return GazeScene.From(
                service, factory, blocks, slots,
                ActorAt(clone), ActorAt(target), ActorAt(second), ActorAt(ungated),
                target.Address, objectTable, probe);
        }
    }

    /// <summary>A settable hook on the fake table's <c>SearchById</c>. Held in
    /// its own object because the object-table proxy captures it during
    /// construction, long before the scene a test can reach exists.</summary>
    internal sealed class SearchProbe
    {
        public Action<ulong>? OnProbe { get; set; }
    }

    /// <summary>One object-table row: a stable id, an object index and the
    /// address the service resolves it by. Two rows may share an id.</summary>
    internal sealed class FakeGameObject
    {
        public FakeGameObject(ulong id, int index, nint address)
        {
            Id = id;
            Address = address;
            Wrapper = NewProxy<IGameObject>();
            var proxy = (DefaultProxy)(object)Wrapper;
            proxy.Overrides["get_GameObjectId"] = id;
            proxy.Overrides["get_ObjectIndex"] = (ushort)index;
            proxy.Overrides["get_Address"] = address;
            proxy.Overrides["IsValid"] = true;
        }

        public ulong Id { get; }
        public nint Address { get; }
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
