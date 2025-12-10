using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Single source of truth for entity selection.
/// Replaces dual selection in ActorManager and EditorState.
/// </summary>
public interface ISelectionService
{
    /// <summary>
    /// All currently selected entities.
    /// </summary>
    IReadOnlyList<IEntity> Selected { get; }

    /// <summary>
    /// The primary (first) selected entity, shown in properties panel.
    /// </summary>
    IEntity? Primary { get; }

    /// <summary>
    /// The last clicked entity, used for shift-select range operations.
    /// </summary>
    IEntity? LastClicked { get; }

    /// <summary>
    /// Clear current selection and select a single entity.
    /// </summary>
    void Select(IEntity entity);

    /// <summary>
    /// Add an entity to the current selection (Ctrl+click).
    /// </summary>
    void AddToSelection(IEntity entity);

    /// <summary>
    /// Remove an entity from the current selection.
    /// </summary>
    void RemoveFromSelection(IEntity entity);

    /// <summary>
    /// Toggle selection state of an entity.
    /// </summary>
    void ToggleSelection(IEntity entity);

    /// <summary>
    /// Select a range of entities (Shift+click).
    /// Requires context of display order.
    /// </summary>
    void SelectRange(IEntity from, IEntity to, IEnumerable<IEntity> displayOrder);

    /// <summary>
    /// Clear all selection.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Check if an entity is selected.
    /// </summary>
    bool IsSelected(IEntity entity);

    /// <summary>
    /// Get all selected entities of a specific type.
    /// </summary>
    IEnumerable<T> GetSelected<T>() where T : IEntity;

    /// <summary>
    /// Get the first selected entity of a specific type.
    /// </summary>
    T? GetFirstSelected<T>() where T : class, IEntity;
}
