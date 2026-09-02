using Poser.Domain.Identity;

namespace Poser.Domain.Transforms;

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
