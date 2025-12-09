namespace Poser.Services;

/// <summary>
/// Transform pivot - the center point around which transforms occur.
/// </summary>
public enum TransformPivot
{
    /// <summary>Gizmo on first selected entity's position.</summary>
    Local,
    /// <summary>Gizmo on parent of first selected (fallback to entity if no parent).</summary>
    Parent,
    /// <summary>Gizmo at average position of all selected entities.</summary>
    Average
}

/// <summary>
/// Transform orientation - which coordinate axes to use for transforms.
/// </summary>
public enum TransformOrientation
{
    /// <summary>Use the object's local coordinate axes.</summary>
    Local,
    /// <summary>Use world coordinate axes.</summary>
    Global,
    /// <summary>Use the parent bone's coordinate axes.</summary>
    Parent
}

/// <summary>
/// Bone display mode for skeleton hierarchy.
/// </summary>
public enum BoneDisplayMode
{
    /// <summary>Show bones in their natural hierarchy.</summary>
    Hierarchy,
    /// <summary>Group bones by category (Head, Arms, Legs, etc.).</summary>
    Category
}

/// <summary>
/// Transform tool - which operation the gizmo performs.
/// </summary>
public enum TransformTool
{
    /// <summary>Move/translate the selection.</summary>
    Move,
    /// <summary>Rotate the selection.</summary>
    Rotate,
    /// <summary>Scale the selection.</summary>
    Scale,
    /// <summary>Combined move, rotate, and scale in one gizmo.</summary>
    Universal
}

/// <summary>
/// Symmetry mode for paired bones (left/right).
/// </summary>
public enum SymmetryMode
{
    /// <summary>No symmetry - only transform selected bone.</summary>
    Off,
    /// <summary>Copy - paired bone gets the same transform.</summary>
    Copy,
    /// <summary>Mirror - paired bone gets mirrored transform across body center.</summary>
    Mirror
}

/// <summary>
/// Skeleton visualization mode for the overlay.
/// </summary>
public enum SkeletonViewMode
{
    /// <summary>Simple dots with lines (Ktisis-style, default).</summary>
    Dots,
    /// <summary>Blender-style bone shapes (diamond/octahedra pointing to child).</summary>
    Octahedra,
    /// <summary>Only balls at joint positions, no connecting geometry.</summary>
    Joints
}

/// <summary>
/// Tracks editor-wide state: gizmo settings.
///
/// NOTE: Selection is handled by ISelectionService, not here.
/// This interface only tracks editor tool settings.
/// </summary>
public interface IEditorState
{
    /// <summary>Transform pivot - the center point for transforms.</summary>
    TransformPivot TransformPivot { get; set; }

    /// <summary>Transform orientation - which axes to use for transforms.</summary>
    TransformOrientation TransformOrientation { get; set; }

    /// <summary>Current transform tool (Move, Rotate, Scale).</summary>
    TransformTool TransformTool { get; set; }

    /// <summary>Debug mode - expands all entities and logs untranslated bones.</summary>
    bool DebugMode { get; set; }

    /// <summary>Bone display mode - hierarchy or category grouping.</summary>
    BoneDisplayMode BoneDisplayMode { get; set; }

    /// <summary>Symmetry mode for paired bones (left/right).</summary>
    SymmetryMode SymmetryMode { get; set; }

    /// <summary>Skeleton visualization mode for the overlay.</summary>
    SkeletonViewMode SkeletonViewMode { get; set; }

    /// <summary>When true, only show selected bones and their symmetry pairs in the overlay.</summary>
    bool ShowSelectedBonesOnly { get; set; }
}
