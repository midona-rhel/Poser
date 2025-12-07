using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

public interface IActorManager : IDisposable
{
    /// <summary>
    /// Gets the list of actors currently available in GPose.
    /// </summary>
    IReadOnlyList<IActor> Actors { get; }

    /// <summary>
    /// Gets the currently selected actors (supports multi-selection).
    /// </summary>
    IReadOnlyList<IActor> SelectedActors { get; }

    /// <summary>
    /// Gets the primary selected actor (first in selection).
    /// </summary>
    IActor? PrimarySelectedActor { get; }

    /// <summary>
    /// Sets the selection to a single actor.
    /// </summary>
    void Select(IActor actor);

    /// <summary>
    /// Sets the selection to multiple actors.
    /// </summary>
    void SelectMultiple(IEnumerable<IActor> actors);

    /// <summary>
    /// Adds an actor to the current selection.
    /// </summary>
    void AddToSelection(IActor actor);

    /// <summary>
    /// Removes an actor from the current selection.
    /// </summary>
    void RemoveFromSelection(IActor actor);

    /// <summary>
    /// Clears all selections.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Checks if an actor is selected.
    /// </summary>
    bool IsSelected(IActor actor);

    /// <summary>
    /// Refreshes the actor list from the game.
    /// </summary>
    void RefreshActors();

    // Events removed - use EventBus with:
    // - ActorListChangedEvent for actor list changes
    // - SelectionChangedEvent for selection changes
}
