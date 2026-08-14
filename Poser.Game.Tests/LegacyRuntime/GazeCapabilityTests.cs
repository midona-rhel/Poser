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

    // ── the 201-439 clone gate ───────────────────────────────────────────
    // A GPose clone SHARES its GameObjectId with the overworld original, so an
    // id never names a writable body on its own. Every native gaze write is
    // gated at one funnel, and the reconciliation pass resolves the clone by
    // scanning the GPose range instead of trusting SearchById, which scans from
    // index 0 and answers with the original.

    [Fact]
    public void Target_writes_land_on_the_gpose_clone_never_the_overworld_original()
    {
        using var scene = GazeScene.Create();

        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Head);

        // Guard against a vacuous pass: the harness must really place two
        // bodies under one id, and SearchById must really answer with the
        // overworld original, or none of this proves anything.
        Assert.NotEqual(scene.CloneAddress, scene.OriginalAddress);
        Assert.Equal(
            scene.OriginalAddress,
            scene.ObjectTable.SearchById(GazeScene.ActorId)!.Address);

        Assert.NotEmpty(scene.WrittenAddresses());
        Assert.All(scene.WrittenAddresses(), a => Assert.Equal(scene.CloneAddress, a));
        Assert.DoesNotContain(scene.OriginalAddress, scene.WrittenAddresses());
    }

    [Fact]
    public void A_despawned_target_clears_the_id_on_the_clone_not_the_overworld_original()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);

        // SearchById(ActorId) answers with the index-3 original here — taking
        // the write address from it would land SetTargetId on the real body.
        scene.DespawnTarget();

        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());
        Assert.All(scene.WrittenAddresses(), a => Assert.Equal(scene.CloneAddress, a));
    }

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
    public void A_stale_pass_leaves_the_locks_of_a_point_mode_entry_alone()
    {
        using var scene = GazeScene.Create();
        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        // The Actor target is now merely remembered — it governs nothing in
        // Point mode, so its despawn must not touch this lock.
        scene.Service.SetGazeMode(scene.Actor, GazeTargetMode.Position);
        scene.Service.SetPartLock(scene.Actor, GazeTargetType.Head, true);

        scene.DespawnTarget();

        Assert.True(scene.Service.IsPartLocked(scene.Actor, GazeTargetType.Head));
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
            IObjectTable objectTable)
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
            };
            scene._blocks.AddRange(blocks);
            scene._slots = slots;
            return scene;
        }

        /// <summary>The channels the detour would enforce on its next pass.</summary>
        public GazeTargetType Written() => Service.WrittenParts(ActorId);

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
                target.Address, objectTable);
        }
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
