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
    public void Enumeration_filters_dedupes_and_sorts_nearest_first()
    {
        var adapter = new FakeTableAdapter();
        var near = Obs((nint)0x10, index: 5, id: 1, name: "Near", distance: 2f);
        var far = Obs(
            (nint)0x20, index: 6, id: 2, name: "Far",
            kind: WorldActorKind.Player, distance: 9f);
        var pluginSpawn = Obs(
            (nint)0x30, index: 440, id: 3, name: "Plugin spawn", distance: 5f);
        adapter.World.AddRange(
        [
            far,
            near,
            pluginSpawn,
            // Filtered: no address, not drawing, unsupported kind.
            Obs(nint.Zero, index: 7, id: 4),
            Obs((nint)0x40, index: 8, id: 5, drawing: false),
            Obs((nint)0x50, index: 9, id: 6, kind: null),
            // Filtered: the protected 200–439 band, at both edges and inside.
            Obs((nint)0x60, index: 200, id: 7),
            Obs((nint)0x70, index: 201, id: 8),
            Obs((nint)0x80, index: 439, id: 9),
            // Filtered: duplicate address across enumerations, and Poser's
            // own auxiliary body.
            near with { DistanceFromPlayer = 99f },
            Obs((nint)0xAA, index: 441, id: 10, name: "Preview"),
        ]);
        var manager = new FakeActorManager
        {
            AuxiliaryActors =
                [new ActorBase(new EntityId("aux"), "Preview", (nint)0xAA)],
        };
        var discovery = NewDiscovery(adapter, new CloneSeam(), manager: manager);

        var candidates = discovery.RefreshCandidates();

        Assert.Equal(3, candidates.Count);
        Assert.Equal(["Near", "Plugin spawn", "Far"],
            candidates.Select(candidate => candidate.Name).ToArray());
        Assert.Equal(WorldActorKind.BattleNpc, candidates[0].Kind);
        Assert.Equal(WorldActorKind.Player, candidates[2].Kind);
        Assert.Equal(2f, candidates[0].DistanceFromPlayer);
        Assert.Equal(3, candidates.Select(candidate => candidate.Id).Distinct().Count());
    }

    [Fact]
    public void Enumeration_faults_yield_an_empty_listing()
    {
        var adapter = new FakeTableAdapter { ThrowOnEnumerate = true };
        var discovery = NewDiscovery(adapter, new CloneSeam());
        Assert.Empty(discovery.RefreshCandidates());
    }

    [Fact]
    public void A_refresh_mints_new_ids_and_old_ids_are_typed_stale_refusals()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam);

        var first = Assert.Single(discovery.RefreshCandidates());
        var second = Assert.Single(discovery.RefreshCandidates());
        Assert.NotEqual(first.Id, second.Id);

        var stale = discovery.CloneCandidate(first.Id, out var spawned);
        Assert.Equal(WorldActorImportStatus.StaleCandidate, stale.Status);
        Assert.Null(spawned);
        Assert.Empty(seam.Calls);

        // Positive control: the current listing's id clones.
        Assert.True(discovery.CloneCandidate(second.Id, out _).Success);
        Assert.Equal((nint)0x10, Assert.Single(seam.Calls));
    }

    [Fact]
    public void Clone_refuses_a_source_that_despawned_between_list_and_clone()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam);

        var candidate = Assert.Single(discovery.RefreshCandidates());
        adapter.World.Clear();

        var result = discovery.CloneCandidate(candidate.Id, out var spawned);
        Assert.Equal(WorldActorImportStatus.StaleCandidate, result.Status);
        Assert.Null(spawned);
        Assert.Empty(seam.Calls);
    }

    [Fact]
    public void Clone_refuses_any_exact_identity_drift_at_revalidation()
    {
        var stored = Obs((nint)0x10, index: 5, id: 1);
        WorldActorObservation[] drifted =
        [
            // Same slot, different occupant identity.
            stored with { GameObjectId = 2 },
            // Same object, different slot.
            stored with { ObjectIndex = 6 },
            // Same slot and id, different address.
            stored with { Address = (nint)0x11 },
            // Still there but no longer drawing.
            stored with { IsDrawing = false },
            // No longer a supported kind.
            stored with { Kind = null },
        ];

        foreach (var current in drifted)
        {
            var adapter = new FakeTableAdapter();
            adapter.World.Add(stored);
            var seam = new CloneSeam();
            var discovery = NewDiscovery(adapter, seam);
            var candidate = Assert.Single(discovery.RefreshCandidates());

            adapter.OnRevalidate = _ => current;
            var result = discovery.CloneCandidate(candidate.Id, out var spawned);

            Assert.Equal(WorldActorImportStatus.StaleCandidate, result.Status);
            Assert.Null(spawned);
            Assert.Empty(seam.Calls);
        }
    }

    [Fact]
    public void Revalidation_faults_are_stale_refusals_not_permission()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam);
        var candidate = Assert.Single(discovery.RefreshCandidates());

        adapter.ThrowOnRevalidate = true;
        var result = discovery.CloneCandidate(candidate.Id, out _);
        Assert.Equal(WorldActorImportStatus.StaleCandidate, result.Status);
        Assert.Empty(seam.Calls);
    }

    [Fact]
    public void A_proven_source_clones_through_the_seam_with_its_exact_address()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10, index: 5, id: 1, name: "Near"));
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam);
        var candidate = Assert.Single(discovery.RefreshCandidates());

        var result = discovery.CloneCandidate(candidate.Id, out var spawned);

        Assert.True(result.Success);
        Assert.Same(seam.Result, spawned);
        Assert.Equal((nint)0x10, Assert.Single(seam.Calls));
    }

    [Fact]
    public void A_failed_or_faulting_spawn_is_a_typed_spawn_failure()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var seam = new CloneSeam { Result = null };
        var discovery = NewDiscovery(adapter, seam);

        var candidate = Assert.Single(discovery.RefreshCandidates());
        var failed = discovery.CloneCandidate(candidate.Id, out var spawned);
        Assert.Equal(WorldActorImportStatus.SpawnFailed, failed.Status);
        Assert.Null(spawned);

        // The candidate stayed listed: a spawn failure is not staleness.
        seam.Throw = true;
        var faulted = discovery.CloneCandidate(candidate.Id, out _);
        Assert.Equal(WorldActorImportStatus.SpawnFailed, faulted.Status);
        Assert.Equal(2, seam.Calls.Count);
    }

    [Fact]
    public void Outside_gpose_listing_is_empty_and_clone_is_unavailable()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var gpose = new FakeGPoseService { IsGPosing = false };
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam, gpose: gpose);

        Assert.Empty(discovery.RefreshCandidates());

        // GPose exit MID-FLOW: listed while posing, cloned after leaving.
        gpose.IsGPosing = true;
        var candidate = Assert.Single(discovery.RefreshCandidates());
        gpose.IsGPosing = false;

        var result = discovery.CloneCandidate(candidate.Id, out var spawned);
        Assert.Equal(WorldActorImportStatus.Unavailable, result.Status);
        Assert.Null(spawned);
        Assert.Empty(seam.Calls);
    }

    [Fact]
    public void Off_the_framework_thread_listing_is_empty_and_clone_refuses()
    {
        var adapter = new FakeTableAdapter();
        adapter.World.Add(Obs((nint)0x10));
        var framework = new FakeFramework();
        var seam = new CloneSeam();
        var discovery = NewDiscovery(adapter, seam, framework: framework);

        var candidate = Assert.Single(discovery.RefreshCandidates());

        framework.InThread = false;
        Assert.Empty(discovery.RefreshCandidates());
        var result = discovery.CloneCandidate(candidate.Id, out _);
        Assert.Equal(WorldActorImportStatus.Unavailable, result.Status);
        Assert.Empty(seam.Calls);
    }

    // ── helpers ──────────────────────────────────────────────────────────

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
