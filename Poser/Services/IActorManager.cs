using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Manages the lifecycle of actors in GPose.
///
/// NOTE: Selection is handled by ISelectionService, not here.
/// This interface only tracks actor lifecycle (discovery, refresh).
/// </summary>
public interface IActorManager : IDisposable
{
    /// <summary>
    /// Gets the list of actors currently available in GPose.
    /// </summary>
    IReadOnlyList<IActor> Actors { get; }

    /// <summary>
    /// Refreshes the actor list from the game.
    /// </summary>
    void RefreshActors();

    // Events are published via EventBus:
    // - ActorListChangedEvent when actor list changes
}
