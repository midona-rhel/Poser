using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>Puts back values read earlier, for undo and redo of a move and
/// after a change that failed, and words what could not be put back.</summary>
internal static class TransformRecovery
{
    public static TransformRecoveryReceipt RestoreAll(
        ITransformRuntimePort runtime,
        IEnumerable<TransformTargetState> states)
    {
        var attempts = new List<TransformRecoveryAttempt>();
        foreach (var state in states.OrderBy(state => runtime.DependencyDepth(state.Target)))
        {
            TransformPortResult result;
            try
            {
                result = runtime.Restore(state);
            }
            catch (Exception exception)
            {
                // A game call that throws may have changed something, like
                // one that fails. Note it and go on, so every value is tried
                // and the result lists every one that failed.
                result = TransformPortResult.Fail(
                    TransformPortStatus.NativeUnavailable,
                    $"Restore threw for {state.Target}: {exception.Message}");
            }
            attempts.Add(new TransformRecoveryAttempt(
                state,
                result.Status,
                result.Detail));
        }
        return new TransformRecoveryReceipt(attempts);
    }

    public static string AppendRollbackFailure(
        string primaryFailure,
        TransformRecoveryReceipt recovery) =>
        recovery.Complete
            ? primaryFailure
            : $"{primaryFailure} Rollback also failed: {DescribeFailures(recovery)}";

    public static string DescribeFailures(
        TransformRecoveryReceipt recovery) =>
        string.Join(
            "; ",
            recovery.Failures.Select(failure =>
                failure.Detail ??
                $"Could not restore {failure.RequestedState.Target}."));
}
