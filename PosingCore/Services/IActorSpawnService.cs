using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for spawning and destroying actors in GPose.
/// </summary>
public interface IActorSpawnService : IDisposable
{
    /// <summary>
    /// Spawn a clone of the local player.
    /// </summary>
    /// <returns>The spawned actor, or null if failed.</returns>
    IActor? SpawnPlayerClone();

    /// <summary>
    /// Destroy a spawned actor.
    /// </summary>
    bool DestroyActor(IActor actor);

    /// <summary>
    /// Set an actor's visibility.
    /// </summary>
    void SetVisibility(IActor actor, bool visible);

    /// <summary>
    /// Get an actor's visibility state.
    /// </summary>
    bool IsVisible(IActor actor);

    /// <summary>
    /// Check if an actor was spawned by this service (and can be destroyed).
    /// </summary>
    bool IsSpawnedActor(IActor actor);
}
