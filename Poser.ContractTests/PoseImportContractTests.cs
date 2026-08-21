using Newtonsoft.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Appearance;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Appearance;
using Poser.Config;
using Poser.Core;
using Poser.Game;
using Poser.Services;

namespace Poser.ContractTests;

public sealed class PoseImportContractTests
{
    [Fact]
    public void Import_adjacent_model_id_ownership_and_preset_state_round_trip()
    {
        var port = new ModelIdPort { Current = 0 };
        var model = new ActorModelIdSession(port);
        var actor = ActorId.New();

        Assert.True(model.Apply(actor, 878).Success);
        Assert.True(model.Apply(actor, 1234).Success);
        Assert.True(model.IsOwned(actor));
        Assert.Equal(0, model.CaptureFor(actor));
        Assert.Equal(new[] { 878, 1234 }, port.Writes);

        var config = new PoserConfiguration();
        config.Skeleton.BoneVisibilityPresets.Add(new BoneVisibilityPreset
        {
            Name = "Head",
            Bones = new List<string> { "j_kao", "j_kubi" },
        });
        var restored = JsonConvert.DeserializeObject<PoserConfiguration>(
            JsonConvert.SerializeObject(config));
        Assert.NotNull(restored);
        var preset = Assert.Single(restored!.Skeleton.BoneVisibilityPresets);
        Assert.Equal("Head", preset.Name);
        Assert.Equal(new[] { "j_kao", "j_kubi" }, preset.Bones);
    }

    [Fact]
    public void Admission_setup_and_recovery_failures_preserve_owner_and_exact_tokens()
    {
        using (var app = new PoseImportCaptureHarness())
        {
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

            app.RunQueued(0);
            Assert.Equal(0, app.RewindCalls);
            Assert.Equal(0, app.BeginCalls);
            app.RunQueued(0);
            Assert.Equal(1, app.RewindCalls);
            Assert.Equal(1, app.BeginCalls);
            Assert.Equal(app.ActorId, second[0].TargetActorId);
        }

        using (var app = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            app.ThrowDuringSchedule = () =>
                throw new InvalidOperationException("arm schedule exploded");
            var result = app.ArmModelImport(1, receipts.Add);
            Assert.False(result.Success);
            Assert.Equal(OperationReceiptState.Pending, receipts[0].State);
            var terminal = Assert.Single(
                receipts,
                receipt => receipt.State != OperationReceiptState.Pending);
            Assert.Equal(OperationReceiptState.Cancelled,
                terminal.State);
            Assert.False(app.Imports.IsPending);
            Assert.Equal(1, app.RestoreArmCalls);
            Assert.Equal(0, app.RewindCalls);
            Assert.Equal(0, app.BeginCalls);
        }

        using (var app = new PoseImportCaptureHarness())
        {
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

        using (var app = new PoseImportCaptureHarness())
        {
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
            Assert.Same(terminal, nested.Value.OperationReceipt);
            Assert.False(app.Imports.IsPending);
        }

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

        using (var app = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            app.ThrowDuringReset = () =>
                throw new InvalidOperationException("reset exploded");
            app.Runtime.FailRestoreCalls.Add(1);
            var result = app.BeginResetImport(receipts.Add);
            var pending = Assert.Single(receipts);
            Assert.False(result.Success);
            Assert.Equal(OperationReceiptState.RecoveryRequired, pending.State);
            Assert.False(pending.Recovery!.Complete);
            Assert.Same(pending.Recovery, result.Recovery);
            Assert.Same(pending.Recovery, app.Gestures.PendingRecovery);
        }

        using (var app = new TransformApplicationHarness())
        {
            var target = TestIds.ActorTarget();
            app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
            var initial = TestStates.At(target, -2);
            app.Runtime.Seed(initial);
            app.Runtime.FailRestoreCalls.Add(1);
            var failed = app.Gestures.RestoreForOperation(new[] { initial });
            var recovery = Assert.IsType<TransformRecoveryReceipt>(failed.Recovery);
            Assert.False(failed.Success);
            Assert.False(recovery.Complete);
            Assert.Same(recovery, app.Gestures.PendingRecovery);
            app.Runtime.FailRestoreCalls.Clear();
            Assert.True(app.Gestures.RetryRecovery(recovery).Success);
            Assert.Null(app.Gestures.PendingRecovery);
            Assert.Equal(initial, app.Runtime.State(target));
        }
    }

    [Fact]
    public void Teardown_and_host_invalidation_publish_one_truthful_terminal()
    {
        var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        Assert.True(app.BeginResetImport(receipts.Add).Success);
        Assert.True(app.Imports.IsPending);
        app.Imports.Dispose();
        app.Imports.Dispose();
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.Cancelled, terminal.State);
        Assert.True(terminal.Recovery?.Complete);
        Assert.False(app.Imports.IsPending);
        Assert.Single(app.Runtime.RestoreCalls);
        app.Dispose();

        using var invalidated = new PoseImportCaptureHarness();
        var invalidatedReceipts = new List<OperationReceipt>();
        Assert.True(invalidated.BeginWriteImport(invalidatedReceipts.Add).Success);
        invalidated.Imports.InvalidateForHostTeardown("dispatch failed");
        invalidated.FireRegisteredNativeAction();
        invalidated.EndRegisteredNativeBatch();
        terminal = Assert.Single(invalidatedReceipts);
        Assert.Equal(OperationReceiptState.Failed, terminal.State);
        Assert.Null(terminal.Recovery);
        Assert.Equal(0, invalidated.InteractiveStackCount);
        Assert.False(invalidated.Imports.IsPending);
    }

    [Fact]
    public void Exact_identity_no_op_and_legacy_receipt_compatibility_remain_observable()
    {
        using (var app = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            Assert.True(app.BeginModelImport(0, receipts.Add).Success);
            Assert.Empty(app.Runtime.ApplyCalls);
            app.RunNextDelay(0);
            Assert.Equal(OperationReceiptState.Applied, Assert.Single(receipts).State);
            Assert.Empty(app.Runtime.ApplyCalls);
            Assert.Null(app.History.PeekUndo());
        }

        using (var app = new PoseImportCaptureHarness())
        {
            // Issue #78: actor identity is the admitted (lineage, generation),
            // not a wrapper object. Replacing the wrapper at an UNCHANGED
            // identity between Reserve and Begin is invisible to the import —
            // it resolves the live wrapper through the registry and applies.
            var receipts = new List<OperationReceipt>();
            var reserved = app.Imports.Reserve(
                app.Actor,
                "replacement import",
                out var operation,
                onReceipt: receipts.Add);
            Assert.True(reserved.Success);
            app.ReplaceActorObjectAtSameLogicalIdentity();
            var begun = app.Imports.Begin(operation!, app.CreateModelPlan(3));
            Assert.True(begun.Success, begun.Detail);
            Assert.Single(app.Runtime.ApplyCalls);
            app.RunIfQueued(0);
            Assert.Equal(OperationReceiptState.Applied, Assert.Single(
                receipts,
                receipt => receipt.State != OperationReceiptState.Pending).State);
            Assert.Equal(app.ActorId, receipts[^1].TargetActorId);
            Assert.False(app.Imports.IsPending);
        }

        var id = Guid.NewGuid();
        var session = SessionGeneration.New();
        var actor = TestIds.Actor(4);
        var receipt = OperationReceipt.Pending(
            id, OperationEpoch.First, session, actor, "Import test");
        Assert.Equal(id, receipt.OperationId);
        Assert.Equal(OperationEpoch.First, receipt.OperationEpoch);
        Assert.Equal(session, receipt.SessionGeneration);
        Assert.Equal(actor, receipt.TargetActorId);
        Assert.Equal(OperationReceiptState.Pending, receipt.State);

        var legacy = new PoseEditResult(true, 3, "same");
        var withReceipt = legacy with { OperationReceipt = receipt };
        Assert.Equal(legacy, withReceipt);
        Assert.Same(receipt, withReceipt.OperationReceipt);
    }

    [Fact]
    public void Deferred_head_and_reconcile_failures_roll_back_once_and_ignore_stale_callbacks()
    {
        RunDeferredRollback(app => app.ThrowOnPoseInfoCall = 1, headRestore: true);
        RunDeferredRollback(app => app.ThrowOnRegisterCall = 2, headRestore: true);
        RunDeferredRollback(app => app.ThrowOnPoseInfoCall = 1);
        RunDeferredRollback(app => app.Runtime.ThrowCaptureCall = 2);
        RunDeferredRollback(app => app.ThrowOnRegisterCall = 2);
    }

    [Fact]
    public void Deferred_flatten_failures_refreeze_targets_and_restore_all_prior_phases()
    {
        using (var app = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            app.SeedHeadInteractiveStack();
            Assert.True(app.BeginFaceExpressionImport(receipts.Add).Success);
            app.FireCharacterNativeActions();
            app.EndRegisteredNativeBatch();
            app.RunNextDelay(4);
            app.FireCharacterNativeActions();
            app.EndRegisteredNativeBatch();
            app.UseWeaponFlattenPlan();
            app.RunNextDelay(0);
            app.RunNextDelay(2);
            app.ReplaceWeaponSlot();
            app.RunNextDelay(60);
            app.FireWeaponNativeAction();
            var terminal = Assert.Single(receipts);
            Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
            Assert.True(terminal.Recovery?.Complete);
            Assert.False(app.Imports.IsPending);
        }

        using (var app = new PoseImportCaptureHarness())
        {
            var receipts = new List<OperationReceipt>();
            app.SeedHeadInteractiveStack();
            app.PoseFiles.CreatePoseFile(Arg.Any<IReadOnlyList<Poser.Entities.ISkeleton>>())
                .Returns(_ => throw new InvalidOperationException("flatten read exploded"));
            Assert.True(app.BeginFaceExpressionImport(receipts.Add).Success);
            app.FireCharacterNativeActions();
            app.EndRegisteredNativeBatch();
            app.RunNextDelay(4);
            app.FireCharacterNativeActions();
            app.EndRegisteredNativeBatch();
            app.RunNextDelay(0);
            app.UseWeaponFlattenPlan();
            app.RunNextDelay(2);
            var terminal = Assert.Single(receipts);
            Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
            Assert.True(terminal.Recovery?.Complete);
            Assert.False(app.Imports.IsPending);
            Assert.Null(app.History.PeekUndo());
            app.RunNextDelay(60);
            app.FireCharacterNativeActions();
            app.EndRegisteredNativeBatch();
            Assert.Single(receipts);
        }

        RunFlattenRollback(app => app.ThrowDuringBuildImportPlan =
            () => throw new InvalidOperationException("flatten build exploded"));
        RunFlattenRollback(app => app.Runtime.ThrowCaptureCall = 3);
        RunFlattenRollback(app => app.ThrowOnPoseInfoCall = 2);
        RunFlattenRollback(app => app.ThrowOnRegisterCall = 3);
    }

    [Fact]
    public void Completion_callbacks_and_gpose_exit_preserve_terminal_ordering()
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

        using var gposeApp = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        Assert.True(gposeApp.BeginResetImport(receipts.Add).Success);
        var client = Substitute.For<IClientState>();
        var events = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var lifecycle = Substitute.For<ISessionLifecycleCoordinator>();
        lifecycle.OnGposeEntered().Returns(SessionGeneration.New());
        lifecycle.OnGposeExit().Returns(_ =>
        {
            Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
            Assert.False(gposeApp.Imports.IsPending);
            return default;
        });
        events.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(call =>
            {
                if (!call.Arg<GPoseStateChangedEvent>().IsGPosing)
                    Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
            });
        using var gpose = new GPoseService(
            client, gposeApp.Framework, events, log, lifecycle, () => gposeApp.Imports);
        client.IsGPosing.Returns(true);
        gposeApp.Framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(gposeApp.Framework);
        client.IsGPosing.Returns(false);
        gposeApp.Framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(gposeApp.Framework);
        Assert.Equal(OperationReceiptState.Cancelled, Assert.Single(receipts).State);
        Assert.Single(gposeApp.Runtime.RestoreCalls);
    }

    private static void RunDeferredRollback(
        Action<PoseImportCaptureHarness> configure,
        bool headRestore = false)
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.SeedHeadInteractiveStack();
        configure(app);
        Assert.True((headRestore
                ? app.BeginHeadRestoreImport(receipts.Add)
                : app.BeginFaceExpressionImport(receipts.Add)).Success);
        app.FireCharacterNativeActions();
        app.EndRegisteredNativeBatch();
        app.RunNextDelay(4);
        AssertDeferredRollback(app, receipts);
    }

    private static void RunFlattenRollback(Action<PoseImportCaptureHarness> configure)
    {
        using var app = new PoseImportCaptureHarness();
        var receipts = new List<OperationReceipt>();
        app.SeedHeadInteractiveStack();
        configure(app);
        Assert.True(app.BeginFaceExpressionImport(receipts.Add).Success);
        app.ReachFlattenSetup();
        app.RunNextDelay(2);
        AssertDeferredRollback(app, receipts);
    }

    private static void AssertDeferredRollback(
        PoseImportCaptureHarness app,
        IReadOnlyList<OperationReceipt> receipts)
    {
        var terminal = Assert.Single(receipts);
        Assert.Equal(OperationReceiptState.RolledBack, terminal.State);
        Assert.True(terminal.Recovery?.Complete);
        Assert.False(app.Imports.IsPending);
        Assert.Null(app.Gestures.PendingRecovery);
        Assert.NotEmpty(app.Runtime.RestoreCalls);
        Assert.Null(app.History.PeekUndo());

        app.RunIfQueued(60);
        app.FireCharacterNativeActions();
        app.EndRegisteredNativeBatch();
        app.FireWeaponNativeAction();
        app.EndWeaponNativeBatch();
        Assert.Single(receipts);
    }

    private sealed class ModelIdPort : IModelIdRuntimePort
    {
        public int? Current;
        public readonly List<int> Writes = new();

        public int? Read(ActorId actor) => Current;

        public PresentationPortResult Write(ActorId actor, int modelCharaId)
        {
            if (Current is null)
                return PresentationPortResult.Fail("actor unavailable");
            Writes.Add(modelCharaId);
            Current = modelCharaId;
            return PresentationPortResult.Ok();
        }
    }
}
