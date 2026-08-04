using System.Numerics;
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

/// <summary>Native boundary used by application transform commands.</summary>
public interface ITransformRuntimePort
{
    TransformPortResult Capture(TransformTargetId target);

    /// <summary>
    /// Applies an absolute value. For bones the application basis is the
    /// captured baseline transform; <paramref name="rawBaseline"/> uses
    /// the bone's CURRENT LastRawTransform instead — the pre-reparent
    /// absolute a pose file stores, which diverges from LastTransform on
    /// face partials. The facial bake requires the raw basis.
    /// </summary>
    TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        PoseTransform desired,
        bool rawBaseline = false);

    TransformPortResult Restore(TransformTargetState state);
}
