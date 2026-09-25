namespace Poser.Services;

/// <summary>
/// Tracks editor-wide state: gizmo settings.
///
/// NOTE: Selection is handled by the application SelectionSession, not here.
/// This interface only tracks editor tool settings.
/// </summary>
public interface IEditorState
{
    /// <summary>Transform orientation - which axes to use for transforms.</summary>
    TransformOrientation TransformOrientation { get; set; }

    /// <summary>Current transform tool (Move, Rotate, Scale).</summary>
    TransformTool TransformTool { get; set; }

    /// <summary>Skeleton visualization mode for the overlay.</summary>
    SkeletonViewMode SkeletonViewMode { get; set; }

    /// <summary>When true, only show selected bones in the overlay.</summary>
    bool ShowSelectedBonesOnly { get; set; }

    /// <summary>Symmetry mode for paired bone transforms.</summary>
    SymmetryMode SymmetryMode { get; set; }

    /// <summary>The rotation pivot for the rotation gizmos: Self rotates in
    /// place; Parent orbits around the frozen parent pivot.</summary>
    Poser.Core.RotationPivot RotationPivot { get; set; }
}
