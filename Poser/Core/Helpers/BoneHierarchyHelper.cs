using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Core.Helpers;

/// <summary>
/// Utilities for bone hierarchy traversal and analysis.
/// </summary>
public static class BoneHierarchyHelper
{
    /// <summary>
    /// Get the depth of a bone in the hierarchy (0 = root).
    /// </summary>
    public static int GetBoneDepth(IBone bone)
    {
        int depth = 0;
        var current = bone.ParentBone;
        while (current != null)
        {
            depth++;
            current = current.ParentBone;
        }
        return depth;
    }

    /// <summary>
    /// Count how many of this bone's ancestors are in the selection.
    /// Used for rotation compounding in Parent/Average pivot modes.
    /// </summary>
    public static int CountSelectedAncestors(IBone bone, IReadOnlyList<IBone> selectedBones)
    {
        int count = 0;
        var current = bone.ParentBone;
        while (current != null)
        {
            foreach (var selected in selectedBones)
            {
                if (selected == current)
                {
                    count++;
                    break;
                }
            }
            current = current.ParentBone;
        }
        return count;
    }

    /// <summary>
    /// Check if a bone is an ancestor of another bone.
    /// </summary>
    public static bool IsAncestorOf(IBone potentialAncestor, IBone bone)
    {
        var current = bone.ParentBone;
        while (current != null)
        {
            if (current == potentialAncestor)
                return true;
            current = current.ParentBone;
        }
        return false;
    }
}
