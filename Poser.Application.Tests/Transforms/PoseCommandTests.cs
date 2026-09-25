using System.Numerics;
using Poser.Documents.Mcdf;
using System.Reflection;
using Poser.Application.Animation;
using Poser.Application.Gaze;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Presentation;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Transforms;

public sealed class PoseCommandTests
{
    [Fact]
    public void Reset_refuses_before_mutation_if_complete_capture_fails()
    {
        using var f = new Fixture { FailCapture = true };
        var original = f.Live.ToDictionary();
        Assert.False(f.ResetAll.ResetAll(f.Actor).Success);
        Assert.Equal(original, f.Live);
        Assert.Equal(0, f.IkClears);
        Assert.False(f.History.CanUndo);
    }

    [Fact]
    public void Deferred_restore_keeps_history_until_completion_and_failure_is_retryable()
    {
        using var f = new Fixture { DeferRestore = true };
        Assert.True(f.ResetAll.ResetAll(f.Actor).Success);
        var reset = f.History.PeekUndo();
        Assert.True(f.Journal.Undo().Success);
        Assert.True(f.Journal.IsRestoring);
        Assert.Same(reset, f.History.PeekUndo());
        Assert.False(f.Journal.Redo().Success);
        f.FinishRestore!(GestureResult.Fail("The actor redraw failed; retry after it is ready."));
        Assert.False(f.Journal.IsRestoring);
        Assert.Same(reset, f.History.PeekUndo());
        Assert.True(f.Journal.Undo().Success);
        f.FinishRestore!(GestureResult.Ok());
        Assert.Same(reset, f.History.PeekRedo());
    }

    [Fact]
    public void Clearing_history_cancels_deferred_restore_and_ignores_late_completion()
    {
        using var f = new Fixture { DeferRestore = true };
        Assert.True(f.ResetAll.ResetAll(f.Actor).Success);
        Assert.True(f.Journal.Undo().Success);
        f.History.Clear();
        Assert.True(f.RestoreCancellation.IsCancellationRequested);
        f.FinishRestore!(GestureResult.Ok());
        Assert.False(f.Journal.IsRestoring);
        Assert.False(f.History.CanUndo);
        Assert.False(f.History.CanRedo);
    }

    [Fact]
    public void Whole_actor_reset_is_one_step_and_redo_resets_pose_inside_the_history_transition()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        var older = new JournalStep("earlier edit", () => true, () => true);
        f.History.Append(older);
        Assert.True(f.ResetAll.ResetAll(f.Actor).Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Empty(f.Live[f.Weapon].Pose.Layers);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(original[f.Character], f.Live[f.Character]);
        Assert.Same(older, f.History.PeekUndo());
        Assert.True(f.Journal.Redo().Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Empty(f.Live[f.Weapon].Pose.Layers);
        Assert.Equal(2, f.IkClears);
        Assert.True(f.Journal.Undo().Success);
        Assert.Same(older, f.History.PeekUndo());
    }

    [Fact]
    public void Whole_actor_reset_reports_partial_failure_but_cleans_remaining_state_and_never_replays_on_replacement()
    {
        using var f = new Fixture { FailExpression = true };
        var original = f.Live[f.Character];
        var result = f.ResetAll.ResetAll(f.Actor);
        Assert.False(result.Success);
        Assert.Contains("Expression", result.Detail);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Equal(1, f.IkClears);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(original, f.Live[f.Character]);
        var replacement = f.Actor with { Generation = f.Actor.Generation + 1 };
        Assert.True(f.Scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(replacement, "replacement", [])], [], [], [])).Accepted);
        f.Captures = 0;
        Assert.False(f.Journal.Redo().Success);
        Assert.False(f.ResetAll.ResetAll(f.Actor).Success);
        Assert.Equal(0, f.Captures);
        Assert.Equal(1, f.IkClears);
    }

    [Fact]
    public void Region_reset_leaves_auxiliary_pose_and_actor_placement_alone_and_undo_restores_edits()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.Body).Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Equal(original[f.Weapon], f.Live[f.Weapon]);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
        Assert.True(f.Gestures.Undo().Success);
        Assert.Equal(original[f.Character], f.Live[f.Character]);
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.Empty(f.Live[f.Character].Pose.Layers);
        Assert.Empty(f.Live[f.Weapon].Pose.Layers);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
    }

    [Fact]
    public void Stash_restores_all_bone_slots_without_model_transform_while_mirror_includes_facing()
    {
        using var f = new Fixture();
        var original = f.Live.ToDictionary();
        Assert.True(f.Commands.Stash(f.Actor, "source").Success);
        Assert.True(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.True(f.Commands.ApplyStash(f.Actor).Success);
        Assert.Equal(original[f.Character].Pose.Evaluate(), f.Live[f.Character].Pose.Evaluate());
        Assert.Equal(original[f.Weapon].Pose.Evaluate(), f.Live[f.Weapon].Pose.Evaluate());
        Assert.Equal(original[f.Model], f.Live[f.Model]);
        Assert.True(f.Commands.Mirror(f.Actor).Success);
        var mirror = Assert.IsType<TransformPatch>(f.History.PeekUndo());
        Assert.Contains(mirror.After, state => state.Target == f.Model);
        Assert.Equal(original[f.Model].Transform.Position, f.Live[f.Model].Transform.Position);
        Assert.True(f.Gestures.Undo().Success);
        Assert.Equal(original[f.Model], f.Live[f.Model]);
    }

    [Fact]
    public void Old_actor_and_bone_commands_do_not_redirect_to_new_generation_or_overwrite_stash()
    {
        using var f = new Fixture();
        Assert.True(f.Commands.Stash(f.Actor, "kept").Success);
        var replacement = f.Actor with { Generation = f.Actor.Generation + 1 };
        Assert.True(f.Scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(replacement, "replacement", [])], [], [], [])).Accepted);
        f.Captures = 0;
        Assert.False(f.Commands.Reset(f.Actor, PoseRegion.All).Success);
        Assert.False(f.Commands.Mirror(f.Actor).Success);
        Assert.False(f.Commands.Stash(f.Actor, "lost").Success);
        Assert.False(f.Commands.ApplyStash(f.Actor).Success);
        Assert.False(f.Commands.ResetBone(f.Character, "old bone").Success);
        Assert.False(f.Commands.HasAuthoredEdits(f.Actor));
        Assert.Equal("kept", f.Commands.StashedFrom);
        Assert.Equal(0, f.Captures);
        Assert.False(f.History.CanUndo);
    }

    private sealed class Fixture : ITransformRuntimePort, IPoseEditReads,
        IActorPoseResetRuntime, IPoseSnapshotPort, IActorStateSnapshots, IActorStateKeySource, IDisposable
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly SceneSession Scene = new(new SelectionSession());
        public readonly TransformHistory History = new();
        public readonly Dictionary<TransformTargetId, TransformTargetState> Live = new();
        public readonly TransformTargetId Character, Weapon, Model;
        public readonly TransformGestureService Gestures;
        public readonly UndoJournal Journal;
        public readonly IPoseCommands Commands;
        public readonly IActorResetControl ResetAll;
        private readonly ActorIntegrationSession _integration;
        public int Captures;
        public int IkClears;
        public bool FailExpression;
        public bool FailCapture, DeferRestore;
        public Action<GestureResult>? FinishRestore;
        public CancellationToken RestoreCancellation;

        public Fixture()
        {
            var body = new BoneId(new SkeletonId(Actor, PoseSlot.Character, 0), 0, 0, "j_ude_a_l");
            var weapon = new BoneId(new SkeletonId(Actor, PoseSlot.MainHand, 0), 0, 0, "j_ude_a_l");
            Character = TransformTargetId.ForBone(body);
            Weapon = TransformTargetId.ForBone(weapon);
            Model = TransformTargetId.ForActor(Actor);
            Assert.True(Scene.TryRefresh(new SceneSnapshot(1,
                [new ActorDescriptor(Actor, "actor", [
                    new(body.Skeleton, [new(body, "arm", null)]),
                    new(weapon.Skeleton, [new(weapon, "weapon", null)])])], [], [], [])).Accepted);
            foreach (var target in new[] { Character, Weapon, Model })
                Live[target] = new(target,
                    new(new Vector3(4, 5, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), Vector3.One),
                    new BonePose([new(new(PoseLayerKind.Manual, "manual"), TransformComponents.All,
                        new(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), Vector3.Zero))]), true);
            Gestures = new(Scene, this, History);
            Journal = new(History, Gestures, this, new(() => this), _ => true, _ => { });
            var edits = new PoseEditService(Scene, this, History, Gestures);
            Commands = new PoseCommands(Scene, edits, new(edits), this);
            _integration = new(Idle<IIntegrationRuntimePort>(), Idle<IMcdfFileBoundary>(),
                Idle<ISessionGenerationSource>());
            ResetAll = new ActorResetControl(Scene, Gestures, edits, this,
                Idle<IGazeRuntimePort>(), new AnimationSession(Idle<IAnimationRuntimePort>()),
                new ActorPresentationSession(Idle<IPresentationRuntimePort>()), _integration,
                History, new ValueJournal(History), this);
        }

        public PoseEditResult CanReset(ActorId actor) => PoseEditResult.Ok(0);
        public IntegrationValue<ActorStateSnapshot> Capture(ActorId actor) =>
            FailCapture ? IntegrationValue<ActorStateSnapshot>.Fail("Appearance capture failed")
                : IntegrationValue<ActorStateSnapshot>.Ok(new(actor, SessionGeneration.New(), Capture(actor.LogicalId)!, null!));
        public void Restore(ActorStateSnapshot snapshot, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed)
        {
            if (DeferRestore) { FinishRestore = completed; RestoreCancellation = cancellation; return; }
            if (!current()) { completed(GestureResult.Fail("Stale history")); return; }
            Restore(snapshot.Pose, ok => completed(ok ? GestureResult.Ok() : GestureResult.Fail("Restore failed")));
        }
        public void WaitForReset(ActorId actor, Func<bool> current, CancellationToken cancellation,
            Action<GestureResult> completed) => completed(current() ? GestureResult.Ok() : GestureResult.Fail("Stale history"));
        public ActorStateKey? Current(Guid lineage) => null;
        public PoseEditResult ResetExpression(ActorId actor) => FailExpression
            ? throw new InvalidOperationException("native reset failed") : PoseEditResult.Ok(1);
        public PoseEditResult ClearIk(ActorId actor) { IkClears++; return PoseEditResult.Ok(1); }
        public ActorSnapshot? Capture(Guid lineage) => new(lineage,
            Live.Values.Where(s => s.Target.Bone != null).ToArray(), []);
        public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
        {
            foreach (var state in (TransformTargetState[])snapshot.Pose) Live[state.Target] = state;
            finished(true);
            return true;
        }

        public bool HasAuthoredEdits(ActorId actor) => true;
        public TransformPortResult Capture(TransformTargetId target)
        {
            Captures++;
            return TransformPortResult.Ok(Live[target]);
        }
        public TransformPortResult Restore(TransformTargetState state)
        {
            Live[state.Target] = state;
            return TransformPortResult.Ok(state);
        }
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired,
            bool rawBaseline = false) => throw new NotSupportedException();
        public void Dispose() { Gestures.Dispose(); _integration.Dispose(); }
    }

    private static T Idle<T>() where T : class => DispatchProxy.Create<T, IdleResetPort>();

    // These sessions own no overrides in this fixture. Only their no-op reset
    // mechanisms may run; an unexpected read or mutation fails the test.
    public class IdleResetPort : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "Reset" => GazeResult.Ok(),
            "ClearLoops" or "SuspendColors" or "ClearOwned" => null,
            _ => throw new InvalidOperationException($"Unexpected reset mechanism: {method.Name}"),
        };
    }
}
