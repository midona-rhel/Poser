using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// Service for integrating with Penumbra mod management.
/// </summary>
public interface IPenumbraService : IDisposable
{
    /// <summary>
    /// Whether Penumbra is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Current status of the Penumbra integration.
    /// </summary>
    IPCStatus Status { get; }

    /// <summary>
    /// Gets all available mod collections.
    /// </summary>
    /// <returns>Dictionary of collection ID to name.</returns>
    Dictionary<Guid, string> GetCollections();

    /// <summary>
    /// Gets the current collection assigned to an actor.
    /// </summary>
    /// <param name="actor">The actor to check.</param>
    /// <returns>Collection ID or null if none assigned.</returns>
    Guid? GetCollectionForActor(IActor actor);

    /// <summary>
    /// Sets the collection for an actor.
    /// </summary>
    /// <param name="actor">The actor to modify.</param>
    /// <param name="collectionId">The collection ID to assign.</param>
    void SetCollectionForActor(IActor actor, Guid collectionId);

    /// <summary>
    /// Forces a redraw of an actor to apply mod changes.
    /// </summary>
    /// <param name="actor">The actor to redraw.</param>
    void RedrawActor(IActor actor);
}
