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
    public TransformParent? Parent { get; init; }
    /// <summary>
    /// Model-space rotation before authored pose edits, captured with this state.
    /// Mirroring uses it to account for the two bones' different starting orientations.
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
