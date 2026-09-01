using System;
using Poser.Entities;

using Poser.Domain.Companions;

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
    /// Spawn a catalog entry (minion/mount/accessory) as its OWN actor: a
    /// fresh battle character that first draws as the entry's ModelChara,
    /// classified by the entry's kind (<see cref="GetSpawnedKind"/>).
    /// </summary>
    IActor? SpawnCatalogActor(SpawnCatalogEntry entry);

    /// <summary>The actor's ModelChara row id; 0 is the human base.</summary>
    int GetModelCharaId(IActor actor);

    /// <summary>
    /// Writes the model id and fully redraws the actor (Brio's mechanism:
    /// draw down, wait ready, draw up). The human customize/equipment bytes
    /// survive in DrawData behind a creature model, so writing 0 restores the
    /// human look.
    /// </summary>
    void SetModelCharaId(IActor actor, int modelCharaId);

    /// <summary>The kind a spawned actor was classified as at spawn; null
    /// for plain spawns, clones, and actors not spawned by this service.
    /// </summary>
    CompanionKind? GetSpawnedKind(IActor actor);

    /// <summary>
    /// Destroy a spawned actor.
    /// </summary>
    bool DestroyActor(IActor actor);

    /// <summary>
    /// Removes one CURRENT root actor from the temporary GPose scene. An actor
    /// Poser spawned goes through the same ownership ledger
    /// <see cref="DestroyActor"/> uses; an actor that was already in the GPose
    /// scene is deleted from the temporary GPose object table only.
    ///
    /// <para>Refuses the GPose primary/local actor, companion bodies, stale or
    /// non-root wrappers, and anything not currently standing in the GPose
    /// object-table range. Never edits the overworld actor or persistent game
    /// data — the GPose table is a copy that ends with the session.</para>
    /// </summary>
    bool RemoveActorFromScene(IActor actor);

    /// <summary>
    /// The reason <see cref="RemoveActorFromScene"/> would refuse this actor
    /// right now, in the user's words — or null when removal is admitted.
    /// Read-only: the UI offers the verb only when this is null, and the
    /// mutation re-checks for itself.
    /// </summary>
    string? RemovalRefusal(IActor actor);

    /// <summary>
    /// Set an actor's visibility.
    /// </summary>
    void SetVisibility(IActor actor, bool visible);

    /// <summary>Copies the equipment visibility flags (weapons, headgear,
    /// visor, ears) from <paramref name="source"/> onto <paramref name="target"/>.
    /// A duplicate re-copies them once its body is there: the seed copy
    /// carries the equipment, not the flags.</summary>
    bool CopyEquipmentVisibility(IActor source, IActor target) => false;

    /// <summary>Copies the source's DRAWN customize, equipment and facewear
    /// onto <paramref name="target"/>: what the source shows, whatever its
    /// DrawData says (a sync plugin writes the draw object).</summary>
    bool CopyDrawnAppearance(IActor source, IActor target) => false;

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
    /// existing one; null detaches. The actor must have a companion slot
    /// (clones spawn with one reserved).
    /// </summary>
    bool SetCompanion(IActor owner, CompanionAttachment? container);

    /// <summary>Detach the actor's companion/mount/ornament.</summary>
    void DestroyCompanion(IActor owner);

    /// <summary>Current companion attachment; null when the slot is empty,
    /// absent, or unreadable.</summary>
    CompanionAttachment? GetCompanionInfo(IActor owner);

    /// <summary>
    /// The attached companion AS AN ACTOR — the body itself, which owns a
    /// skeleton and can be posed like any other. The attachment
    /// (<see cref="GetCompanionInfo"/>) says WHICH minion, mount or ornament
    /// sits in the slot; this says which actor it is, and an actor is the only
    /// handle a pose read or write has. Null when the slot is empty or
    /// unreadable, and when the child object has no entry in the actor table
    /// yet — a companion's body builds a few frames after it attaches.
    /// </summary>
    IActor? GetCompanionActor(IActor owner);

    /// <summary>
    /// Whether the actor reserved a companion slot when it spawned. Without
    /// one <see cref="SetCompanion"/> can only fail, so a surface asks before
    /// it offers the choice.
    /// </summary>
    bool HasCompanionSlot(IActor actor);
}
