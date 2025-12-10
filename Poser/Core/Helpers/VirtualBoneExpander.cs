using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Core.Helpers;

/// <summary>
/// Expands virtual bones (bone categories) to their constituent bones for transformation.
/// </summary>
public static class VirtualBoneExpander
{
    /// <summary>
    /// Expands virtual bones to their pivot bone only (not all constituents).
    /// Regular bones are passed through unchanged.
    /// Deduplicates: if a VirtualBone's PivotBone is also directly selected, skip it.
    /// </summary>
    public static List<IBone> Expand(IReadOnlyList<IBone> selectedBones)
    {
        // Collect pivot bones from VirtualBones for deduplication
        var pivotBones = new HashSet<IBone>();
        foreach (var bone in selectedBones)
        {
            if (bone is VirtualBone vb && vb.PivotBone != null)
                pivotBones.Add(vb.PivotBone);
        }

        var expandedBones = new List<IBone>();

        foreach (var bone in selectedBones)
        {
            if (bone is VirtualBone virtualBone)
            {
                // VirtualBone: only transform the pivot bone (e.g., "Head" -> neck only)
                if (virtualBone.PivotBone != null)
                {
                    expandedBones.Add(virtualBone.PivotBone);
                }
                // If no pivot bone, this is an averaged category - skip transform
                // (user must select individual bones to transform)
            }
            else
            {
                // Regular bone - skip if it's a pivot bone of a selected VirtualBone
                // (already added via the VirtualBone above)
                if (!pivotBones.Contains(bone))
                    expandedBones.Add(bone);
            }
        }

        return expandedBones;
    }
}
