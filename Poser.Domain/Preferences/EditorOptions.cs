namespace Poser.Services;

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
