using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Cameras;
using Poser.Domain.Transforms;

namespace Poser.Application.Viewport;

/// <summary>What the surfaces read from the viewport: presentation-frame
/// transforms by stable id, and the skeleton matrix the gizmo folds in.
/// Reads only; nothing here writes.</summary>
public interface IViewportReads
{
    void RequestBoneSnapshot();
    ColliderViewportState? GetCollider(OverlayId id);
    LightViewportState? GetLight(LightId id);
    ActorId? GameTarget { get; }
    FreeCameraSpeedNotice? CameraSpeedNotice { get; }
    /// <summary>Copies only requested bones into caller-owned buffers, in descriptor order.
    /// Unavailable/stale entries are excluded. Native skeleton resolution happens once.</summary>
    void ReadBonePositions(SkeletonId skeleton, IReadOnlyList<BoneDescriptor> bones,
        Span<bool> included, Span<Vector3> positions);
    bool IsLightAttached(LightId id);
    bool HasActorOverride(ActorId id);
    PoseTransform? GetModelTransform(TransformTargetId target);
    PoseTransform? GetPropTransform(PropId id);
    PoseTransform? GetWorldObjectTransform(WorldObjectId id);
    PoseTransform? GetLightTransform(LightId id);
    PoseTransform? GetActorTransform(ActorId id);
    PoseTransform? GetBoneModelTransform(BoneId id);
    PoseTransform? GetParentModelTransform(BoneId id);
    Matrix4x4? GetSkeletonModelMatrix(BoneId id);
}

public readonly record struct ColliderViewportState(IkCollider Collider, bool Visible, float Alpha)
{
    public IkColliderShape Shape => Collider.Shape;
    public bool Locked => Collider.Locked;
}

public readonly record struct LightViewportState(
    LightKind Kind, bool IsOn, Vector3 Color, float SpotAngle, Vector2 AreaAngle);
