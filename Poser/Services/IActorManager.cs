using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

public interface IActorManager : IDisposable
{
    /// <summary>
    /// Gets the list of actors currently available in GPose.
    /// </summary>
    IReadOnlyList<ActorBase> Actors { get; }

    /// <summary>
    /// Gets the currently selected actors (supports multi-selection).
    /// </summary>
    IReadOnlyList<ActorBase> SelectedActors { get; }

    /// <summary>
    /// Gets the primary selected actor (first in selection).
    /// </summary>
    ActorBase? PrimarySelectedActor { get; }

    /// <summary>
    /// Sets the selection to a single actor.
    /// </summary>
    void Select(ActorBase actor);

    /// <summary>
    /// Sets the selection to multiple actors.
    /// </summary>
    void SelectMultiple(IEnumerable<ActorBase> actors);

    /// <summary>
    /// Adds an actor to the current selection.
    /// </summary>
    void AddToSelection(ActorBase actor);

    /// <summary>
    /// Removes an actor from the current selection.
    /// </summary>
    void RemoveFromSelection(ActorBase actor);

    /// <summary>
    /// Clears all selections.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Checks if an actor is selected.
    /// </summary>
    bool IsSelected(ActorBase actor);

    /// <summary>
    /// Refreshes the actor list from the game.
    /// </summary>
    void RefreshActors();

    /// <summary>
    /// Event fired when the actor list changes.
    /// </summary>
    event Action? OnActorsChanged;

    /// <summary>
    /// Event fired when the selection changes.
    /// </summary>
    event Action<IReadOnlyList<ActorBase>>? OnSelectionChanged;
}
