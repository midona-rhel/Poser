namespace Poser.Services;

using System.Numerics;

/// <summary>
/// Transform orientation - which coordinate axes to use for transforms.
/// </summary>
public enum TransformOrientation
{
    /// <summary>Use the object's local coordinate axes.</summary>
    Local,
    /// <summary>Use world coordinate axes.</summary>
    Global
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
/// Skeleton visualization mode for the overlay.
/// </summary>
public enum SkeletonViewMode
{
    /// <summary>Simple dots with lines (Ktisis/Brio-style, default).</summary>
    Default,
    /// <summary>Blender-style bone shapes (diamond/octahedra pointing to child).</summary>
    Octahedra,
    /// <summary>Only balls at joint positions, no connecting geometry.</summary>
    Joints
}

/// <summary>
/// Symmetry mode for paired bone transforms (_l/_r suffix bones).
/// </summary>
public enum SymmetryMode
{
    /// <summary>No symmetry - only transform selected bones.</summary>
    Off,
    /// <summary>Paired bone receives the same transform (both arms up identically).</summary>
    Copy,
    /// <summary>Paired bone receives mirrored transform (left arm up = right arm down).</summary>
    Mirror
}

/// <summary>
/// Tracks editor-wide state: gizmo settings.
///
/// NOTE: Selection is handled by ISelectionService, not here.
/// This interface only tracks editor tool settings.
/// </summary>
public interface IEditorState
{
    /// <summary>Transform orientation - which axes to use for transforms.</summary>
    TransformOrientation TransformOrientation { get; set; }

    /// <summary>Current transform tool (Move, Rotate, Scale).</summary>
    TransformTool TransformTool { get; set; }

    /// <summary>Debug mode - expands all entities and logs untranslated bones.</summary>
    bool DebugMode { get; set; }

    /// <summary>Bone display mode - hierarchy or category grouping.</summary>
    BoneDisplayMode BoneDisplayMode { get; set; }

    /// <summary>Skeleton visualization mode for the overlay.</summary>
    SkeletonViewMode SkeletonViewMode { get; set; }

    /// <summary>When true, only show selected bones in the overlay.</summary>
    bool ShowSelectedBonesOnly { get; set; }

    /// <summary>Symmetry mode for paired bone transforms.</summary>
    SymmetryMode SymmetryMode { get; set; }

    /// <summary>When true, translate-dragging an IK-eligible bone (hands/feet/tails)
    /// solves the chain toward the drag target instead of offsetting the bone alone.</summary>
    bool IkEnabled { get; set; }

    /// <summary>When true, rotate drags ORBIT the bone around the pivot below instead of spinning in place.</summary>
    bool OrbitBoneRotation { get; set; }

    /// <summary>Pivot source for orbit rotation.</summary>
    Poser.Core.OrbitPivotMode OrbitPivot { get; set; }

    /// <summary>User-defined model-space pivot used when OrbitPivot is Custom.</summary>
    Vector3 CustomOrbitPivot { get; set; }

    /// <summary>Orbit computation strategy — switchable in game to compare stability (see OrbitStrategy docs).</summary>
    Poser.Core.OrbitStrategy OrbitStrategy { get; set; }
}
