using System.Numerics;
using Poser.Entities;

namespace Poser.UI.Gizmo.Helpers;

/// <summary>
/// Utilities for handling cross-partial bones (e.g., face bones parented to head).
/// Cross-partial bones exist in different skeleton partials than their logical parent,
/// which requires special coordinate space handling.
/// </summary>
public static class CrossPartialHelper
{
    /// <summary>
    /// Checks if a bone is a cross-partial bone (parent is in a different partial).
    /// Cross-partial bones (like face bones) require coordinate space transformation.
    /// </summary>
    public static bool IsCrossPartialBone(IBone bone)
    {
        return bone.ParentBone != null && bone.ParentBone.PartialId != bone.PartialId;
    }

    /// <summary>
    /// Finds the partial root bone for a given bone's partial.
    /// Used to handle cross-partial bones (e.g., face bones whose parent is head).
    /// </summary>
    public static IBone? FindPartialRoot(IBone bone)
    {
        var current = bone;
        while (current.ParentBone != null && current.ParentBone.PartialId == bone.PartialId)
        {
            current = current.ParentBone;
        }
        return current.IsPartialRoot ? current : null;
    }

    /// <summary>
    /// Gets the effective parent position, handling cross-partial bones.
    /// For bones in different partials than their parent (e.g., face bones), uses the partial root.
    /// Returns position in post-reparent space (what the UI shows).
    /// </summary>
    public static Vector3 GetEffectiveParentPosition(IBone bone)
    {
        if (bone.ParentBone == null)
            return bone.LastTransform.Position;

        // Cross-partial: parent is in different partial (e.g., face bone -> head)
        if (bone.ParentBone.PartialId != bone.PartialId)
        {
            var partialRoot = FindPartialRoot(bone);
            if (partialRoot != null)
                return partialRoot.LastTransform.Position;
        }

        return bone.ParentBone.LastTransform.Position;
    }

    /// <summary>
    /// Gets the effective parent position in RAW space (before reparenting).
    /// For cross-partial bones, this is where the partial root was BEFORE it was moved to match the head.
    /// </summary>
    public static Vector3 GetEffectiveParentRawPosition(IBone bone)
    {
        if (bone.ParentBone == null)
            return bone.LastRawTransform.Position;

        // Cross-partial: parent is in different partial (e.g., face bone -> head)
        if (bone.ParentBone.PartialId != bone.PartialId)
        {
            var partialRoot = FindPartialRoot(bone);
            if (partialRoot != null)
                return partialRoot.LastRawTransform.Position;
        }

        return bone.ParentBone.LastRawTransform.Position;
    }

    /// <summary>
    /// Gets the effective parent rotation, handling cross-partial bones.
    /// For bones in different partials than their parent (e.g., face bones), uses the partial root.
    /// </summary>
    public static Quaternion GetEffectiveParentRotation(IBone bone)
    {
        if (bone.ParentBone == null)
            return bone.LastTransform.Rotation;

        // Cross-partial case
        if (bone.ParentBone.PartialId != bone.PartialId)
        {
            var partialRoot = FindPartialRoot(bone);
            if (partialRoot != null)
                return partialRoot.LastTransform.Rotation;
        }

        return bone.ParentBone.LastTransform.Rotation;
    }
}
