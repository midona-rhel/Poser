using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

internal static class TransformRecovery
{
    public static TransformRecoveryReceipt RestoreAll(
        ITransformRuntimePort runtime,
        IEnumerable<TransformTargetState> states)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(states);

        var attempts = new List<TransformRecoveryAttempt>();
        foreach (var state in states)
        {
            TransformPortResult result;
            try
            {
                result = runtime.Restore(state);
            }
            catch (Exception exception)
            {
                // A thrown native boundary is mutation-unknown just like an
                // explicit failure. Record it and continue so the receipt is
                // exhaustive and every later frozen baseline is attempted.
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
