using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Viewport;

/// <summary>What the surfaces read from the viewport: presentation-frame
/// transforms by stable id, and the skeleton matrix the gizmo folds in.
/// Reads only; nothing here writes.</summary>
public interface IViewportReads
{
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
