using Poser.Domain.Actors;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Core;
using Poser.Entities;
using Poser.Game;
using Poser.Services;
using Poser.Application.Transforms;
using Poser.Game.Journal;
using Poser.Game.Scene;
using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Files;
using System.Numerics;
using System.Reflection;
using Poser.Application.Lifecycle;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class WorldActorDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gpose_exit_restores_tint_after_capture_before_binding_removal(bool unload)
    {
        var framework = new FakeFramework();
        var inGpose = true;
        var client = DispatchProxy.Create<IClientState, ExitProxy>();
        ((ExitProxy)(object)client).Handle = name => name == "get_IsGPosing" ? inGpose : null;
        var log = DispatchProxy.Create<IPluginLog, ExitProxy>();
        var bus = new EventBus(log);
        var id = new ActorId(Guid.NewGuid(), 1);
        var incoming = new Vector4(0.8f, 0.7f, 0.6f, 1f);
        var authored = new Vector4(1f, 0.2f, 0.3f, 1f);
        var port = new TintPort { Tint = incoming };
        var presentation = new ActorPresentationSession(port);
        var order = new List<string>();
        Vector4? captured = null;
        Vector4? atRemoval = null;
        var lifecycle = new SessionLifecycleCoordinator(new ExitCapture(() =>
        {
            captured = port.Tint;
            order.Add("capture");
        }));
        using var gpose = new GPoseService(client, framework, bus, log, lifecycle);

        // ActorManager subscribes first in production. Its state-change event
        // can remove the binding, but not before the separate restore phase.
        bus.Subscribe<GPoseStateChangedEvent>(evt =>
        {
            if (evt.IsGPosing) return;
            atRemoval = port.Tint;
            port.Supported = false;
            order.Add("remove");
        });
        bus.Subscribe<GPoseExitingEvent>(_ =>
        {
            presentation.ResetAll();
            order.Add("restore");
        });
        framework.RaiseUpdate();
        Assert.True(presentation.SetTint(id, PresentationModel.Character, authored).Success);
        if (unload)
            gpose.ExitForUnload();
        else
        {
            inGpose = false;
            framework.RaiseUpdate();
        }
        // Neither repeated observation nor the subsequent unload repeats exit.
        framework.RaiseUpdate();
        gpose.ExitForUnload();
        Assert.Equal(new[] { "capture", "restore", "remove" }, order);
        Assert.Equal(authored, captured);
        Assert.Equal(incoming, atRemoval);
        Assert.Equal(incoming, port.Tint);
        Assert.False(presentation.OverridesFor(id).HasAny);
    }

    private sealed class ExitCapture(Action capture) : IFinalCapturePort
    {
        public FinalCaptureResult CaptureForExit()
        {
            capture();
            return FinalCaptureResult.Captured(1);
        }
    }

    public class ExitProxy : DispatchProxy
    {
        public Func<string, object?>? Handle;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handle?.Invoke(method!.Name);
    }

    [Fact]
    public void Release_returns_native_tint_and_undo_restores_saved_pose_placement_and_tint()
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10);
        adapter.World.Add(observed);
        var actor = new ActorBase(ActorManager.ActorIdentity.For(observed.GameObjectId, observed.ObjectIndex),
            "Edited NPC", observed.Address);
        var manager = new FakeActorManager { Actors = [actor], Adopted = true };
        var id = new ActorId(Guid.NewGuid(), 1);
        var incomingTint = new Vector4(0.8f, 0.7f, 0.6f, 1f);
        var authoredTint = new Vector4(1f, 0.2f, 0.3f, 1f);
        var port = new TintPort { Tint = incomingTint };
        var presentation = new ActorPresentationSession(port);
        Assert.True(presentation.SetTint(id, PresentationModel.Character, authoredTint).Success);
        var pose = new PoseFile();
        pose.Bones["j_te_l"] = new PoseFile.BoneData
        {
            Position = new Vector3(1, 2, 3),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f),
            Scale = Vector3.One,
        };
        var authored = new ActorState(new Transform(new Vector3(10, 20, 30), Quaternion.Identity, new Vector3(2)), true, pose);
        var released = new ActorState(Transform.Identity, true, null);
        var state = new StatePort { Current = authored };
        var seam = new CloneSeam
        {
            Result = actor,
            OnInvoke = () => { manager.Adopted = true; id = id with { Generation = id.Generation + 1 }; },
        };
        var history = new TransformHistory();
        int appends = 0;
        history.Appended += _ => appends++;
        var session = new WorldActorSession(NewDiscovery(adapter, seam, manager: manager), history,
            _ =>
            {
                Assert.Equal(incomingTint, port.Tint); // Reset happens before the binding disappears.
                manager.Adopted = false;
                state.Current = released;
                return true;
            }, state, presentation, _ => manager.Adopted ? id : null);

        Assert.True(session.Release(actor));
        Assert.False(presentation.OverridesFor(id).HasAny);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        state.Pump();
        Assert.Equal(authored, state.Current);
        Assert.Equal(pose.Bones["j_te_l"].Rotation, state.Current.Pose!.Bones["j_te_l"].Rotation);
        Assert.Equal(authoredTint, port.Tint);
        Assert.Equal(incomingTint, presentation.OverridesFor(id).TintCaptures[PresentationModel.Character]);
        Assert.True(step.Redo());
        Assert.Equal(released, state.Current);
        Assert.Equal(incomingTint, port.Tint);

        Assert.True(step.Undo());
        Assert.True(step.Redo()); // Release again before the queued restore runs.
        state.Pump();
        Assert.Equal(released, state.Current);
        Assert.Equal(incomingTint, port.Tint);
        Assert.True(step.Undo());
        state.Pump();
        Assert.Equal(authored, state.Current); // Never recapture the temporary unposed state.
        Assert.Equal(authoredTint, port.Tint);
        Assert.Equal(1, appends);
    }

    private sealed class StatePort : IActorLifecycle
    {
        public ActorState Current;
        private readonly Queue<Action> _pending = new();
        public void Pump() { while (_pending.TryDequeue(out var action)) action(); }
        public ActorState Read(object actor) => Current;
        public void Restore(object actor, ActorState state, Func<bool>? stillCurrent = null) =>
            _pending.Enqueue(() => { if (stillCurrent?.Invoke() != false) Current = state; });
        public void WhenPosable(object actor, Action<object> act) => _pending.Enqueue(() => act(actor));
        public string GetName(object actor) => ((IActor)actor).Name;
        public void SetName(object actor, string name) => ((IActor)actor).Name = name;
        public void NameCreated(object actor, string seed) => throw new NotSupportedException();
        public bool IsSpawned(object actor) => false;
        public bool Destroy(object actor) => throw new NotSupportedException();
        public void Note(string detail) => throw new InvalidOperationException(detail);
    }

    private sealed class TintPort : IPresentationRuntimePort
    {
        public Vector4 Tint;
        public bool Supported = true;
        public bool IsSupported(ActorId actor) => Supported;
        public PresentationReading? Read(ActorId actor) => Supported ? new(1, Tint, null, null, default) : null;
        public PresentationPortResult SetTint(ActorId actor, PresentationModel model, Vector4 value)
        { Tint = value; return PresentationPortResult.Ok(); }
        public PresentationPortResult RestoreTint(ActorId actor, PresentationModel model, Vector4 value)
        {
            if (!Supported) return PresentationPortResult.Fail("Actor binding was removed.");
            Tint = value;
            return PresentationPortResult.Ok();
        }
        public PresentationPortResult SetOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult RestoreOpacity(ActorId actor, float value) => PresentationPortResult.Ok();
        public PresentationPortResult SetWetness(ActorId actor, WetnessState value) => PresentationPortResult.Ok();
        public PresentationPortResult ClearWetness(ActorId actor, WetnessState value) => PresentationPortResult.Ok();
        public void ClearOwned(ActorId actor) { }
    }

    [Fact]
    public void Release_history_reacquires_exact_actor_and_never_adopts_a_reused_address()
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10);
        adapter.World.Add(observed);
        var first = new ActorBase(ActorManager.ActorIdentity.For(observed.GameObjectId, observed.ObjectIndex),
            "Borrowed", observed.Address);
        var second = new ActorBase(first.Id, "Borrowed", observed.Address);
        var seam = new CloneSeam { Result = second };
        var manager = new FakeActorManager { Actors = [first], Adopted = true };
        var history = new TransformHistory();
        var released = new List<IActor>();
        var session = new WorldActorSession(NewDiscovery(adapter, seam, manager: manager), history,
            actor => { released.Add(actor); return true; });
        Assert.True(session.Release(first));
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.True(step.Redo());
        Assert.Equal(new IActor[] { first, second }, released);
        adapter.World[0] = observed with { GameObjectId = 77 };
        Assert.False(step.Undo());
        Assert.Single(seam.Calls);
        Assert.False(session.Release(first)); // A stale scene wrapper cannot authorize a fresh release either.
        Assert.Equal(2, released.Count);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("reused")]
    [InlineData("kind")]
    [InlineData("address")]
    [InlineData("index")]
    [InlineData("hidden")]
    public void Adoption_redo_refuses_changed_observation_without_native_calls(string change)
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10);
        adapter.World.Add(observed);
        var seam = new CloneSeam { Result = new ActorBase(new EntityId("borrowed"), "Borrowed", observed.Address) };
        var discovery = NewDiscovery(adapter, seam);
        var history = new TransformHistory();
        int releases = 0;
        var session = new WorldActorSession(discovery, history, _ => { releases++; return true; });
        Assert.True(session.Adopt(Assert.Single(discovery.RefreshCandidates()).Id, out _).Success);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        adapter.OnRevalidate = _ => change switch
        {
            "gone" => null,
            "reused" => observed with { GameObjectId = 77 },
            "address" => observed with { Address = (nint)0x20 },
            "index" => observed with { ObjectIndex = 6 },
            "hidden" => observed with { IsDrawing = false },
            _ => observed with { Kind = WorldActorKind.EventNpc },
        };
        Assert.False(step.Redo());
        Assert.False(step.Redo());
        Assert.Single(seam.Calls);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void Failed_adoption_does_not_append_and_failed_release_keeps_the_claim_for_retry()
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10);
        adapter.World.Add(observed);
        var seam = new CloneSeam { Throw = true };
        var discovery = NewDiscovery(adapter, seam);
        var history = new TransformHistory();
        int releases = 0;
        bool releaseAllowed = false;
        var session = new WorldActorSession(discovery, history, _ => { releases++; return releaseAllowed; });
        var candidate = Assert.Single(discovery.RefreshCandidates()).Id;
        Assert.False(session.Adopt(candidate, out _).Success);
        Assert.False(history.CanUndo);
        seam.Throw = false;
        seam.Result = new ActorBase(new EntityId("borrowed"), "Borrowed", observed.Address);
        Assert.True(session.Adopt(candidate, out _).Success);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        adapter.ThrowOnRevalidate = true;
        Assert.False(step.Undo());
        Assert.Equal(0, releases);
        adapter.ThrowOnRevalidate = false;
        Assert.False(step.Undo());
        releaseAllowed = true;
        Assert.True(step.Undo());
        Assert.True(step.Undo());
        Assert.Equal(2, releases);
        Assert.Equal(2, seam.Calls.Count); // No adoption as a failed-undo fallback.
    }

    [Fact]
    public void Adoption_history_survives_listing_refresh_and_releases_the_latest_wrapper()
    {
        var adapter = new FakeTableAdapter();
        var observed = Obs((nint)0x10);
        adapter.World.Add(observed);
        var first = new ActorBase(new EntityId("first"), "Borrowed", observed.Address);
        var second = new ActorBase(new EntityId("second"), "Borrowed", observed.Address);
        var seam = new CloneSeam { Result = first };
        var manager = new FakeActorManager();
        var discovery = NewDiscovery(adapter, seam, manager: manager);
        var history = new TransformHistory();
        var released = new List<IActor>();
        var session = new WorldActorSession(discovery, history, actor => { released.Add(actor); return true; });
        Assert.True(session.Adopt(Assert.Single(discovery.RefreshCandidates()).Id, out var actor).Success);
        Assert.Same(first, actor);
        manager.Actors = [first];
        Assert.Empty(discovery.RefreshCandidates()); // Held actors leave discovery; the claim must not.
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        manager.Actors = [];
        seam.Result = second;
        Assert.True(step.Redo());
        Assert.True(step.Undo());
        Assert.Equal(new IActor[] { first, second }, released);
        Assert.True(step.Redo());
        adapter.OnRevalidate = _ => observed with { GameObjectId = 77 };
        Assert.True(step.Undo());
        Assert.Equal(2, released.Count); // Undo must not release the replacement occupant either.
    }

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
        public Action? OnInvoke { get; set; }

        public IActor? Invoke(nint address)
        {
            Calls.Add(address);
            if (Throw)
                throw new InvalidOperationException("clone");
            OnInvoke?.Invoke();
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
        public bool Adopted { get; set; }
        public bool IsAdopted(IActor actor) => Adopted;
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
