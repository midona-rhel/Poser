using Poser.Domain.Actors;

namespace Poser.Game.Entities;

/// <summary>
/// Represents a game character that can be posed and animated.
/// </summary>
public interface IActor : IEntity
{
    /// <summary>Current native game-character address.</summary>
    nint Address { get; }

    /// <summary>Actor category reported by the game.</summary>
    ActorKind ActorKind { get; }

    bool IsCompanion { get; }

    bool IsPlayer { get; }

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
