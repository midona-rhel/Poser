using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;

namespace Poser.ContractTests;

public sealed class PoseImportContractTests
{
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
