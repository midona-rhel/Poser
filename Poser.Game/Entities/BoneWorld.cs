using Poser.Domain.Transforms;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace Poser.Game.Entities;

/// <summary>A bone's posed transform in the world: its cached model-space
/// transform through the skeleton's model matrix. The caller decides
/// whether the cache is refreshed first; this only reads it.</summary>
public static class BoneWorld
{
    /// <summary>Read one live model-space bone without relying on display-cache demand.</summary>
    internal static unsafe Transform? ReadCurrent(IBone bone)
    {
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid) return null;
        var native = skeleton.GetGameSkeletonPointer();
        if (native == null || bone.PartialId < 0 || bone.PartialId >= native->PartialSkeletonCount) return null;
        var pose = native->PartialSkeletons[bone.PartialId].GetHavokPose(0);
        if (pose == null || bone.BoneIndex < 0 || bone.BoneIndex >= pose->Skeleton->Bones.Length) return null;
        var value = pose->AccessBoneModelSpace(bone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
        if (value == null) return null;
        // A newly picked bone may not have requested a display snapshot yet.
        // Reading that stale snapshot, then refreshing it during Evaluate,
        // changes the frame underneath the just-calculated attachment offset.
        // Never write LastRawTransform here: it belongs to the posing pass.
        var model = new Transform
        {
            Position = new(value->Translation.X, value->Translation.Y, value->Translation.Z),
            Rotation = new(value->Rotation.X, value->Rotation.Y, value->Rotation.Z, value->Rotation.W),
            Scale = new(value->Scale.X, value->Scale.Y, value->Scale.Z),
        };
        var world = Transform.FromMatrix(model.ToMatrix() * skeleton.GetModelMatrix());
        return TransformMath.IsFinite(world.Position) && TransformMath.IsFinite(world.Rotation) ? world : null;
    }

    /// <summary>Null off an invalid skeleton or when the result is not
    /// finite.</summary>
    public static Transform? Of(IBone bone)
    {
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            return null;
        var world = Transform.FromMatrix(
            bone.LastTransform.ToMatrix() * skeleton.GetModelMatrix());
        return TransformMath.IsFinite(world.Position) && TransformMath.IsFinite(world.Rotation)
            ? world
            : (Transform?)null;
    }
}
