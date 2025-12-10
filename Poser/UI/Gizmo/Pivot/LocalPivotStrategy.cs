using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Gizmo.Pivot;

/// <summary>
/// Local pivot strategy: each bone rotates around its own center.
/// For single bone, applies transform directly.
/// For multiple bones, applies same delta to each.
/// </summary>
public class LocalPivotStrategy : IPivotStrategy
{
    private readonly IBonePosingService _bonePosingService;

    public LocalPivotStrategy(IBonePosingService bonePosingService)
    {
        _bonePosingService = bonePosingService;
    }

    public void Apply(
        List<IBone> bones,
        Skeleton skeleton,
        Transform oldPivot,
        Transform newPivot,
        DragState dragState,
        BoneSymmetryHandler symmetryHandler,
        HashSet<string> selectedBoneNames)
    {
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (float.IsNaN(rotationDelta.X) || float.IsNaN(rotationDelta.Y) ||
            float.IsNaN(rotationDelta.Z) || float.IsNaN(rotationDelta.W))
        {
            return;
        }

        const TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation;

        // Single bone: apply directly
        if (bones.Count == 1)
        {
            var bone = bones[0];
            _bonePosingService.ApplyTransform(bone, newPivot, oldPivot, propagate, accumulate: true);
            symmetryHandler.ApplySymmetryTransform(bone, skeleton, selectedBoneNames, newPivot, oldPivot, propagate);
            return;
        }

        // Multiple bones: each rotates in place
        foreach (var bone in bones)
        {
            var newPosition = bone.LastTransform.Position + positionDelta;
            var newRotation = rotationDelta * bone.LastTransform.Rotation;

            var newBoneTransform = new Transform(newPosition, newRotation, bone.LastTransform.Scale);
            _bonePosingService.ApplyTransform(bone, newBoneTransform, bone.LastTransform, propagate, accumulate: true);
            symmetryHandler.ApplySymmetryTransform(bone, skeleton, selectedBoneNames, newBoneTransform, bone.LastTransform, propagate);
        }
    }
}
