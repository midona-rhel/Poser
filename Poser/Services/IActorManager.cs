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
    /// Gets or sets the currently selected actor.
    /// </summary>
    ActorBase? SelectedActor { get; set; }

    /// <summary>
    /// Refreshes the actor list from the game.
    /// </summary>
    void RefreshActors();

    /// <summary>
    /// Event fired when the actor list changes.
    /// </summary>
    event Action? OnActorsChanged;

    /// <summary>
    /// Event fired when the selected actor changes.
    /// </summary>
    event Action<ActorBase?>? OnSelectedActorChanged;
}
