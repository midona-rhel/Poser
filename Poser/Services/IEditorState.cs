using System.Collections.Generic;
using Poser.Entities;

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
/// The type of gizmo target.
/// </summary>
public enum GizmoTargetType
{
    /// <summary>No target selected.</summary>
    None,
    /// <summary>Actor(s) selected - object mode.</summary>
    Actor,
    /// <summary>Bone selected - edit mode.</summary>
    Bone
}

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
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

    /// <summary>Currently selected bone (primary, if any).</summary>
    IBone? SelectedBone { get; set; }

    /// <summary>All currently selected bones.</summary>
    IReadOnlyList<IBone> SelectedBones { get; }

    /// <summary>Select a single bone (clears previous selection).</summary>
    void SelectBone(IBone? bone);

    /// <summary>Select multiple bones (clears previous selection).</summary>
    void SelectBones(IEnumerable<IBone> bones);

    /// <summary>Add a bone to the current selection.</summary>
    void AddBoneToSelection(IBone bone);

    /// <summary>Remove a bone from the current selection.</summary>
    void RemoveBoneFromSelection(IBone bone);

    /// <summary>Toggle bone selection (add if not selected, remove if selected).</summary>
    void ToggleBoneSelection(IBone bone);

    /// <summary>Check if a bone is currently selected.</summary>
    bool IsBoneSelected(IBone bone);

    /// <summary>Clear bone selection.</summary>
    void ClearBoneSelection();

    /// <summary>Get the current gizmo target type based on selection state.</summary>
    GizmoTargetType GetGizmoTargetType();

    /// <summary>Toggle edit mode on the given actor.</summary>
    void ToggleEditMode(IActor actor);
}
