using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Domain.Transforms;

public enum TransformPortStatus
{
    Success,
    StaleTarget,
    IdentityMismatch,
    InvalidTransform,
    NativeUnavailable,
    Rejected,
}
public sealed record TransformTargetState(
    TransformTargetId Target,
    PoseTransform Transform,
    BonePose Pose,
    bool HasOverride)
{
    /// <summary>
    /// The frozen animated/reference model rotation beneath the authored
    /// layers, captured with the state. Counterpart-aware mirroring rebases
    /// transferred deltas through these baselines so opposing bind frames
    /// cannot flip an adjustment backward.
    /// </summary>
    public Quaternion AnimatedBaselineRotation { get; init; } = Quaternion.Identity;
}

public readonly record struct TransformPortResult(
    TransformPortStatus Status,
    string? Detail = null,
    TransformTargetState? State = null)
{
    public bool Success => Status == TransformPortStatus.Success;

    public static TransformPortResult Ok(TransformTargetState? state = null) =>
        new(TransformPortStatus.Success, null, state);

    public static TransformPortResult Fail(
        TransformPortStatus status,
        string detail) =>
        new(status, detail);
}

/// <summary>
/// Native boundary used by serialized Application/framework-thread transform
/// transitions. Implementations may synchronously call observers; Application
/// rejects any mutation reentered through those callbacks.
/// </summary>
