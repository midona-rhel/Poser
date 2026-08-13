using System.Collections;
using System.Reflection;
using Poser.Application.Operations;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

public sealed class OperationReceiptContractTests
{
    private static readonly Guid OperationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherSessionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActorLineage =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly SessionGeneration Session =
        SessionGeneration.Create(SessionId);
    private static readonly OperationEpoch Epoch = OperationEpoch.First;
    private static readonly ActorId Target = new(ActorLineage, 7);

    [Fact]
    public void Session_generation_is_exact_guid_identity_with_explicit_default_and_no_ordering()
    {
        var generation = SessionGeneration.Create(SessionId);
        var same = SessionGeneration.Create(SessionId);
        var other = SessionGeneration.Create(OtherSessionId);

        Assert.True(generation.IsValid);
        Assert.False(SessionGeneration.Default.IsValid);
        Assert.False(default(SessionGeneration).IsValid);
        Assert.Equal(Guid.Empty, SessionGeneration.Default.Value);
        Assert.Equal(generation, same);
        Assert.NotEqual(generation, other);
        Assert.Equal(generation.GetHashCode(), same.GetHashCode());
        Assert.Equal(SessionId.ToString("D"), generation.ToString());

        generation.Deconstruct(out var identity);
        Assert.Equal(SessionId, identity);
        var interfaces = typeof(SessionGeneration).GetInterfaces();
        Assert.DoesNotContain(typeof(IComparable<SessionGeneration>), interfaces);
        Assert.DoesNotContain(typeof(IComparable), interfaces);
        var orderingOperators = typeof(SessionGeneration)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.IsSpecialName)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("op_LessThan", orderingOperators);
        Assert.DoesNotContain("op_LessThanOrEqual", orderingOperators);
        Assert.DoesNotContain("op_GreaterThan", orderingOperators);
        Assert.DoesNotContain("op_GreaterThanOrEqual", orderingOperators);
        Assert.Throws<ArgumentException>(
            () => SessionGeneration.Create(Guid.Empty));
    }

    [Fact]
    public void Operation_epoch_is_owner_local_ordered_value_advanced_only_by_next()
    {
        var first = OperationEpoch.First;
        var second = first.Next();
        var same = OperationEpoch.Create(first.Value);

        Assert.True(first.IsValid);
        Assert.Equal(1UL, first.Value);
        Assert.False(default(OperationEpoch).IsValid);
        Assert.Equal(first, same);
        Assert.NotEqual(first, second);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.True(first < second);
        Assert.True(second > first);
        Assert.Equal(-1, first.CompareTo(second));
        Assert.Equal(1, second.CompareTo(first));

        second.Deconstruct(out var ordinal);
        Assert.Equal(2UL, ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OperationEpoch.Create(0));
        Assert.Throws<InvalidOperationException>(
            () => default(OperationEpoch).Next());
        Assert.Throws<OverflowException>(
            () => OperationEpoch.Create(ulong.MaxValue).Next());
    }

    [Fact]
    public void All_receipt_factories_expose_the_six_receipt_states_and_legal_evidence()
    {
        var completeRecovery = CompleteRecovery();
        var incompleteRecovery = IncompleteRecovery();

        var pending = OperationReceipt.Pending(
            OperationId, Epoch, Session, Target);
        var applied = OperationReceipt.Applied(
            OperationId, Epoch, Session, Target, "applied");
        var rolledBack = OperationReceipt.RolledBack(
            OperationId, Epoch, Session, Target, "rolled back", completeRecovery);
        var failed = OperationReceipt.Failed(
            OperationId, Epoch, Session, Target, "write failed", completeRecovery);
        var recoveryRequired = OperationReceipt.RecoveryRequired(
            OperationId,
            Epoch,
            Session,
            Target,
            "rollback is incomplete",
            incompleteRecovery);
        var cancelled = OperationReceipt.Cancelled(
            OperationId, Epoch, Session, Target);

        Assert.Equal(OperationReceiptState.Pending, pending.State);
        Assert.Null(pending.Detail);
        Assert.Null(pending.Recovery);
        Assert.Equal(OperationReceiptState.Applied, applied.State);
        Assert.Equal("applied", applied.Detail);
        Assert.Null(applied.Recovery);
        Assert.Equal(OperationReceiptState.RolledBack, rolledBack.State);
        Assert.Equal("rolled back", rolledBack.Detail);
        Assert.Same(completeRecovery, rolledBack.Recovery);
        Assert.Equal(OperationReceiptState.Failed, failed.State);
        Assert.Equal("write failed", failed.Detail);
        Assert.Same(completeRecovery, failed.Recovery);
        Assert.Equal(OperationReceiptState.RecoveryRequired, recoveryRequired.State);
        Assert.Equal("rollback is incomplete", recoveryRequired.Detail);
        Assert.Same(incompleteRecovery, recoveryRequired.Recovery);
        Assert.Equal(OperationReceiptState.Cancelled, cancelled.State);
        Assert.Null(cancelled.Detail);
        Assert.Null(cancelled.Recovery);

        Assert.Equal(
            new[]
            {
                OperationReceiptState.Pending,
                OperationReceiptState.Applied,
                OperationReceiptState.RolledBack,
                OperationReceiptState.Failed,
                OperationReceiptState.RecoveryRequired,
                OperationReceiptState.Cancelled,
            },
            Enum.GetValues<OperationReceiptState>());
    }

    [Fact]
    public void Receipt_preserves_exact_identity_and_record_value_semantics()
    {
        var recovery = CompleteRecovery();
        var receipt = OperationReceipt.RolledBack(
            OperationId,
            Epoch,
            Session,
            Target,
            "rollback complete",
            recovery);

        Assert.Equal(OperationId, receipt.OperationId);
        Assert.Equal(Epoch, receipt.OperationEpoch);
        Assert.Equal(Session, receipt.SessionGeneration);
        Assert.Equal(Target, receipt.TargetActorId);
        Assert.Equal(OperationReceiptState.RolledBack, receipt.State);
        Assert.Equal("rollback complete", receipt.Detail);
        Assert.Same(recovery, receipt.Recovery);

        var equivalent = OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            OperationReceiptState.RolledBack,
            "rollback complete",
            recovery);
        Assert.Equal(receipt, equivalent);
        Assert.Equal(receipt.GetHashCode(), equivalent.GetHashCode());

        var copy = receipt with { };
        Assert.NotSame(receipt, copy);
        Assert.Equal(receipt, copy);
        Assert.Equal(receipt.GetHashCode(), copy.GetHashCode());
        Assert.Same(recovery, copy.Recovery);

        var (
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            state,
            detail,
            deconstructedRecovery) = receipt;
        Assert.Equal(OperationId, operationId);
        Assert.Equal(Epoch, operationEpoch);
        Assert.Equal(Session, sessionGeneration);
        Assert.Equal(Target, targetActorId);
        Assert.Equal(OperationReceiptState.RolledBack, state);
        Assert.Equal("rollback complete", detail);
        Assert.Same(recovery, deconstructedRecovery);
    }

    [Fact]
    public void Recovery_evidence_keeps_identity_and_defensive_snapshot()
    {
        var attempts = new List<TransformRecoveryAttempt>
        {
            new(RecoveryState(), TransformPortStatus.NativeUnavailable, "native restore failed"),
        };
        var recovery = new TransformRecoveryReceipt(attempts);
        var receipt = OperationReceipt.RecoveryRequired(
            OperationId,
            Epoch,
            Session,
            Target,
            "restore must be retried",
            recovery);

        attempts.Clear();

        Assert.Same(recovery, receipt.Recovery);
        Assert.Single(receipt.Recovery!.Attempts);
        Assert.Single(receipt.Recovery.Failures);
        Assert.Equal("native restore failed", receipt.Recovery.Failures[0].Detail);
        Assert.Throws<NotSupportedException>(
            () => ((IList<TransformRecoveryAttempt>)receipt.Recovery.Attempts)
                .Add(new(RecoveryState(), TransformPortStatus.Success)));
    }

    [Fact]
    public void Distinct_recovery_instances_make_receipts_unequal_by_deliberate_reference_identity()
    {
        var first = OperationReceipt.RolledBack(
            OperationId,
            Epoch,
            Session,
            Target,
            "same detail",
            CompleteRecovery());
        var second = OperationReceipt.RolledBack(
            OperationId,
            Epoch,
            Session,
            Target,
            "same detail",
            CompleteRecovery());

        Assert.NotSame(first.Recovery, second.Recovery);
        Assert.NotEqual(first, second);
        // Unequal objects are permitted to share a hash code; only equal
        // receipts are required to hash equally by the .NET contract.
        Assert.Equal(first, first with { });
        Assert.Equal(first.GetHashCode(), (first with { }).GetHashCode());
    }

    [Fact]
    public void Invalid_default_and_contradictory_receipts_are_rejected()
    {
        var completeRecovery = CompleteRecovery();
        var incompleteRecovery = IncompleteRecovery();

        Assert.Throws<ArgumentException>(() => OperationReceipt.Pending(
            Guid.Empty, Epoch, Session, Target));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Pending(
            OperationId, default, Session, Target));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Pending(
            OperationId, Epoch, default, Target));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Pending(
            OperationId, Epoch, Session, default));

        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            OperationReceiptState.Pending,
            recovery: incompleteRecovery));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            OperationReceiptState.Applied,
            recovery: completeRecovery));
        Assert.Throws<ArgumentNullException>(() => OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            OperationReceiptState.RecoveryRequired,
            "missing recovery"));
        Assert.Throws<ArgumentException>(() => OperationReceipt.RecoveryRequired(
            OperationId,
            Epoch,
            Session,
            Target,
            "recovery is already complete",
            completeRecovery));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Failed(
            OperationId,
            Epoch,
            Session,
            Target,
            "   "));
        Assert.Throws<ArgumentException>(() => OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            OperationReceiptState.Failed,
            "failed",
            incompleteRecovery));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperationReceipt.Create(
            OperationId,
            Epoch,
            Session,
            Target,
            (OperationReceiptState)999));
    }

    [Fact]
    public void Receipt_contract_has_no_public_constructor_or_mutable_static_state()
    {
        Assert.Empty(typeof(OperationReceipt).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));
        foreach (var property in typeof(OperationReceipt).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.False(
                property.SetMethod?.IsPublic == true,
                $"{property.Name} must remain read-only.");
        }

        AssertNoMutableStaticFields(typeof(SessionGeneration));
        AssertNoMutableStaticFields(typeof(OperationEpoch));
        AssertNoMutableStaticFields(typeof(OperationReceipt));
    }

    private static TransformRecoveryReceipt CompleteRecovery() =>
        new(
        [
            new TransformRecoveryAttempt(RecoveryState(), TransformPortStatus.Success),
        ]);

    private static TransformRecoveryReceipt IncompleteRecovery() =>
        new(
        [
            new TransformRecoveryAttempt(
                RecoveryState(),
                TransformPortStatus.Rejected,
                "restore failed"),
        ]);

    private static TransformTargetState RecoveryState() =>
        new(
            TransformTargetId.ForActor(Target),
            PoseTransform.Identity,
            new BonePose(),
            HasOverride: false);

    private static void AssertNoMutableStaticFields(Type type)
    {
        foreach (var field in type.GetFields(
                     BindingFlags.Public |
                     BindingFlags.NonPublic |
                     BindingFlags.Static))
        {
            Assert.True(
                field.IsLiteral || field.IsInitOnly,
                $"{type.Name}.{field.Name} must not be mutable static state.");
        }
    }
}
