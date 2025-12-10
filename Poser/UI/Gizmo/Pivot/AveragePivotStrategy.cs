using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Gizmo.Helpers;

namespace Poser.UI.Gizmo.Pivot;

/// <summary>
/// Average pivot strategy: all bones orbit around the average center point.
/// Uses same compounding approach as Parent pivot.
/// Root bones orbit around gizmo, child bones orbit around their parent's new position.
/// </summary>
public class AveragePivotStrategy : IPivotStrategy
{
    private readonly IBonePosingService _bonePosingService;

    public AveragePivotStrategy(IBonePosingService bonePosingService)
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
            dragState.RelativeToGizmo == null ||
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
            if (!dragState.RelativeToGizmo.TryGetValue(bone, out var originalRelativeToGizmo))
                continue;
            if (!dragState.BoneRotations.TryGetValue(bone, out var originalBoneRot))
                continue;

            // Count how many ancestors are in the selection - rotation compounds for each
            int selectedAncestorCount = BoneHierarchyHelper.CountSelectedAncestors(bone, sortedBones);

            // Compound rotation
            var compoundedRotation = totalRotation;
            for (int i = 0; i < selectedAncestorCount; i++)
            {
                compoundedRotation = totalRotation * compoundedRotation;
            }

            // For root bones (no parent in selection): orbit around gizmo (average center)
            // For child bones: orbit around parent's new position
            Vector3 orbitCenter;
            Vector3 originalRelativePos;
            if (bone.ParentBone != null && newPositions.TryGetValue(bone.ParentBone, out var parentNewPos))
            {
                // Child bone: orbit around parent's new position
                orbitCenter = parentNewPos;
                // Use relative-to-parent for children
                if (dragState.RelativePositions != null && dragState.RelativePositions.TryGetValue(bone, out var relToParent))
                    originalRelativePos = relToParent;
                else
                    originalRelativePos = originalRelativeToGizmo;
            }
            else
            {
                // Root bone: orbit around gizmo (average center)
                orbitCenter = newPivot.Position;
                originalRelativePos = originalRelativeToGizmo;
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
