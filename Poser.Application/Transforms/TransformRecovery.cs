namespace Poser.Application.Transforms;

/// <summary>One ordered attempt to restore an exact captured target state.</summary>
public sealed record TransformRecoveryAttempt(
    TransformTargetState RequestedState,
    TransformPortStatus Status,
    string? Detail = null)
{
    public bool Success => Status == TransformPortStatus.Success;
}

/// <summary>
/// Typed evidence for an exhaustive restore sweep. Requested states remain the
/// frozen retry payload; a failed port call is mutation-unknown. Receipts have
/// reference identity so only the exact current pending token can be retried.
/// </summary>
public sealed class TransformRecoveryReceipt
{
    private readonly IReadOnlyList<TransformRecoveryAttempt> _attempts;
    private readonly IReadOnlyList<TransformRecoveryAttempt> _failures;

    public TransformRecoveryReceipt(
        IEnumerable<TransformRecoveryAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var snapshot = attempts.ToArray();
        _attempts = Array.AsReadOnly(snapshot);
        _failures = Array.AsReadOnly(
            snapshot.Where(attempt => !attempt.Success).ToArray());
    }

    public IReadOnlyList<TransformRecoveryAttempt> Attempts => _attempts;
    public IReadOnlyList<TransformRecoveryAttempt> Failures => _failures;
    public bool Complete => _failures.Count == 0;
}

/// <summary>Single owner for ordered, exhaustive transform restoration.</summary>
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
