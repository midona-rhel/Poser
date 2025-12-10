using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Gizmo.Helpers;

namespace Poser.UI.Gizmo.Pivot;

/// <summary>
/// Target pivot strategy: bones orbit around any selected entity.
/// The orbit target can be any entity (bone, actor, pivot point).
/// Similar to Average, but the orbit center is the target entity's position.
/// </summary>
public class TargetPivotStrategy : IPivotStrategy
{
    private readonly IBonePosingService _bonePosingService;
    private readonly IEditorState _editorState;

    public TargetPivotStrategy(IBonePosingService bonePosingService, IEditorState editorState)
    {
        _bonePosingService = bonePosingService;
        _editorState = editorState;
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
        // Need an orbit target for this strategy
        var orbitTarget = _editorState.OrbitTarget;
        if (orbitTarget == null)
        {
            // Fallback to local behavior if no target selected
            ApplyLocalFallback(bones, skeleton, oldPivot, newPivot, symmetryHandler, selectedBoneNames);
            return;
        }

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

        // The orbit center is the target entity's position
        var targetCenter = orbitTarget.Transform.Position;

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

            // For root bones (no parent in selection): orbit around target entity
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
                // Root bone: orbit around target entity
                orbitCenter = targetCenter;
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

    private void ApplyLocalFallback(
        List<IBone> bones,
        Skeleton skeleton,
        Transform oldPivot,
        Transform newPivot,
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
