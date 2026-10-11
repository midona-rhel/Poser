using Poser.Domain.Actors;

namespace Poser.Game.Entities;

/// <summary>
/// Represents a game character that can be posed and animated.
/// </summary>
public interface IActor : IEntity
{
    /// <summary>
    /// Memory address of the game character object.
    /// </summary>
    nint Address { get; }

    /// <summary>
    /// The type of actor (Player, Companion, BattleNpc, etc.).
    /// </summary>
    ActorKind ActorKind { get; }

    /// <summary>
    /// Returns true if this actor is a companion (minion, mount, pet).
    /// </summary>
    bool IsCompanion { get; }

    /// <summary>
    /// Returns true if this actor is a player character.
    /// </summary>
    bool IsPlayer { get; }

    /// <summary>
    /// Returns true if this actor is an NPC (battle or event).
    /// </summary>
    bool IsNpc { get; }

    /// <summary>
    /// Whether animation controls are available for this entity.
    /// Returns false for companions (minions, mounts) which have limited control.
    /// </summary>
    bool CanControlAnimation { get; }

    /// <summary>
    /// The skeleton owned by this entity, or null if not available.
    /// </summary>
    ISkeleton? Skeleton { get; }
}
