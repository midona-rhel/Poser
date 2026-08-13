using System.Numerics;
using Poser.ContractTests.Fixtures;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

public sealed class TransactionContractTests
{
    [Fact]
    public void Multi_target_apply_failure_restores_prior_writes_and_adds_no_history()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.BoneTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBoneScene(
            TestIds.Actor(),
            second.Bone!.Value));
        var firstInitial = TestStates.At(first, -4);
        var secondInitial = TestStates.At(second, 7, hasOverride: false);
        app.Runtime.Seed(firstInitial);
        app.Runtime.Seed(secondInitial);
        app.Runtime.FailApplyCall = 2;
        app.Runtime.FailureDetail = "native capability unavailable";
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;

        var result = app.Commands.SetAbsoluteMany(
            new[]
            {
                (first, TestStates.Translated(1)),
                (second, TestStates.Translated(2)),
            },
            "atomic edit");

        Assert.False(result.Success);
        Assert.Contains("native capability", result.Detail!);
        // Current production behavior restores every captured target when any
        // absolute write fails, including targets after the failing write.
        Assert.Equal(
            new[] { first, second },
            app.Runtime.RestoreCalls);
        Assert.Equal(firstInitial, app.Runtime.State(first));
        Assert.Equal(secondInitial, app.Runtime.State(second));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Final_capture_failure_restores_single_target_and_adds_no_history()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        var initial = TestStates.At(target, -9);
        app.Runtime.Seed(initial);
        app.Runtime.FailCaptureCall = 2;
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
        app.Runtime.FailureDetail = "native capability unavailable";

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(4),
            "capture failure");

        Assert.False(result.Success);
        Assert.Contains("native capability", result.Detail!);
        Assert.Equal(new[] { target }, app.Runtime.RestoreCalls);
        Assert.Equal(initial, app.Runtime.State(target));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Unavailable_runtime_is_refused_without_false_success()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(TestStates.For(target));
        app.Runtime.FailApplyCall = 1;
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
        app.Runtime.FailureDetail = "framework thread is unavailable";

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(3),
            "unavailable edit");

        Assert.False(result.Success);
        Assert.Contains("framework thread", result.Detail!);
        Assert.False(app.History.CanUndo);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
    }

    [Fact]
    public void Single_apply_failure_after_mutation_rolls_back_with_complete_receipt()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, -3));
        var initial = app.Runtime.State(target);
        app.Runtime.FailApplyCall = 1;
        app.Runtime.MutateBeforeApplyFailure = true;
        app.Runtime.ApplyFailureDetail = "apply mutated before failing";

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(8),
            "single recovery");

        Assert.False(result.Success);
        Assert.Contains("apply mutated", result.Detail!);
        var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.True(recovery.Complete);
        var attempt = Assert.Single(recovery.Attempts);
        Assert.Equal(initial, attempt.RequestedState);
        Assert.True(attempt.Success);
        Assert.Equal(initial, app.Runtime.State(target));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Final_capture_and_restore_failure_returns_retryable_partial_receipt()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, -9));
        var initial = app.Runtime.State(target);
        app.Runtime.FailCaptureCall = 2;
        app.Runtime.CaptureFailureDetail = "final capture unavailable";
        app.Runtime.FailRestoreCalls.Add(1);
        app.Runtime.RestoreFailureDetail = "baseline restore unavailable";
        app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;

        var result = app.Commands.SetAbsolute(
            target,
            TestStates.Translated(4),
            "capture recovery");

        Assert.False(result.Success);
        Assert.Contains("final capture unavailable", result.Detail!);
        Assert.Contains("Rollback also failed", result.Detail!);
        Assert.Contains("baseline restore unavailable", result.Detail!);
        var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.False(pending.Complete);
        Assert.Same(pending, app.Gestures.PendingRecovery);
        var failure = Assert.Single(pending.Failures);
        Assert.Equal(target, failure.RequestedState.Target);
        Assert.Equal(TransformPortStatus.NativeUnavailable, failure.Status);
        Assert.Equal(TestStates.Translated(4), app.Runtime.State(target).Transform);
        Assert.False(app.History.CanUndo);

        app.Runtime.FailRestoreCalls.Clear();
        var retried = app.Gestures.RetryRecovery(pending);

        Assert.True(retried.Success);
        Assert.True(Assert.IsType<TransformRecoveryReceipt>(retried.Recovery).Complete);
        Assert.Null(app.Gestures.PendingRecovery);
        Assert.Equal(initial, app.Runtime.State(target));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Multi_apply_partial_rollback_attempts_every_target_and_retries_exact_baselines()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.BoneTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBoneScene(
            TestIds.Actor(),
            second.Bone!.Value));
        var firstInitial = TestStates.At(first, -4);
        var secondInitial = TestStates.At(second, 7, hasOverride: false);
        app.Runtime.Seed(firstInitial);
        app.Runtime.Seed(secondInitial);
        app.Runtime.FailApplyCall = 2;
        app.Runtime.MutateBeforeApplyFailure = true;
        app.Runtime.ApplyFailureDetail = "second apply mutated";
        app.Runtime.FailRestoreCalls.Add(1);
        app.Runtime.RestoreFailureDetail = "first baseline stayed dirty";

        var result = app.Commands.SetAbsoluteMany(
            new[]
            {
                (first, TestStates.Translated(1)),
                (second, TestStates.Translated(2)),
            },
            "partial rollback");

        Assert.False(result.Success);
        var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.False(pending.Complete);
        Assert.Equal(2, pending.Attempts.Count);
        Assert.Single(pending.Failures);
        Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
        Assert.Equal(TestStates.Translated(1), app.Runtime.State(first).Transform);
        Assert.Equal(secondInitial, app.Runtime.State(second));
        Assert.False(app.History.CanUndo);

        app.Runtime.FailRestoreCalls.Clear();
        var retried = app.Gestures.RetryRecovery(pending);

        Assert.True(retried.Success);
        Assert.Equal(firstInitial, app.Runtime.State(first));
        Assert.Equal(secondInitial, app.Runtime.State(second));
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Clear_override_keeps_primary_failure_distinct_from_rollback_failure()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, 5));
        app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
        app.Runtime.MutateBeforeRestoreFailureCalls.Add(1);
        app.Runtime.RestoreFailureDetails[1] = "clear override write failed";
        app.Runtime.RestoreFailureDetails[2] = "rollback restore failed";

        var result = app.Commands.ClearActorOverrides(
            new[] { target },
            "clear override");

        Assert.False(result.Success);
        Assert.Contains("clear override write failed", result.Detail!);
        Assert.Contains("Rollback also failed", result.Detail!);
        Assert.Contains("rollback restore failed", result.Detail!);
        var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.False(pending.Complete);
        Assert.False(app.Runtime.State(target).HasOverride);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Gesture_update_reports_all_rollback_failures_and_clears_active_once()
    {
        var first = TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1);
        var second = TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2);
        using var app = BoneHarness(first, second);
        var begun = app.Gestures.Begin(Gesture(first, second));
        Assert.True(begun.Success);
        app.Runtime.FailApplyCall = 2;
        app.Runtime.MutateBeforeApplyFailure = true;
        app.Runtime.ApplyFailureDetail = "second gesture apply failed";
        app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
        app.Runtime.RestoreFailureDetail = "gesture rollback failed";

        var result = app.Gestures.Update(
            begun.GestureId!.Value,
            Move(3));

        Assert.False(result.Success);
        Assert.Contains("second gesture apply failed", result.Detail!);
        var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.Equal(2, pending.Attempts.Count);
        Assert.Equal(2, pending.Failures.Count);
        Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
        Assert.Null(app.Gestures.ActiveGesture);
        Assert.Same(pending, app.Gestures.PendingRecovery);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Gesture_commit_capture_failure_returns_partial_recovery_without_history()
    {
        var target = TestIds.BoneTarget();
        using var app = BoneHarness(target);
        var begun = app.Gestures.Begin(Gesture(target));
        Assert.True(begun.Success);
        Assert.True(app.Gestures.Update(begun.GestureId!.Value, Move(2)).Success);
        app.Runtime.FailCaptureCall = 2;
        app.Runtime.CaptureFailureDetail = "commit capture failed";
        app.Runtime.FailRestoreCalls.Add(1);
        app.Runtime.RestoreFailureDetail = "commit rollback failed";

        var result = app.Gestures.Commit(begun.GestureId.Value);

        Assert.False(result.Success);
        Assert.Contains("commit capture failed", result.Detail!);
        Assert.Contains("commit rollback failed", result.Detail!);
        Assert.False(Assert.IsType<TransformRecoveryReceipt>(result.Recovery).Complete);
        Assert.Null(app.Gestures.ActiveGesture);
        Assert.NotNull(app.Gestures.PendingRecovery);
        Assert.False(app.History.CanUndo);
    }

    [Fact]
    public void Cancel_returns_recovery_and_void_cancellation_paths_retain_pending_receipt()
    {
        using (var direct = BoneHarness(TestIds.BoneTarget()))
        {
            var begun = direct.Gestures.Begin(Gesture(TestIds.BoneTarget()));
            direct.Runtime.FailRestoreCalls.Add(1);
            var cancelled = direct.Gestures.Cancel(begun.GestureId!.Value);
            Assert.False(cancelled.Success);
            Assert.Same(cancelled.Recovery, direct.Gestures.PendingRecovery);
        }

        using (var selection = BoneHarness(TestIds.BoneTarget()))
        {
            Assert.True(selection.Gestures.Begin(Gesture(TestIds.BoneTarget())).Success);
            selection.Runtime.FailRestoreCalls.Add(1);
            selection.Selection.Select(SelectionId.ForActor(TestIds.Actor()));
            Assert.NotNull(selection.Gestures.PendingRecovery);
        }

        using (var reconcile = BoneHarness(TestIds.BoneTarget()))
        {
            Assert.True(reconcile.Gestures.Begin(Gesture(TestIds.BoneTarget())).Success);
            reconcile.Runtime.FailRestoreCalls.Add(1);
            reconcile.Gestures.ReconcileScene(_ => false);
            Assert.NotNull(reconcile.Gestures.PendingRecovery);
        }

        var disposed = BoneHarness(TestIds.BoneTarget());
        Assert.True(disposed.Gestures.Begin(Gesture(TestIds.BoneTarget())).Success);
        disposed.Runtime.FailRestoreCalls.Add(1);
        disposed.Dispose();
        Assert.NotNull(disposed.Gestures.PendingRecovery);
    }

    [Fact]
    public void Undo_partial_restore_requires_recovery_then_commits_same_patch_once()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, 0));
        Assert.True(app.Commands.SetAbsolute(
            target,
            TestStates.Translated(6),
            "undo recovery").Success);
        app.Runtime.FailRestoreCalls.Add(1);

        var failed = app.Gestures.Undo();

        var pending = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
        Assert.False(failed.Success);
        Assert.True(app.History.CanUndo);
        Assert.False(app.History.CanRedo);
        Assert.Same(pending, app.Gestures.PendingRecovery);

        app.Runtime.FailRestoreCalls.Clear();
        Assert.True(app.Gestures.RetryRecovery(pending).Success);
        Assert.True(app.History.CanUndo);
        Assert.False(app.History.CanRedo);
        Assert.True(app.Gestures.Undo().Success);
        Assert.False(app.History.CanUndo);
        Assert.True(app.History.CanRedo);
        Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
    }

    [Fact]
    public void Redo_partial_restore_requires_recovery_then_commits_same_patch_once()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, 0));
        Assert.True(app.Commands.SetAbsolute(
            target,
            TestStates.Translated(6),
            "redo recovery").Success);
        Assert.True(app.Gestures.Undo().Success);
        app.Runtime.FailRestoreCalls.Add(app.Runtime.RestoreCalls.Count + 1);

        var failed = app.Gestures.Redo();

        var pending = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
        Assert.False(failed.Success);
        Assert.False(app.History.CanUndo);
        Assert.True(app.History.CanRedo);

        app.Runtime.FailRestoreCalls.Clear();
        Assert.True(app.Gestures.RetryRecovery(pending).Success);
        Assert.False(app.History.CanUndo);
        Assert.True(app.History.CanRedo);
        Assert.True(app.Gestures.Redo().Success);
        Assert.True(app.History.CanUndo);
        Assert.False(app.History.CanRedo);
        Assert.Equal(TestStates.Translated(6), app.Runtime.State(target).Transform);
    }

    [Fact]
    public void Pose_partial_rollback_blocks_every_mutation_until_exact_recovery_succeeds()
    {
        var bone = TestIds.BoneTarget();
        using var app = BoneHarness(bone);
        var initial = app.Runtime.State(bone);
        var pose = Portable(bone, 4);
        app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
        app.Runtime.MutateBeforeRestoreFailureCalls.Add(1);
        app.Runtime.RestoreFailureDetails[1] = "pose write failed after mutation";
        app.Runtime.RestoreFailureDetails[2] = "pose rollback failed";

        var result = app.PoseEdits.ApplyPortable(
            new[] { bone },
            pose,
            "pose recovery");

        Assert.False(result.Success);
        Assert.Equal(0, result.Affected);
        Assert.Contains("pose write failed after mutation", result.Detail!);
        Assert.Contains("pose rollback failed", result.Detail!);
        var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.Same(pending, app.Gestures.PendingRecovery);
        Assert.True(app.Runtime.State(bone).HasOverride);
        Assert.False(app.History.CanUndo);
        var restoreCount = app.Runtime.RestoreCalls.Count;

        AssertBarrier(app.Gestures.Begin(Gesture(bone)), pending);
        AssertBarrier(app.Commands.SetAbsolute(
            TestIds.ActorTarget(), TestStates.Translated(1), "blocked"), pending);
        AssertBarrier(app.Commands.SetAbsoluteMany(
            new[] { (TestIds.ActorTarget(), TestStates.Translated(1)) }, "blocked"), pending);
        AssertBarrier(app.Commands.ClearActorOverrides(
            new[] { TestIds.ActorTarget() }, "blocked"), pending);
        AssertBarrier(app.PoseEdits.Reset(new[] { bone }, PoseRegion.All, "blocked"), pending);
        AssertBarrier(app.PoseEdits.Flip(bone, "blocked"), pending);
        AssertBarrier(app.PoseEdits.Mirror(new[] { bone }, "blocked"), pending);
        AssertBarrier(app.PoseEdits.ApplyPortable(new[] { bone }, pose, "blocked"), pending);
        AssertBarrier(app.Gestures.Undo(), pending);
        AssertBarrier(app.Gestures.Redo(), pending);
        Assert.Equal(restoreCount, app.Runtime.RestoreCalls.Count);

        app.Runtime.FailRestoreCalls.Clear();
        var recovered = app.Gestures.RetryRecovery(pending);

        Assert.True(recovered.Success);
        var receipt = Assert.IsType<TransformRecoveryReceipt>(recovered.Recovery);
        Assert.True(receipt.Complete);
        Assert.Empty(receipt.Failures);
        Assert.Null(recovered.Detail);
        Assert.Null(app.Gestures.PendingRecovery);
        Assert.Equal(initial, app.Runtime.State(bone));
        Assert.False(app.History.CanUndo);
    }

    private static TransformApplicationHarness ActorHarness(
        TransformTargetId target,
        TransformTargetState initial)
    {
        var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(target.Actor!.Value));
        app.Runtime.Seed(initial);
        return app;
    }

    private static TransformApplicationHarness BoneHarness(
        params TransformTargetId[] targets)
    {
        var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBonesScene(
            TestIds.Actor(),
            targets.Select(target => target.Bone!.Value).ToArray()));
        foreach (var target in targets)
            app.Runtime.Seed(TestStates.At(target, 0, hasOverride: false));
        return app;
    }

    private static BeginTransformGesture Gesture(
        params TransformTargetId[] targets) =>
        new(
            targets,
            TransformOperation.Translate,
            TransformSpace.Local,
            PivotMode.PerTarget,
            Description: "contract gesture");

    private static TransformDelta Move(float x) =>
        TransformDelta.Identity with { Translation = new Vector3(x, 0, 0) };

    private static PortablePose Portable(TransformTargetId target, float x) =>
        new(new[]
        {
            new PortableBonePose(
                PortableBoneId.From(target.Bone!.Value),
                new BonePose(new[]
                {
                    new PoseLayer(
                        new PoseLayerId(PoseLayerKind.Manual, "contract edit"),
                        TransformComponents.All,
                        new PoseDelta(
                            new Vector3(x, 0, 0),
                            Quaternion.Identity,
                            Vector3.Zero)),
                })),
        });

    private static void AssertBarrier(
        GestureResult result,
        TransformRecoveryReceipt pending)
    {
        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, result.Recovery);
    }

    private static void AssertBarrier(
        PoseEditResult result,
        TransformRecoveryReceipt pending)
    {
        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, result.Recovery);
    }
}
