using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

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
    bool HasOverride);

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

/// <summary>Native boundary used by application transform commands.</summary>
public interface ITransformRuntimePort
{
    TransformPortResult Capture(TransformTargetId target);

    TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        PoseTransform desired);

    TransformPortResult Restore(TransformTargetState state);
}
