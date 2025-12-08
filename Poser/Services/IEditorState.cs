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

    /// <summary>
    /// Whether posing mode is enabled. When true, all actors are frozen
    /// and bone manipulation is allowed.
    /// </summary>
    bool IsPosingMode { get; }

    /// <summary>Enter posing mode - freezes all actors and enables bone manipulation.</summary>
    void EnterPosingMode();

    /// <summary>Exit posing mode - unfreezes actors and clears selection.</summary>
    void ExitPosingMode();

    /// <summary>Toggle posing mode on/off.</summary>
    void TogglePosingMode();

    #region Unified Selection (IEntity)

    /// <summary>All currently selected entities.</summary>
    IReadOnlyList<IEntity> SelectedEntities { get; }

    /// <summary>Primary selected entity (first in selection).</summary>
    IEntity? PrimarySelection { get; }

    /// <summary>Select a single entity (clears previous selection).</summary>
    void Select(IEntity entity);

    /// <summary>Add an entity to the current selection (Ctrl+click).</summary>
    void AddToSelection(IEntity entity);

    /// <summary>Remove an entity from the current selection.</summary>
    void RemoveFromSelection(IEntity entity);

    /// <summary>Toggle entity selection (add if not selected, remove if selected).</summary>
    void ToggleSelection(IEntity entity);

    /// <summary>Select a range of entities (Shift+click).</summary>
    void SelectRange(IEntity from, IEntity to);

    /// <summary>Check if an entity is currently selected.</summary>
    bool IsSelected(IEntity entity);

    /// <summary>Clear all selection.</summary>
    void ClearSelection();

    #endregion

    #region Convenience accessors

    /// <summary>Get selected entities of a specific type.</summary>
    IEnumerable<T> GetSelected<T>() where T : IEntity;

    /// <summary>Get the primary selected bone (if any bone is selected).</summary>
    IBone? SelectedBone { get; }

    /// <summary>Get the primary selected actor (if any actor is selected).</summary>
    IActor? SelectedActor { get; }

    /// <summary>Get the current gizmo target type based on selection state.</summary>
    GizmoTargetType GetGizmoTargetType();

    #endregion

    #region Category Selection

    /// <summary>Currently selected category ID (if any).</summary>
    string? SelectedCategory { get; }

    /// <summary>The skeleton that the selected category belongs to.</summary>
    ISkeleton? SelectedCategorySkeleton { get; }

    /// <summary>Select a bone category (clears entity selection).</summary>
    void SelectCategory(string categoryId, ISkeleton skeleton);

    /// <summary>Clear category selection.</summary>
    void ClearCategorySelection();

    /// <summary>Check if a category is selected.</summary>
    bool IsCategorySelected(string categoryId);

    /// <summary>Get all bones in the selected category (if any).</summary>
    IReadOnlyList<IBone> GetSelectedCategoryBones();

    #endregion
}
