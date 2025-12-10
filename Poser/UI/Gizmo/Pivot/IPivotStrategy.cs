using System.Collections.Generic;
using Poser.Core;
using Poser.Entities;

namespace Poser.UI.Gizmo.Pivot;

/// <summary>
/// Strategy interface for different pivot transformation modes.
/// Each strategy handles how transforms are applied relative to a pivot point.
/// </summary>
public interface IPivotStrategy
{
    /// <summary>
    /// Apply transformation to bones based on this pivot strategy.
    /// </summary>
    /// <param name="bones">The bones to transform.</param>
    /// <param name="skeleton">The skeleton containing the bones.</param>
    /// <param name="oldPivot">The pivot transform before manipulation.</param>
    /// <param name="newPivot">The pivot transform after manipulation.</param>
    /// <param name="dragState">Captured state from drag start.</param>
    /// <param name="symmetryHandler">Handler for symmetry transforms.</param>
    /// <param name="selectedBoneNames">Names of selected bones (for symmetry deduplication).</param>
    void Apply(
        List<IBone> bones,
        Skeleton skeleton,
        Transform oldPivot,
        Transform newPivot,
        DragState dragState,
        BoneSymmetryHandler symmetryHandler,
        HashSet<string> selectedBoneNames);
}
