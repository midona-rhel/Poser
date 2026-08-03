using System;
using Poser.Entities;

using Poser.Game.Types;

namespace Poser.Services;

/// <summary>
/// Service for spawning and destroying actors in GPose.
/// </summary>
public interface IActorSpawnService : IDisposable
{
    /// <summary>
    /// Create a NEW actor (Brio's actor-container "New actor"): spawned as
    /// its own entity, internally seeded from the local player's
    /// appearance exactly as Brio does. The companion slot is reserved
    /// only on request — it costs an extra object slot and is what allows
    /// minions/mounts/ornaments to attach later.
    /// </summary>
    /// <returns>The spawned actor, or null if failed.</returns>
    IActor? SpawnNewActor(bool reserveCompanionSlot);

    /// <summary>
    /// Spawn a clone of an arbitrary scene actor (appearance + position copy —
    /// Brio ActorLifetimeCapability.Clone).
    /// </summary>
    IActor? CloneActor(IActor source);

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

    /// <summary>
    /// Attach a companion/mount/ornament to a character actor. Replaces any
    /// existing one; <see cref="CompanionAttachment.None"/> detaches. The actor
    /// must have a companion slot (clones spawn with one reserved).
    /// </summary>
    bool SetCompanion(IActor owner, CompanionAttachment container);

    /// <summary>Detach the actor's companion/mount/ornament.</summary>
    void DestroyCompanion(IActor owner);

    /// <summary>Current companion attachment (None when empty or no slot).</summary>
    CompanionAttachment GetCompanionInfo(IActor owner);

    /// <summary>
    /// Whether the actor reserved a companion slot when it spawned. Without
    /// one <see cref="SetCompanion"/> can only fail, so a surface asks before
    /// it offers the choice.
    /// </summary>
    bool HasCompanionSlot(IActor actor);
}
