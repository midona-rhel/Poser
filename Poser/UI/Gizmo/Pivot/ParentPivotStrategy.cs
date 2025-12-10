using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Gizmo.Helpers;

namespace Poser.UI.Gizmo.Pivot;

/// <summary>
/// Parent pivot strategy: bones orbit around their parent's position.
/// Uses total rotation from drag start applied to original relative positions.
/// Rotations compound down the hierarchy chain.
/// </summary>
public class ParentPivotStrategy : IPivotStrategy
{
    private readonly IBonePosingService _bonePosingService;

    public ParentPivotStrategy(IBonePosingService bonePosingService)
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
        if (!dragState.DragStartGizmo.HasValue ||
            dragState.RelativePositions == null ||
            dragState.BoneRotations == null)
        {
            return;
        }

        // Total rotation from drag start to now
        var totalRotation = newPivot.Rotation * Quaternion.Inverse(dragState.DragStartGizmo.Value.Rotation);

        if (float.IsNaN(totalRotation.X) || float.IsNaN(totalRotation.Y) ||
            float.IsNaN(totalRotation.Z) || float.IsNaN(totalRotation.W))
        {
            return;
        }

        const TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation;

        // Sort bones by hierarchy depth (parents first)
        var sortedBones = bones.OrderBy(b => BoneHierarchyHelper.GetBoneDepth(b)).ToList();

        // Track new positions for children
        var newPositions = new Dictionary<IBone, Vector3>();

        foreach (var bone in sortedBones)
        {
            if (!dragState.RelativePositions.TryGetValue(bone, out var originalRelativePos))
                continue;
            if (!dragState.BoneRotations.TryGetValue(bone, out var originalBoneRot))
                continue;

            // Count how many ancestors are in the selection - rotation compounds for each
            int selectedAncestorCount = BoneHierarchyHelper.CountSelectedAncestors(bone, sortedBones);

            // Compound rotation: totalRotation applied (1 + selectedAncestorCount) times
            var compoundedRotation = totalRotation;
            for (int i = 0; i < selectedAncestorCount; i++)
            {
                compoundedRotation = totalRotation * compoundedRotation;
            }

            // Orbit center: use parent's NEW position if parent was in selection,
            // otherwise use the DRAG START parent position
            Vector3 orbitCenter;
            if (bone.ParentBone != null && newPositions.TryGetValue(bone.ParentBone, out var parentNewPos))
            {
                orbitCenter = parentNewPos;
            }
            else if (dragState.ParentPositions != null && dragState.ParentPositions.TryGetValue(bone, out var dragStartParentPos))
            {
                orbitCenter = dragStartParentPos;
            }
            else
            {
                orbitCenter = CrossPartialHelper.GetEffectiveParentPosition(bone);
            }

            // Rotate ORIGINAL relative vector by COMPOUNDED rotation
            var rotatedRelative = Vector3.Transform(originalRelativePos, compoundedRotation);
            var newPosition = orbitCenter + rotatedRelative;

            newPositions[bone] = newPosition;

            // Rotate ORIGINAL bone rotation by COMPOUNDED rotation
            var newRotation = compoundedRotation * originalBoneRot;

            Transform originalTransform = dragState.BoneTransforms != null && dragState.BoneTransforms.TryGetValue(bone, out var dragStart)
                ? dragStart
                : bone.LastTransform;

            var newTransform = new Transform(newPosition, newRotation, originalTransform.Scale);
            _bonePosingService.ApplyTransform(bone, newTransform, originalTransform, propagate, accumulate: false);
            symmetryHandler.ApplySymmetryTransform(bone, skeleton, selectedBoneNames, newTransform, originalTransform, propagate);
        }
    }
}
