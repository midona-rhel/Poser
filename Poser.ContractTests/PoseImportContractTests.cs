using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Core;
using Poser.Game;
using Poser.Services;

namespace Poser.ContractTests;

public sealed class PoseImportContractTests
{
    [Fact]
    public void Superseded_four_tick_arm_cancels_only_its_request_and_stale_callback_cannot_rewind_or_begin()
    {
        using var app = new PoseImportCaptureHarness();
        var first = new List<OperationReceipt>();
        var second = new List<OperationReceipt>();

        Assert.True(app.ArmModelImport(1, first.Add).Success);
        Assert.True(app.ArmModelImport(2, second.Add).Success);

        Assert.Equal(OperationReceiptState.Pending, first[0].State);
        var cancelled = Assert.Single(
            first,
            receipt => receipt.State == OperationReceiptState.Cancelled);
        Assert.Equal(OperationReceiptState.Cancelled, cancelled.State);
        Assert.Equal(OperationReceiptState.Pending, Assert.Single(second).State);
        Assert.Equal(1, app.RestoreArmCalls);

        app.RunQueued(0); // stale first request's +4 callback
        Assert.Equal(0, app.RewindCalls);
        Assert.Equal(0, app.BeginCalls);
        Assert.Single(second);

        app.RunQueued(0); // current second request's +4 callback
        Assert.Equal(1, app.RewindCalls);
        Assert.Equal(1, app.BeginCalls);
        Assert.Equal(OperationReceiptState.Pending, Assert.Single(second).State);
        Assert.Equal(app.ActorId, second[0].TargetActorId);
    }

    [Fact]
    public void Four_tick_schedule_exception_cancels_reserved_operation_and_restores_animation_owner()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.ThrowDuringSchedule = () =>
            throw new InvalidOperationException("arm schedule exploded");

        var result = app.ArmModelImport(1, receipts.Add);

        Assert.False(result.Success);
        Assert.Equal(OperationReceiptState.Pending, receipts[0].State);
        Assert.Equal(
            OperationReceiptState.Cancelled,
            Assert.Single(receipts, receipt => receipt.State != OperationReceiptState.Pending).State);
        Assert.False(app.Imports.IsPending);
        Assert.Equal(1, app.RestoreArmCalls);
        Assert.Equal(0, app.RewindCalls);
        Assert.Equal(0, app.BeginCalls);
    }

    [Fact]
    public void Post_admission_reset_exception_observes_pending_owner_before_mutation_and_rolls_back_once()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        var observedPendingOwner = false;
        app.ThrowDuringReset = () =>
        {
            observedPendingOwner = app.Imports.IsPending;
            throw new InvalidOperationException("reset exploded");
        };

        var result = app.BeginResetImport(receipts.Add);

        Assert.True(observedPendingOwner);
        Assert.False(result.Success);
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
        Assert.True(terminal.Recovery?.Complete);
        Assert.Same(terminal, result.OperationReceipt);
        Assert.False(app.Imports.IsPending);
        Assert.Single(app.Runtime.RestoreCalls);
    }

    [Fact]
    public void Busy_operation_restore_never_fabricates_complete_empty_recovery()
    {
        using var app = new PoseImportCaptureHarness();
        GestureResult? nested = null;
        var receipts = new List<OperationReceipt>();
        app.ThrowDuringReset = () => throw new InvalidOperationException("reset exploded");
        app.Runtime.DuringRestore = () =>
        {
            app.Runtime.DuringRestore = null;
            nested = app.BeginResetImport(receipts.Add);
        };

        var outer = app.Gestures.RestoreForOperation(new[] { app.InitialBoneState });

        Assert.True(outer.Success);
        Assert.NotNull(nested);
        Assert.False(nested!.Value.Success);
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Failed, terminal.State);
        Assert.Null(terminal.Recovery);
        Assert.Null(nested.Value.Recovery);
        Assert.Same(terminal, nested.Value.OperationReceipt);
        Assert.False(app.Imports.IsPending);
    }

    [Fact]
    public void Model_register_and_schedule_setup_exceptions_publish_typed_terminal_states()
    {
        using (var model = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            model.Runtime.DuringApply = () =>
                throw new InvalidOperationException("model exploded");

            var result = model.BeginModelImport(2, receipts.Add);

            Assert.False(result.Success);
            var terminal = Assert.Single(receipts);
            Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
            Assert.True(terminal.Recovery?.Complete);
        }

        using (var register = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            register.ThrowDuringRegister = () =>
                throw new InvalidOperationException("register exploded");

            var result = register.BeginWriteImport(receipts.Add);

            Assert.False(result.Success);
            var terminal = Assert.Single(receipts);
            Assert.Equal(OperationReceiptState.Failed, terminal.State);
            Assert.Null(terminal.Recovery);
        }

        using (var schedule = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            schedule.ThrowDuringSchedule = () =>
                throw new InvalidOperationException("schedule exploded");

            var result = schedule.BeginResetImport(receipts.Add);

            Assert.False(result.Success);
            var terminal = Assert.Single(receipts);
            Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
            Assert.True(terminal.Recovery?.Complete);
        }
    }

    [Fact]
    public void Incomplete_setup_rollback_preserves_exact_recovery_token()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.ThrowDuringReset = () =>
            throw new InvalidOperationException("reset exploded");
        app.Runtime.FailRestoreCalls.Add(1);

        var result = app.BeginResetImport(receipts.Add);

        Assert.False(result.Success);
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.RecoveryRequired, terminal.State);
        Assert.NotNull(terminal.Recovery);
        Assert.False(terminal.Recovery!.Complete);
        Assert.Same(terminal.Recovery, result.Recovery);
        Assert.Same(terminal.Recovery, app.Gestures.PendingRecovery);
    }

    [Fact]
    public void Direct_dispose_rolls_back_pending_import_and_publishes_one_cancelled_terminal()
    {
        var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        var begun = app.BeginResetImport(receipts.Add);
        Assert.True(begun.Success);
        Assert.True(app.Imports.IsPending);

        app.Imports.Dispose();
        app.Imports.Dispose();

        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Cancelled, terminal.State);
        Assert.True(terminal.Recovery?.Complete);
        Assert.False(app.Imports.IsPending);
        Assert.Single(app.Runtime.RestoreCalls);
        app.Dispose();
    }

    [Fact]
    public void Host_invalidation_blocks_late_native_apply_and_leaves_truthful_failed_terminal()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        Assert.True(app.BeginWriteImport(receipts.Add).Success);

        app.Imports.InvalidateForHostTeardown("dispatch failed");
        app.FireRegisteredNativeAction();
        app.EndRegisteredNativeBatch();

        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Failed, terminal.State);
        Assert.Null(terminal.Recovery);
        Assert.Equal(0, app.InteractiveStackCount);
        Assert.False(app.Imports.IsPending);
    }

    [Fact]
    public void Equal_model_transform_is_applied_as_no_op_without_write_or_history()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();

        var begun = app.BeginModelImport(0, receipts.Add);
        Assert.True(begun.Success);
        Assert.Empty(app.Runtime.ApplyCalls);

        app.RunNextDelay(0);

        Assert.Equal(OperationReceiptState.Applied, Assert.Single(receipts).State);
        Assert.Empty(app.Runtime.ApplyCalls);
        Assert.Null(app.History.PeekUndo());
    }

    [Fact]
    public void Actor_object_replacement_with_same_logical_id_fails_exact_identity_before_mutation()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        var reserved = app.Imports.Reserve(
            app.Actor,
            "replacement import",
            out var operation,
            onReceipt: receipts.Add);
        Assert.True(reserved.Success);
        Assert.NotNull(operation);

        app.ReplaceActorObjectAtSameLogicalIdentity();
        var begun = app.Imports.Begin(operation!, app.CreateModelPlan(3));

        Assert.False(begun.Success);
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Failed, terminal.State);
        Assert.Equal(app.ActorId, terminal.TargetActorId);
        Assert.Empty(app.Runtime.ApplyCalls);
        Assert.False(app.Imports.IsPending);
    }

    [Fact]
    public void Flatten_appended_targets_are_refrozen_and_delayed_slot_replacement_rolls_back_before_late_native_write()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.SeedHeadInteractiveStack();
        Assert.True(app.BeginFaceExpressionImport(receipts.Add).Success);

        app.FireCharacterNativeActions();
        app.EndRegisteredNativeBatch();
        app.RunNextDelay(4); // reconcile registration; head is appended
        app.FireCharacterNativeActions();
        app.EndRegisteredNativeBatch();
        app.UseWeaponFlattenPlan();
        app.RunNextDelay(0); // native reconcile completion hops to framework
        app.RunNextDelay(2); // flatten capture/reset; weapon is appended

        app.ReplaceWeaponSlot();
        app.RunNextDelay(60); // original operation timeout revalidates frozen targets
        app.FireWeaponNativeAction();

        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
        Assert.True(terminal.Recovery?.Complete);
        Assert.False(app.Imports.IsPending);
    }

    [Fact]
    public void Terminal_is_published_once_even_when_completion_callbacks_throw()
    {
        var app = new PoseImportCaptureHarness();
        var callbackCalls = 0;
        var broadcastCalls = 0;
        app.Imports.ReceiptPublished += _ =>
        {
            broadcastCalls++;
            throw new InvalidOperationException("subscriber exploded");
        };
        Assert.True(app.BeginResetImport(_ =>
        {
            callbackCalls++;
            throw new InvalidOperationException("callback exploded");
        }).Success);

        app.Imports.Dispose();
        app.Imports.Dispose();

        Assert.Equal(1, callbackCalls);
        Assert.Equal(1, broadcastCalls);
        app.Dispose();
    }

    [Fact]
    public void Normal_gpose_false_drains_import_before_session_exit_and_false_publication()
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        Assert.True(app.BeginResetImport(receipts.Add).Success);
        var client = Substitute.For<IClientState>();
        var events = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var lifecycle = Substitute.For<ISessionLifecycleCoordinator>();
        lifecycle.OnGposeEntered().Returns(SessionGeneration.New());
        lifecycle.OnGposeExit().Returns(_ =>
        {
            Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
            Assert.False(app.Imports.IsPending);
            return default;
        });
        events.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(call =>
            {
                if (!call.Arg<GPoseStateChangedEvent>().IsGPosing)
                    Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
            });
        using var gpose = new GPoseService(
            client,
            app.Framework,
            events,
            log,
            lifecycle,
            () => app.Imports);

        client.IsGPosing.Returns(true);
        app.Framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(app.Framework);
        client.IsGPosing.Returns(false);
        app.Framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(app.Framework);

        Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
        Assert.Single(app.Runtime.RestoreCalls);
    }

    [Fact]
    public void Operation_receipt_is_additive_to_legacy_pose_edit_result_identity()
    {
        var receipt = OperationReceipt.Pending(
            Guid.NewGuid(),
            OperationEpoch.First,
            SessionGeneration.New(),
            TestIds.Actor());
        var legacy = new PoseEditResult(true, 3, "same");
        var withReceipt = legacy with { OperationReceipt = receipt };

        Assert.Equal(legacy, withReceipt);
        Assert.True(legacy == withReceipt);
        Assert.Equal(legacy.GetHashCode(), withReceipt.GetHashCode());
        var (success, affected, detail) = withReceipt;
        Assert.True(success);
        Assert.Equal(3, affected);
        Assert.Equal("same", detail);
        Assert.Same(receipt, withReceipt.OperationReceipt);
    }

    [Fact]
    public void Operation_receipt_pending_is_non_terminal_and_exactly_identified()
    {
        var id = Guid.NewGuid();
        var session = SessionGeneration.New();
        var actor = TestIds.Actor(4);
        var receipt = OperationReceipt.Pending(
            id,
            OperationEpoch.First,
            session,
            actor,
            "Import test");

        Assert.Equal(id, receipt.OperationId);
        Assert.Equal(OperationEpoch.First, receipt.OperationEpoch);
        Assert.Equal(session, receipt.SessionGeneration);
        Assert.Equal(actor, receipt.TargetActorId);
        Assert.Equal(OperationReceiptState.Pending, receipt.State);
        Assert.Null(receipt.Recovery);
    }

    [Fact]
    public void Import_recovery_entry_point_preserves_exact_pending_retry_token()
    {
        var target = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        var initial = TestStates.At(target, -2);
        app.Runtime.Seed(initial);
        app.Runtime.FailRestoreCalls.Add(1);
        app.Runtime.RestoreFailureDetail = "restore failed";

        var result = app.Gestures.RestoreForOperation(new[] { initial });

        Assert.False(result.Success);
        var recovery = Assert.IsType<TransformRecoveryReceipt>(result.Recovery);
        Assert.Same(recovery, app.Gestures.PendingRecovery);
        Assert.False(recovery.Complete);

        app.Runtime.FailRestoreCalls.Clear();
        var retried = app.Gestures.RetryRecovery(recovery);

        Assert.True(retried.Success);
        Assert.Null(app.Gestures.PendingRecovery);
        Assert.Equal(initial, app.Runtime.State(target));
    }
}
