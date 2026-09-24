using Poser.Domain.Transforms;

namespace Poser.Entities;

/// <summary>A bone's posed transform in the world: its cached model-space
/// transform through the skeleton's model matrix. The caller decides
/// whether the cache is refreshed first; this only reads it.</summary>
public static class BoneWorld
{
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
            : null;
    }
}
