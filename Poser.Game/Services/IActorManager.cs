using System;
using System.Collections.Generic;
using Poser.Domain.Actors;
using Poser.Game.Entities;

namespace Poser.Game.Services;

/// <summary>
/// Tracks actors available to Poser during GPose. Selection belongs to the
/// application layer; this interface handles discovery and lifecycle only.
/// </summary>
public interface IActorManager : IDisposable
{
    /// <summary>
    /// Gets the list of actors currently available in GPose.
    /// </summary>
    IReadOnlyList<IActor> Actors { get; }

    /// <summary>
    /// Bodies Poser drives outside the GPose object-table range (201-439),
    /// currently the CharaView preview at slot 441. They use ordinary actor
    /// wrappers so the pose pipeline can reach them, but are excluded from
    /// <see cref="Actors"/> and therefore from pickers, scenes, and autosaves.
    /// </summary>
    IReadOnlyList<IActor> AuxiliaryActors { get; }

    /// <summary>Checks this exact body's current native slot, without probing
    /// its retained address. A cached actor list is not proof of lifetime.</summary>
    bool IsAvailable(IActor actor);

    /// <summary>Whether the actor is the player's own character — the
    /// overworld body or its GPose copy, which shares its game object id.
    /// Ownership of character data rests on this and on Poser having
    /// spawned the actor; nothing else may be exported or saved.</summary>
    bool IsLocalPlayer(IActor actor);

    /// <summary>Adds the existing overworld actor to the GPose scene without
    /// copying it. The same body remains registered until it is released or
    /// GPose ends.</summary>
    void AdoptWorldActor(nint address);

    /// <summary>Whether the actor is an adopted overworld body.</summary>
    bool IsAdopted(IActor actor);

    /// <summary>Whether an adopted body is shown; null when the actor is
    /// not a current adoption.</summary>
    bool? AdoptedVisibility(IActor actor);

    /// <summary>Hides or shows an adopted body. False when the actor is not
    /// a current adoption or not a character.</summary>
    bool SetAdoptedVisibility(IActor actor, bool visible);

    /// <summary>Lets an adopted body go: it is seated back where it was
    /// taken and leaves the scene, the world keeping it.</summary>
    void ReleaseWorldActor(nint address);

    /// <summary>
    /// Opts one object-table index into <see cref="AuxiliaryActors"/>. Safe to
    /// call from any thread; the actor appears on a later framework tick.
    /// </summary>
    void RegisterAuxiliary(ushort objectIndex, ActorKind kind);

    /// <summary>Drops a registration made by
    /// <see cref="RegisterAuxiliary"/>. Safe to call from any thread.</summary>
    void UnregisterAuxiliary(ushort objectIndex);

    /// <summary>
    /// Refreshes the actor list from the game.
    /// </summary>
    void RefreshActors();

    /// <summary>
    /// Gets the actor currently targeted in GPose (the orbit focus).
    /// </summary>
    IActor? GetGPoseTarget();

    /// <summary>Makes an actor the current GPose target.</summary>
    void SetGPoseTarget(IActor actor);

    // Actor-list changes are published through ActorListChangedEvent.
}
