using Poser.Domain.Identity;

namespace Poser.Domain.Transforms;

/// <summary>
/// Result of attempting to restore one captured transform.
/// </summary>
public sealed record TransformRecoveryAttempt(
    TransformTargetState RequestedState,
    TransformPortStatus Status,
    string? Detail = null)
{
    public bool Success => Status == TransformPortStatus.Success;
}

/// <summary>
/// Results of a set of restoration attempts, including the states to retry.
/// A failed attempt may already have changed part of its target; failure does
/// not mean nothing happened.
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
