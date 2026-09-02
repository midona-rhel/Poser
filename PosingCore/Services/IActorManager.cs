using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Manages the lifecycle of actors in GPose.
///
/// NOTE: Selection is handled by the application SelectionSession, not here.
/// This interface only tracks actor lifecycle (discovery, refresh).
/// </summary>
public interface IActorManager : IDisposable
{
    /// <summary>
    /// Gets the list of actors currently available in GPose.
    /// </summary>
    IReadOnlyList<IActor> Actors { get; }

    /// <summary>
    /// Bodies Poser drives outside the GPose object-table range (201-439) —
    /// currently only the CharaView preview at slot 441. They are minted the
    /// same way as <see cref="Actors"/> so the pose pipeline can reach them,
    /// but they are NEVER part of <see cref="Actors"/>: every picker, pane,
    /// scene snapshot, and auto-save reads that list and must not see them.
    /// </summary>
    IReadOnlyList<IActor> AuxiliaryActors { get; }

    /// <summary>Whether the actor is the player's own character — the
    /// overworld body or its GPose copy, which shares its game object id.
    /// Ownership of character data rests on this and on Poser having
    /// spawned the actor; nothing else may be exported or saved.</summary>
    bool IsLocalPlayer(IActor actor) => false;

    /// <summary>Takes an overworld actor into the scene BY REFERENCE —
    /// Brio's AddFromWorld: the same body, registered with GPose, listed
    /// beside the GPose set until GPose ends. No copy is made.</summary>
    void AdoptWorldActor(nint address) { }

    /// <summary>Whether the actor is an adopted overworld body.</summary>
    bool IsAdopted(IActor actor) => false;

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

    /// <summary>Make an actor the current GPose target (Brio Target action).</summary>
    void SetGPoseTarget(IActor actor);

    // Events are published via EventBus:
    // - ActorListChangedEvent when actor list changes
}
