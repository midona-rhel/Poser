namespace Poser.Services;

/// <summary>
/// Transform pivot - the center point around which transforms occur.
/// </summary>
public enum TransformPivot
{
    /// <summary>Transform around each object's own origin.</summary>
    Individual,
    /// <summary>Transform around the parent bone's position.</summary>
    Parent,
    /// <summary>Transform around the median center of all selected objects.</summary>
    Median
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
/// Tracks editor-wide state: gizmo settings and posing mode.
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

    /// <summary>
    /// Whether posing mode is enabled. When true, all actors are frozen
    /// and bone manipulation is allowed.
    /// </summary>
    bool IsPosingMode { get; }

    /// <summary>
    /// Toggle posing mode on/off.
    /// </summary>
    void TogglePosingMode();
}
