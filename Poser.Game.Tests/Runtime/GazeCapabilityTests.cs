using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Game;

using Poser.Domain.Scene;

using Poser.Application.Viewport;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;
using Poser.Game.Services;
using StableActorId = Poser.Domain.Identity.ActorId;

namespace Poser.Game.Tests.Runtime;

public sealed class GazeCapabilityTests
{
    [Fact]
    public void Character_target_writes_use_the_gpose_clone_not_the_overworld_original()
    {
        using var scene = GazeScene.Create();

        scene.Service.SetGazeTarget(scene.Actor, scene.Target);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.None);
        scene.Service.SetGazeParts(scene.Actor, GazeTargetType.Head);

        Assert.NotEqual(scene.CloneAddress, scene.OriginalAddress);
        Assert.Equal(
            scene.OriginalAddress,
            scene.ObjectTable.SearchById(GazeScene.ActorId)!.Address);
        // Untoggling every channel clears the imposed id; retoggling restores it.
        Assert.Equal(
            new ulong[] { GazeScene.TargetId, 0, GazeScene.TargetId },
            scene.Factory.WrittenTargetIds());
        Assert.All(
            scene.WrittenAddresses(),
            address => Assert.Equal(scene.CloneAddress, address));
    }

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

        // A target returning under the same id does not resume by itself.
        scene.RespawnTargetUnderTheSameId();
        Assert.True(scene.Service.GetGazeState(scene.Actor).TargetStale);
        Assert.Equal(GazeTargetType.None, scene.Written());
        Assert.Equal(new ulong[] { GazeScene.TargetId, 0 }, scene.Factory.WrittenTargetIds());

        // Choosing a live target lifts the stale mark.
        Assert.True(scene.Service.SetGazeTarget(scene.Actor, scene.Second).Success);
        state = scene.Service.GetGazeState(scene.Actor);
        Assert.False(state.TargetStale);
        Assert.Equal(GazeScene.SecondId, state.TargetId);
        Assert.Equal(GazeTargetType.All, scene.Written());
    }

    [Fact]
    public void Two_clones_sharing_a_game_object_id_hold_independent_gaze()
    {
        using var scene = GazeScene.Create();

        // The twin carries the source's GameObjectId, as every player-seeded
        // spawn does; it is still another actor, so A -> B is accepted.
        Assert.True(scene.Service.SetGazeTarget(scene.Actor, scene.Twin).Success);
        Assert.True(scene.Service.SetGazeMode(scene.Twin, GazeTargetMode.Camera).Success);

        var actor = scene.Service.GetGazeState(scene.Actor);
        var twin = scene.Service.GetGazeState(scene.Twin);
        Assert.Equal(GazeTargetMode.Entity, actor.Mode);
        Assert.Equal<StableActorId?>(scene.TwinKey, actor.TargetActor);
        Assert.Equal(GazeTargetMode.Camera, twin.Mode);
        Assert.Null(twin.TargetActor);
        Assert.Equal(GazeTargetType.All, scene.Written());
        Assert.Equal(GazeTargetType.All, scene.Service.WrittenParts(scene.TwinKey));
        Assert.False(scene.Service.SetGazeTarget(scene.Actor, scene.Actor).Success);
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

        public const int OriginalIndex = 3;   // overworld original of the source
        public const int CloneIndex = 201;
        public const int TargetIndex = 202;
        public const int SecondIndex = 203;
        public const int TwinIndex = 204;    // a second clone of the source

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
        public required IActor Twin { get; init; }
        public required StableActorId ActorKey { get; init; }
        public required StableActorId TwinKey { get; init; }

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
            IActor twin,
            Func<IActor, StableActorId> key,
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
                Twin = twin,
                ActorKey = key(actor),
                TwinKey = key(twin),
                TargetBlock = targetBlock,
                ObjectTable = objectTable,
            };
            scene._blocks.AddRange(blocks);
            scene._slots = slots;
            return scene;
        }

        /// <summary>The channels the detour would enforce on its next pass.</summary>
        public GazeTargetType Written() => Service.WrittenParts(ActorKey);

        /// <summary>The channels owed a one-shot inactive write on that same
        /// pass — the hand-back the detour is the only place to deliver.</summary>
        public GazeTargetType Released() => Service.PendingRelease(ActorKey);

        /// <summary>Every address a character-target write landed on.</summary>
        public nint[] WrittenAddresses() =>
            Factory.TargetWrites.ConvertAll(write => write.Address).ToArray();

        /// <summary>The pass the scene lifecycle runs after a binding commit.</summary>
        public void Reconcile() => Service.Reconcile();

        /// <summary>Removes the chosen target and runs the reconciliation pass,
        /// exactly as a despawn does.</summary>
        public void DespawnTarget()
        {
            _slots.Remove(TargetIndex);
            Reconcile();
        }

        /// <summary>Puts a fresh object carrying the SAME GameObjectId back in
        /// the target slot — id reuse, which must not resume anything. The
        /// registry binds it under a new generation, so the old id stays gone.</summary>
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
            var twin = Add(GazeScene.ActorId, GazeScene.TwinIndex);

            // The registry: one stable id per actor, resolving only while the
            // exact object it was bound to still holds its slot.
            var bound = new Dictionary<StableActorId, (IActor Actor, FakeGameObject Body, int Index)>();
            var keys = new Dictionary<IActor, StableActorId>(ReferenceEqualityComparer.Instance);
            IActor Bound(FakeGameObject obj, int index)
            {
                var actor = ActorAt(obj);
                var id = StableActorId.New();
                bound[id] = (actor, obj, index);
                keys[actor] = id;
                return actor;
            }
            var bindings = NewProxy<IEntityBindings>();
            var bindingProxy = (DefaultProxy)(object)bindings;
            bindingProxy.Handlers["GetActorId"] = args =>
                args?[0] is IActor actor && keys.TryGetValue(actor, out var id) ? id : null;
            bindingProxy.Handlers["Resolve"] = args =>
                args?[0] is StableActorId id
                && bound.TryGetValue(id, out var entry)
                && slots.TryGetValue(entry.Index, out var live)
                && ReferenceEquals(live, entry.Body)
                    ? new BindingResult<IActor>(BindingStatus.Success, entry.Actor)
                    : new BindingResult<IActor>(BindingStatus.Missing);

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
                NewProxy<ICameraProjection>(),
                objectTable,
                factory.EventBus,
                NewProxy<ISigScanner>(),
                NewProxy<IGameInteropProvider>(),
                NewProxy<IPluginLog>(),
                framework: null,
                factory,
                bindings);

            return GazeScene.From(
                service, factory, blocks, slots,
                Bound(clone, GazeScene.CloneIndex), Bound(target, GazeScene.TargetIndex),
                Bound(second, GazeScene.SecondIndex), Bound(twin, GazeScene.TwinIndex),
                actor => keys[actor], target.Address, objectTable);
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
        public TestEventBus EventBus { get; } = new();
        public List<IGazeHook> Hooks { get; } = new();

        /// <summary>Every character-target-id write, in order — the observable
        /// form of Brio's set-at-:201 / clear-to-0-at-:218 pair.</summary>
        public List<(nint Address, ulong TargetId)> TargetWrites { get; } = new();

        public ulong[] WrittenTargetIds() =>
            TargetWrites.ConvertAll(write => write.TargetId).ToArray();

        public void SetCharacterTargetId(nint characterAddress, ulong targetId) =>
            TargetWrites.Add((characterAddress, targetId));

        public nint ScanUpdateLookAt(ISigScanner scanner) => 1;

        public nint ScanActorLookAtLoop(ISigScanner scanner) => 2;

        public IGazeHook CreateActorLookAtHook(
            IGameInteropProvider hooks,
            nint address,
            GazeLoopDelegate detour)
        {
            var hook = new TestHook();
            Hooks.Add(hook);
            return hook;
        }
    }

    internal sealed class TestHook : IGazeHook
    {
        public int DisposeCount { get; private set; }

        public void Enable() { }

        public unsafe nint Original(ContainerInterface* args) => 0;

        public void Dispose() => DisposeCount++;
    }

    internal sealed class TestEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        // Real dispatch: GPose exit is only reachable by actually delivering
        // GPoseStateChangedEvent.
        public void Publish<T>(T evt) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                return;
            foreach (var handler in list.ToArray())
                ((Action<T>)handler)(evt);
        }

        public void Dispose() { }
    }
}
