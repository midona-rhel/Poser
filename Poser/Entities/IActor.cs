using Poser.Entities.Capabilities;

namespace Poser.Entities;

/// <summary>
/// Represents a game character that can be posed and animated.
/// Extends capability interfaces for compile-time type checking.
/// </summary>
public interface IActor : IEntity, ITransformable, IAnimatable, ISkeletonOwner
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
    /// Whether the actor is currently being posed.
    /// </summary>
    bool IsPosing { get; }

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
    /// Begin posing this actor.
    /// </summary>
    void BeginPosing();

    /// <summary>
    /// End posing this actor.
    /// </summary>
    void EndPosing();
}
