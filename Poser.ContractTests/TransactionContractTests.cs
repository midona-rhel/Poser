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
    public void Absolute_edits_refuse_unavailable_runtime_and_restore_atomic_state()
    {
        using (var app = new TransformApplicationHarness())
        {
            var first = TestIds.ActorTarget();
            var second = TestIds.BoneTarget();
            app.Scene.Refresh(TestScenes.ActorAndBoneScene(TestIds.Actor(), second.Bone!.Value));
            var firstInitial = TestStates.At(first, -4);
            var secondInitial = TestStates.At(second, 7, hasOverride: false);
            app.Runtime.Seed(firstInitial);
            app.Runtime.Seed(secondInitial);
            app.Runtime.FailApplyCall = 2;
            app.Runtime.FailureDetail = "native capability unavailable";
            app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
            var result = app.Commands.SetAbsoluteMany(
                new[] { (first, TestStates.Translated(1)), (second, TestStates.Translated(2)) },
                "atomic edit");
            Assert.False(result.Success);
            Assert.Contains("native capability", result.Detail!);
            Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
            Assert.Equal(firstInitial, app.Runtime.State(first));
            Assert.Equal(secondInitial, app.Runtime.State(second));
            Assert.False(app.History.CanUndo);
        }

        using (var app = ActorHarness(TestIds.ActorTarget(), TestStates.At(TestIds.ActorTarget(), -9)))
        {
            var target = TestIds.ActorTarget();
            var initial = app.Runtime.State(target);
            app.Runtime.FailCaptureCall = 2;
            app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
            app.Runtime.FailureDetail = "native capability unavailable";
            var result = app.Commands.SetAbsolute(
                target, TestStates.Translated(4), "capture failure");
            Assert.False(result.Success);
            Assert.Contains("native capability", result.Detail!);
            Assert.Equal(new[] { target }, app.Runtime.RestoreCalls);
            Assert.Equal(initial, app.Runtime.State(target));
            Assert.False(app.History.CanUndo);
        }

        using (var app = new TransformApplicationHarness())
        {
            var target = TestIds.ActorTarget();
            app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
            app.Runtime.Seed(TestStates.For(target));
            app.Runtime.FailApplyCall = 1;
            app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
            app.Runtime.FailureDetail = "framework thread is unavailable";
            var result = app.Commands.SetAbsolute(
                target, TestStates.Translated(3), "unavailable edit");
            Assert.False(result.Success);
            Assert.Contains("framework thread", result.Detail!);
            Assert.False(app.History.CanUndo);
            Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
        }
    }

    [Fact]
    public void Absolute_failure_recovery_is_complete_or_retryable_with_exact_baselines()
    {
        using (var app = ActorHarness(TestIds.ActorTarget(), TestStates.At(TestIds.ActorTarget(), -3)))
        {
            var target = TestIds.ActorTarget();
            var initial = app.Runtime.State(target);
            app.Runtime.FailApplyCall = 1;
            app.Runtime.MutateBeforeApplyFailure = true;
            app.Runtime.ApplyFailureDetail = "apply mutated before failing";
            var result = app.Commands.SetAbsolute(
                target, TestStates.Translated(8), "single recovery");
            Assert.False(result.Success);
            Assert.Contains("apply mutated", result.Detail!);
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.True(recovery.Complete);
            Assert.Equal(initial, Assert.Single(recovery.Attempts).RequestedState);
            Assert.Equal(initial, app.Runtime.State(target));
            Assert.False(app.History.CanUndo);
        }

        using (var app = ActorHarness(TestIds.ActorTarget(), TestStates.At(TestIds.ActorTarget(), -9)))
        {
            var target = TestIds.ActorTarget();
            var initial = app.Runtime.State(target);
            app.Runtime.FailCaptureCall = 2;
            app.Runtime.CaptureFailureDetail = "final capture unavailable";
            app.Runtime.FailRestoreCalls.Add(1);
            app.Runtime.RestoreFailureDetail = "baseline restore unavailable";
            app.Runtime.FailureStatus = TransformPortStatus.NativeUnavailable;
            var result = app.Commands.SetAbsolute(
                target, TestStates.Translated(4), "capture recovery");
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
            app.Runtime.FailRestoreCalls.Clear();
            var retried = app.Gestures.RetryRecovery(pending);
            Assert.True(retried.Success);
            Assert.True(Assert.IsType<TransformRecoveryReceipt>(retried.Recovery).Complete);
            Assert.Null(app.Gestures.PendingRecovery);
            Assert.Equal(initial, app.Runtime.State(target));
            Assert.False(app.History.CanUndo);
        }

        using (var app = new TransformApplicationHarness())
        {
            var first = TestIds.ActorTarget();
            var second = TestIds.BoneTarget();
            app.Scene.Refresh(TestScenes.ActorAndBoneScene(TestIds.Actor(), second.Bone!.Value));
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
                new[] { (first, TestStates.Translated(1)), (second, TestStates.Translated(2)) },
                "partial rollback");
            var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.False(pending.Complete);
            Assert.Equal(2, pending.Attempts.Count);
            Assert.Equal(new[] { first, second },
                pending.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
            Assert.Equal(TestStates.Translated(1), app.Runtime.State(first).Transform);
            Assert.Equal(secondInitial, app.Runtime.State(second));
            app.Runtime.FailRestoreCalls.Clear();
            var retried = app.Gestures.RetryRecovery(pending);
            Assert.True(retried.Success);
            Assert.True(Assert.IsType<TransformRecoveryReceipt>(retried.Recovery).Complete);
            Assert.Null(app.Gestures.PendingRecovery);
            Assert.Equal(firstInitial, app.Runtime.State(first));
            Assert.Equal(secondInitial, app.Runtime.State(second));
            Assert.False(app.History.CanUndo);
        }
    }

    [Fact]
    public void Primary_failure_and_restore_failure_remain_distinct_and_exhaustive()
    {
        using (var app = ActorHarness(TestIds.ActorTarget(), TestStates.At(TestIds.ActorTarget(), 5)))
        {
            var target = TestIds.ActorTarget();
            app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
            app.Runtime.MutateBeforeRestoreFailureCalls.Add(1);
            app.Runtime.RestoreFailureDetails[1] = "clear override write failed";
            app.Runtime.RestoreFailureDetails[2] = "rollback restore failed";
            var result = app.Commands.ClearActorOverrides(new[] { target }, "clear override");
            Assert.False(result.Success);
            Assert.Contains("clear override write failed", result.Detail!);
            Assert.Contains("Rollback also failed", result.Detail!);
            Assert.Contains("rollback restore failed", result.Detail!);
            Assert.False(Assert.IsType<TransformRecoveryReceipt>(result.Recovery).Complete);
            Assert.False(app.Runtime.State(target).HasOverride);
            Assert.False(app.History.CanUndo);
        }

        using (var app = new TransformApplicationHarness())
        {
            var first = TestIds.ActorTarget();
            var second = TestIds.BoneTarget();
            app.Scene.Refresh(TestScenes.ActorAndBoneScene(TestIds.Actor(), second.Bone!.Value));
            app.Runtime.Seed(TestStates.At(first, 1));
            app.Runtime.Seed(TestStates.At(second, 2, hasOverride: false));
            app.Runtime.FailApplyCall = 2;
            app.Runtime.ThrowRestoreCalls.Add(1);
            var result = app.Commands.SetAbsoluteMany(
                new[] { (first, TestStates.Translated(3)), (second, TestStates.Translated(4)) },
                "throwing restore");
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains("restore threw", result.Detail!);
            Assert.False(recovery.Complete);
            Assert.Single(recovery.Failures);
            Assert.Equal(first, recovery.Failures[0].RequestedState.Target);
            Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
            Assert.Same(recovery, app.Gestures.PendingRecovery);
            Assert.Equal(TestStates.Translated(3), app.Runtime.State(first).Transform);
            Assert.Equal(TestStates.At(second, 2, hasOverride: false), app.Runtime.State(second));
        }
    }

    [Fact]
    public void Gesture_failures_disarm_lifecycle_and_arm_recovery_barriers()
    {
        using (var app = BoneHarness(TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1),
                   TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2)))
        {
            var first = TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1);
            var second = TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2);
            var begun = app.Gestures.Begin(Gesture(first, second));
            Assert.True(begun.Success);
            app.Runtime.FailApplyCall = 2;
            app.Runtime.MutateBeforeApplyFailure = true;
            app.Runtime.ApplyFailureDetail = "second gesture apply failed";
            app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
            app.Runtime.RestoreFailureDetail = "gesture rollback failed";
            var result = app.Gestures.Update(begun.GestureId!.Value, Move(3));
            var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains("second gesture apply failed", result.Detail!);
            Assert.Equal(2, pending.Failures.Count);
            Assert.Equal(new[] { first, second },
                pending.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.ActiveGesture);
            Assert.Same(pending, app.Gestures.PendingRecovery);
            Assert.False(app.History.CanUndo);
            AssertBarrier(app.Gestures.Begin(Gesture(first)), pending);
        }

        using (var app = BoneHarness(TestIds.BoneTarget()))
        {
            var target = TestIds.BoneTarget();
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
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(recovery.Complete);
            Assert.Equal(new[] { target },
                recovery.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { target }, app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.ActiveGesture);
            Assert.NotNull(app.Gestures.PendingRecovery);
            Assert.False(app.History.CanUndo);
        }

        using (var app = BoneHarness(TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1),
                   TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2)))
        {
            var first = TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1);
            var second = TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2);
            app.Runtime.ThrowCaptureCall = 2;
            var result = app.Gestures.Begin(Gesture(first, second));
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains("capture threw", result.Detail!);
            Assert.True(recovery.Complete);
            Assert.Equal(new[] { first },
                recovery.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { first }, app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.ActiveGesture);
            Assert.Null(app.Gestures.PendingRecovery);
            Assert.False(app.History.CanUndo);
        }

        using (var app = BoneHarness(TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1),
                   TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2)))
        {
            var first = TestIds.BoneTarget(name: "j_arm_l", boneIndex: 1);
            var second = TestIds.BoneTarget(name: "j_arm_r", boneIndex: 2);
            var begun = app.Gestures.Begin(Gesture(first, second));
            Assert.True(begun.Success);
            app.Runtime.ThrowApplyCall = 2;
            app.Runtime.ThrowRestoreCalls.Add(1);
            var result = app.Gestures.Update(begun.GestureId!.Value, Move(3));
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains("apply threw", result.Detail!);
            Assert.Contains("restore threw", result.Detail!);
            Assert.False(recovery.Complete);
            Assert.Equal(new[] { first, second },
                recovery.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { first, second }, app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.ActiveGesture);
            Assert.Same(recovery, app.Gestures.PendingRecovery);
        }

        using (var app = BoneHarness(TestIds.BoneTarget()))
        {
            var target = TestIds.BoneTarget();
            var begun = app.Gestures.Begin(Gesture(target));
            Assert.True(begun.Success);
            Assert.True(app.Gestures.Update(begun.GestureId!.Value, Move(2)).Success);
            app.Runtime.ThrowCaptureCall = 2;
            app.Runtime.ThrowRestoreCalls.Add(1);
            var result = app.Gestures.Commit(begun.GestureId.Value);
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains("capture threw", result.Detail!);
            Assert.Contains("restore threw", result.Detail!);
            Assert.False(recovery.Complete);
            Assert.Equal(new[] { target },
                recovery.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(new[] { target }, app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.ActiveGesture);
            Assert.Same(recovery, app.Gestures.PendingRecovery);
        }
    }

    [Fact]
    public void Cancellation_and_disposal_retain_pending_recovery_across_all_owner_paths()
    {
        using (var direct = BoneHarness(TestIds.BoneTarget()))
        {
            var target = TestIds.BoneTarget();
            var begun = direct.Gestures.Begin(Gesture(target));
            direct.Runtime.FailRestoreCalls.Add(1);
            var cancelled = direct.Gestures.Cancel(begun.GestureId!.Value);
            Assert.False(cancelled.Success);
            Assert.Same(cancelled.Recovery, direct.Gestures.PendingRecovery);
        }
        using (var selection = BoneHarness(TestIds.BoneTarget()))
        {
            var target = TestIds.BoneTarget();
            Assert.True(selection.Gestures.Begin(Gesture(target)).Success);
            selection.Runtime.FailRestoreCalls.Add(1);
            selection.Selection.Select(SelectionId.ForActor(TestIds.Actor()));
            Assert.NotNull(selection.Gestures.PendingRecovery);
        }
        using (var reconcile = BoneHarness(TestIds.BoneTarget()))
        {
            var target = TestIds.BoneTarget();
            Assert.True(reconcile.Gestures.Begin(Gesture(target)).Success);
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
    public void Undo_and_redo_retry_the_same_patch_before_committing_history()
    {
        var target = TestIds.ActorTarget();
        using (var app = ActorHarness(target, TestStates.At(target, 0)))
        {
            Assert.True(app.Commands.SetAbsolute(target, TestStates.Translated(6), "undo recovery").Success);
            app.Runtime.FailRestoreCalls.Add(1);
            var failed = app.Gestures.Undo();
            var pending = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
            Assert.False(failed.Success);
            Assert.True(app.History.CanUndo);
            Assert.False(app.History.CanRedo);
            app.Runtime.FailRestoreCalls.Clear();
            Assert.True(app.Gestures.RetryRecovery(pending).Success);
            Assert.True(app.Gestures.Undo().Success);
            Assert.False(app.History.CanUndo);
            Assert.True(app.History.CanRedo);
            Assert.Equal(PoseTransform.Identity, app.Runtime.State(target).Transform);
        }
        using (var app = ActorHarness(target, TestStates.At(target, 0)))
        {
            Assert.True(app.Commands.SetAbsolute(target, TestStates.Translated(6), "redo recovery").Success);
            Assert.True(app.Gestures.Undo().Success);
            app.Runtime.FailRestoreCalls.Add(app.Runtime.RestoreCalls.Count + 1);
            var failed = app.Gestures.Redo();
            var pending = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
            Assert.False(failed.Success);
            Assert.False(app.History.CanUndo);
            Assert.True(app.History.CanRedo);
            app.Runtime.FailRestoreCalls.Clear();
            Assert.True(app.Gestures.RetryRecovery(pending).Success);
            Assert.True(app.Gestures.Redo().Success);
            Assert.True(app.History.CanUndo);
            Assert.False(app.History.CanRedo);
            Assert.Equal(TestStates.Translated(6), app.Runtime.State(target).Transform);
        }
    }

    [Fact]
    public void Pose_recovery_barrier_blocks_every_mutation_and_retry_rejects_stale_receipts()
    {
        var bone = TestIds.BoneTarget();
        using (var app = BoneHarness(bone))
        {
            var initial = app.Runtime.State(bone);
            var pose = Portable(bone, 4);
            app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
            app.Runtime.MutateBeforeRestoreFailureCalls.Add(1);
            app.Runtime.RestoreFailureDetails[1] = "pose write failed after mutation";
            app.Runtime.RestoreFailureDetails[2] = "pose rollback failed";
            var result = app.PoseEdits.ApplyPortable(new[] { bone }, pose, "pose recovery");
            var pending = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Equal(0, result.Affected);
            Assert.Contains("pose write failed after mutation", result.Detail!);
            Assert.Contains("pose rollback failed", result.Detail!);
            Assert.Same(pending, app.Gestures.PendingRecovery);
            Assert.True(app.Runtime.State(bone).HasOverride);
            var captureCount = app.Runtime.CaptureCalls.Count;
            var applyCount = app.Runtime.ApplyCalls.Count;
            var restoreCount = app.Runtime.RestoreCalls.Count;
            AssertBarrier(app.Gestures.Begin(Gesture(bone)), pending);
            AssertBarrier(app.Commands.SetAbsolute(TestIds.ActorTarget(), TestStates.Translated(1), "blocked"), pending);
            AssertBarrier(app.Commands.SetAbsoluteMany(
                new[] { (TestIds.ActorTarget(), TestStates.Translated(1)) }, "blocked"), pending);
            AssertBarrier(app.Commands.ClearActorOverrides(
                new[] { TestIds.ActorTarget() }, "blocked"), pending);
            AssertBarrier(app.PoseEdits.Reset(new[] { bone }, PoseRegion.All, "blocked"), pending);
            AssertBarrier(app.PoseEdits.Flip(bone, "blocked"), pending);
            AssertBarrier(app.PoseEdits.Mirror(new[] { bone }, "blocked"), pending);
            AssertBarrier(app.PoseEdits.CapturePortable(Array.Empty<TransformTargetId>()), pending);
            AssertBarrier(new PoseTransferService(app.PoseEdits)
                .Capture(Array.Empty<TransformTargetId>()), pending);
            AssertBarrier(app.PoseEdits.ApplyPortable(new[] { bone }, pose, "blocked"), pending);
            AssertBarrier(app.PoseEdits.ApplyPortable(
                Array.Empty<TransformTargetId>(), null!, "blocked null"), pending);
            var unrelatedGesture = TransformGestureId.New();
            AssertBarrier(app.Gestures.Update(unrelatedGesture, default), pending);
            AssertBarrier(app.Gestures.Commit(unrelatedGesture), pending);
            AssertBarrier(app.Gestures.Cancel(unrelatedGesture), pending);
            AssertBarrier(app.Gestures.Undo(), pending);
            AssertBarrier(app.Gestures.Redo(), pending);
            Assert.Equal(captureCount, app.Runtime.CaptureCalls.Count);
            Assert.Equal(applyCount, app.Runtime.ApplyCalls.Count);
            Assert.Equal(restoreCount, app.Runtime.RestoreCalls.Count);
            app.Runtime.FailRestoreCalls.Clear();
            var recovered = app.Gestures.RetryRecovery(pending);
            Assert.True(recovered.Success);
            var recoveredReceipt = Assert.IsType<TransformRecoveryReceipt>(recovered.Recovery);
            Assert.True(recoveredReceipt.Complete);
            Assert.Empty(recoveredReceipt.Failures);
            Assert.Null(recovered.Detail);
            Assert.Equal(initial, app.Runtime.State(bone));
            Assert.Null(app.Gestures.PendingRecovery);
        }

        using (var app = ActorHarness(TestIds.ActorTarget(), TestStates.At(TestIds.ActorTarget(), -2)))
        {
            var target = TestIds.ActorTarget();
            app.Runtime.FailApplyCall = 1;
            app.Runtime.MutateBeforeApplyFailure = true;
            app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
            var failed = app.Commands.SetAbsolute(target, TestStates.Translated(7), "retry token");
            var original = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
            var partialRetry = app.Gestures.RetryRecovery(original);
            var replacement = Assert.IsType<TransformRecoveryReceipt>(partialRetry.Recovery);
            Assert.False(partialRetry.Success);
            Assert.NotSame(original, replacement);
            Assert.Same(replacement, app.Gestures.PendingRecovery);
            var restoreCount = app.Runtime.RestoreCalls.Count;
            var stale = app.Gestures.RetryRecovery(original);
            Assert.False(stale.Success);
            Assert.Contains("current", stale.Detail!, StringComparison.OrdinalIgnoreCase);
            Assert.Same(replacement, stale.Recovery);
            Assert.Equal(restoreCount, app.Runtime.RestoreCalls.Count);
            app.Runtime.FailRestoreCalls.Clear();
            Assert.True(app.Gestures.RetryRecovery(replacement).Success);
            Assert.False(app.Gestures.RetryRecovery(replacement).Success);
        }
    }

    [Fact]
    public void Recovery_additions_preserve_legacy_result_equality_hash_and_deconstruction()
    {
        var receipt = new TransformRecoveryReceipt(Array.Empty<TransformRecoveryAttempt>());
        var gestureId = TransformGestureId.New();
        var gesture = new GestureResult(false, "same", gestureId);
        var gestureWithRecovery = gesture with { Recovery = receipt };
        Assert.Equal(gesture, gestureWithRecovery);
        Assert.Equal(gesture.GetHashCode(), gestureWithRecovery.GetHashCode());
        var (gestureSuccess, gestureDetail, deconstructedId) = gestureWithRecovery;
        Assert.False(gestureSuccess);
        Assert.Equal("same", gestureDetail);
        Assert.Equal(gestureId, deconstructedId);

        var edit = new PoseEditResult(false, 3, "same");
        var editWithRecovery = edit with { Recovery = receipt };
        Assert.Equal(edit, editWithRecovery);
        Assert.Equal(edit.GetHashCode(), editWithRecovery.GetHashCode());
        var (editSuccess, affected, editDetail) = editWithRecovery;
        Assert.False(editSuccess);
        Assert.Equal(3, affected);
        Assert.Equal("same", editDetail);

        var pose = Portable(TestIds.BoneTarget(), 1);
        var capture = new PoseCaptureResult(false, pose, "same");
        var captureWithRecovery = capture with { Recovery = receipt };
        Assert.Equal(capture, captureWithRecovery);
        Assert.Equal(capture.GetHashCode(), captureWithRecovery.GetHashCode());
        var (captureSuccess, capturedPose, captureDetail) = captureWithRecovery;
        Assert.False(captureSuccess);
        Assert.Same(pose, capturedPose);
        Assert.Equal("same", captureDetail);
    }

    [Fact]
    public void Reentrant_apply_and_restore_are_busy_without_nested_runtime_calls()
    {
        var target = TestIds.ActorTarget();
        using (var app = ActorHarness(target, TestStates.At(target, 0)))
        {
            GestureResult? reentrant = null;
            app.Runtime.DuringApply = () =>
            {
                app.Runtime.DuringApply = null;
                reentrant = app.Commands.SetAbsolute(target, TestStates.Translated(99), "reentrant");
            };
            var outer = app.Commands.SetAbsolute(target, TestStates.Translated(2), "outer");
            Assert.True(outer.Success);
            Assert.False(reentrant!.Value.Success);
            Assert.Contains("busy", reentrant.Value.Detail!, StringComparison.OrdinalIgnoreCase);
            Assert.Single(app.Runtime.ApplyCalls);
            Assert.Empty(app.Runtime.RestoreCalls);
            Assert.Null(app.Gestures.PendingRecovery);
            Assert.True(app.History.CanUndo);
        }

        using (var app = ActorHarness(target, TestStates.At(target, 0)))
        {
            app.Runtime.FailApplyCall = 1;
            app.Runtime.MutateBeforeApplyFailure = true;
            app.Runtime.FailRestoreCalls.Add(1);
            GestureResult? reentrant = null;
            app.Runtime.DuringRestore = () =>
            {
                app.Runtime.DuringRestore = null;
                reentrant = app.Gestures.Cancel(TransformGestureId.New());
            };
            var outer = app.Commands.SetAbsolute(target, TestStates.Translated(2), "outer failure");
            var pending = Assert.IsType<TransformRecoveryReceipt>(outer.Recovery);
            Assert.False(outer.Success);
            Assert.False(reentrant!.Value.Success);
            Assert.Contains("busy", reentrant.Value.Detail!, StringComparison.OrdinalIgnoreCase);
            Assert.Single(app.Runtime.RestoreCalls);
            Assert.Same(pending, app.Gestures.PendingRecovery);
            Assert.False(app.History.CanUndo);
        }
    }

    [Fact]
    public void Stash_failure_is_barriered_and_successful_stash_apply_keeps_history_semantics()
    {
        var bone = TestIds.BoneTarget();
        using (var app = BoneHarness(bone))
        {
            var pending = CreatePendingPoseRecovery(app, bone);
            var transfers = new PoseTransferService(app.PoseEdits);
            var captureCount = app.Runtime.CaptureCalls.Count;
            var applyCount = app.Runtime.ApplyCalls.Count;
            var restoreCount = app.Runtime.RestoreCalls.Count;
            var result = transfers.Stash(Array.Empty<TransformTargetId>(), "blocked stash");
            AssertBarrier(result, pending);
            Assert.False(transfers.HasStash);
            Assert.Equal(captureCount, app.Runtime.CaptureCalls.Count);
            Assert.Equal(applyCount, app.Runtime.ApplyCalls.Count);
            Assert.Equal(restoreCount, app.Runtime.RestoreCalls.Count);
            AssertBarrier(transfers.ApplyStash(Array.Empty<TransformTargetId>()), pending);
        }

        using (var app = BoneHarness(bone))
        {
            var transfers = new PoseTransferService(app.PoseEdits);
            var stashed = transfers.Stash(new[] { bone }, "source actor");
            Assert.True(stashed.Success);
            Assert.Equal(1, stashed.Affected);
            Assert.True(transfers.HasStash);
            Assert.Equal("source actor", transfers.StashedFrom);
            Assert.NotNull(transfers.StashedAt);
            Assert.False(app.History.CanUndo);
            var applied = transfers.ApplyStash(new[] { bone });
            Assert.True(applied.Success);
            Assert.True(app.History.CanUndo);
            Assert.Equal("Apply stashed pose", app.History.UndoDescription);
        }
    }

    [Fact]
    public void Thrown_capture_and_apply_phases_return_typed_recovery_for_every_captured_target()
    {
        foreach (var phase in new[] { 2, 3 })
        {
            var first = TestIds.ActorTarget();
            var second = TestIds.BoneTarget();
            using var app = new TransformApplicationHarness();
            app.Scene.Refresh(TestScenes.ActorAndBoneScene(TestIds.Actor(), second.Bone!.Value));
            app.Runtime.Seed(TestStates.At(first, 1));
            app.Runtime.Seed(TestStates.At(second, 2, hasOverride: false));
            if (phase == 2)
                app.Runtime.ThrowCaptureCall = phase;
            else
                app.Runtime.ThrowApplyCall = phase - 1;
            var result = app.Commands.SetAbsoluteMany(
                new[] { (first, TestStates.Translated(3)), (second, TestStates.Translated(4)) },
                "throwing phase");
            var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
            Assert.False(result.Success);
            Assert.Contains(phase == 2 ? "capture threw" : "apply threw", result.Detail!);
            Assert.True(recovery.Complete);
            Assert.Equal(
                phase == 2 ? new[] { first } : new[] { first, second },
                recovery.Attempts.Select(attempt => attempt.RequestedState.Target));
            Assert.Equal(
                phase == 2 ? new[] { first } : new[] { first, second },
                app.Runtime.RestoreCalls);
            Assert.False(app.History.CanUndo);
        }

        var firstTarget = TestIds.ActorTarget();
        var secondTarget = TestIds.BoneTarget();
        using var post = new TransformApplicationHarness();
        post.Scene.Refresh(TestScenes.ActorAndBoneScene(TestIds.Actor(), secondTarget.Bone!.Value));
        post.Runtime.Seed(TestStates.At(firstTarget, 1));
        post.Runtime.Seed(TestStates.At(secondTarget, 2, hasOverride: false));
        post.Runtime.ThrowCaptureCall = 3;
        var postResult = post.Commands.SetAbsoluteMany(
            new[] { (firstTarget, TestStates.Translated(3)), (secondTarget, TestStates.Translated(4)) },
            "throwing post capture");
        var postRecovery = Assert.IsType<TransformRecoveryReceipt>(postResult.Recovery);
        Assert.False(postResult.Success);
        Assert.Contains("capture threw", postResult.Detail!);
        Assert.True(postRecovery.Complete);
        Assert.Equal(new[] { firstTarget, secondTarget }, postRecovery.Attempts.Select(a => a.RequestedState.Target));
        Assert.Equal(new[] { firstTarget, secondTarget }, post.Runtime.RestoreCalls);
        Assert.False(post.History.CanUndo);
    }

    [Fact]
    public void Patch_observer_exception_after_commit_does_not_masquerade_as_apply_failure()
    {
        var target = TestIds.ActorTarget();
        using var app = ActorHarness(target, TestStates.At(target, 1));
        app.History.PatchAppended += () =>
            throw new InvalidOperationException("observer threw");
        var result = app.Commands.SetAbsolute(target, TestStates.Translated(8), "observer isolation");
        Assert.True(result.Success);
        Assert.True(app.History.CanUndo);
        Assert.Equal(TestStates.Translated(8), app.Runtime.State(target).Transform);
        Assert.Empty(app.Runtime.RestoreCalls);
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
            TestIds.Actor(), targets.Select(target => target.Bone!.Value).ToArray()));
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

    private static TransformRecoveryReceipt CreatePendingPoseRecovery(
        TransformApplicationHarness app,
        TransformTargetId bone)
    {
        app.Runtime.FailRestoreCalls.UnionWith(new[] { 1, 2 });
        app.Runtime.MutateBeforeRestoreFailureCalls.Add(1);
        var failed = app.PoseEdits.ApplyPortable(
            new[] { bone },
            Portable(bone, 4),
            "create pending recovery");
        return Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
    }

    private static void AssertBarrier(GestureResult result, TransformRecoveryReceipt pending)
    {
        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, result.Recovery);
    }

    private static void AssertBarrier(PoseEditResult result, TransformRecoveryReceipt pending)
    {
        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, result.Recovery);
    }

    private static void AssertBarrier(PoseCaptureResult result, TransformRecoveryReceipt pending)
    {
        Assert.False(result.Success);
        Assert.Contains("recovery", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Same(pending, result.Recovery);
    }
}
