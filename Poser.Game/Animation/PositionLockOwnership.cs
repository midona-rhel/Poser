using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

/// <summary>
/// Records the placement override created by each position lock. Releasing a
/// lock clears the override only when its value has not changed since locking.
/// </summary>
public sealed class PositionLockOwnership
{
    private readonly Dictionary<ActorId, Transform> _held = new();

    public void Lock(ActorId actor, Transform held) => _held[actor] = held;

    /// <summary>Forgets the lock and returns whether its unchanged override
    /// should be cleared.</summary>
    public bool Release(ActorId actor, Transform? current) =>
        _held.Remove(actor, out var held)
        && current is { } placement
        && placement.Position == held.Position
        && placement.Rotation == held.Rotation
        && placement.Scale == held.Scale;
}
