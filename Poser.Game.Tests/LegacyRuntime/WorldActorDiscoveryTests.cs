using Poser.Domain.Actors;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Core;
using Poser.Entities;
using Poser.Game;
using Poser.Services;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class WorldActorDiscoveryTests
{
    [Fact]
    public void Refresh_filters_and_mints_stale_safe_candidate_ids()
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10, index: 5, name: "Near", distance: 2);
        adapter.World.AddRange([observed, Obs((nint)0x20, index: 6, name: "Far", distance: 9, kind: WorldActorKind.Player), Obs(nint.Zero), Obs((nint)0x30, index: 200)]);
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam);
        var first = discovery.RefreshCandidates();
        // Another player's character is never lent: "Far" is a Player that
        // is not the local player, so only the NPC lists.
        Assert.Single(first);
        Assert.Equal("Near", first[0].Name);
        var near = first[0];
        Assert.True(discovery.CloneCandidate(near.Id, out _).Success);
        adapter.World.Clear();
        adapter.World.Add(observed with { Address = (nint)0x11, GameObjectId = 2 });
        var replacement = Assert.Single(discovery.RefreshCandidates());
        Assert.NotEqual(near.Id, replacement.Id);
        Assert.Equal(WorldActorImportStatus.StaleCandidate, discovery.CloneCandidate(near.Id, out _).Status);
    }

    private static WorldActorObservation Obs(
        nint address,
        ushort index = 5,
        ulong id = 1,
        string name = "World Npc",
        WorldActorKind? kind = WorldActorKind.BattleNpc,
        float distance = 0f,
        bool drawing = true) =>
        new(new object(), address, index, id, name, kind, distance, drawing);

    private static WorldActorDiscovery NewDiscovery(
        FakeTableAdapter adapter,
        CloneSeam seam,
        FakeGPoseService? gpose = null,
        FakeActorManager? manager = null,
        FakeFramework? framework = null) =>
        new(
            adapter,
            gpose ?? new FakeGPoseService(),
            manager ?? new FakeActorManager(),
            seam.Invoke,
            framework);

    private sealed class FakeTableAdapter : IWorldActorTableAdapter
    {
        public List<WorldActorObservation> World { get; } = new();
        public Func<WorldActorObservation, WorldActorObservation?>? OnRevalidate { get; set; }
        public bool ThrowOnEnumerate { get; set; }
        public bool ThrowOnRevalidate { get; set; }

        public IReadOnlyList<WorldActorObservation> EnumerateOverworld()
        {
            if (ThrowOnEnumerate)
                throw new InvalidOperationException("enumerate");
            return World.ToArray();
        }

        /// <summary>Default revalidation resolves the stored candidate's own
        /// reference against the current world — the same "does this exact
        /// object still stand there" question the production adapter asks.</summary>
        public WorldActorObservation? Revalidate(WorldActorObservation stored)
        {
            if (ThrowOnRevalidate)
                throw new InvalidOperationException("revalidate");
            if (OnRevalidate is { } custom)
                return custom(stored);
            foreach (var current in World)
            {
                if (ReferenceEquals(current.Reference, stored.Reference))
                    return current;
            }
            return null;
        }
    }

    private sealed class CloneSeam
    {
        public List<nint> Calls { get; } = new();
        public IActor? Result { get; set; } =
            new ActorBase(new EntityId("world-clone"), "Clone", (nint)0xC10);
        public bool Throw { get; set; }

        public IActor? Invoke(nint address)
        {
            Calls.Add(address);
            if (Throw)
                throw new InvalidOperationException("clone");
            return Result;
        }
    }

    private sealed class FakeGPoseService : IGPoseService
    {
        public bool IsGPosing { get; set; } = true;
        public void Dispose() { }
        public void ExitForUnload() { }
    }

    private sealed class FakeActorManager : IActorManager
    {
        public IReadOnlyList<IActor> Actors { get; set; } = Array.Empty<IActor>();
        public IReadOnlyList<IActor> AuxiliaryActors { get; set; } =
            Array.Empty<IActor>();
        public void Dispose() { }
        public void RegisterAuxiliary(ushort objectIndex, ActorKind kind) { }
        public void UnregisterAuxiliary(ushort objectIndex) { }
        public void RefreshActors() { }
        public IActor? GetGPoseTarget() => null;
        public void SetGPoseTarget(IActor actor) { }
    }

    private sealed class FakeFramework : IFramework
    {
        public event IFramework.OnUpdateDelegate? Update;
        public bool InThread { get; set; } = true;
        public void RaiseUpdate() => Update?.Invoke(this);

        public DateTime LastUpdate => DateTime.MinValue;
        public DateTime LastUpdateUTC => DateTime.MinValue;
        public TimeSpan UpdateDelta => TimeSpan.Zero;
        public bool IsInFrameworkUpdateThread => InThread;
        public bool IsFrameworkUnloading => false;
        public TaskFactory GetTaskFactory() => throw new NotSupportedException();
        public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Run(Action action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Run(Func<Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Dalamud.Utility.IDebouncer CreateDebouncer(TimeSpan interval, Action action) =>
            throw new NotSupportedException();
        public Task RunOnFrameworkThread(Action action) =>
            throw new NotSupportedException();
        public Task<T> RunOnFrameworkThread<T>(Func<T> func) =>
            throw new NotSupportedException();
        public Task RunOnFrameworkThread(Func<Task> func) =>
            throw new NotSupportedException();
        public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> func) =>
            throw new NotSupportedException();
        public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
