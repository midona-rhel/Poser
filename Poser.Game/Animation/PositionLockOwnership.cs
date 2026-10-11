using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

/// <summary>
/// Which actor placement overrides a position lock created, and with what
/// value. The lock holds an actor by writing its current placement as the
/// transform override; a placement the user makes while locked replaces that
/// value, and from then on the override is the user's. Releasing the lock
/// clears the override only while it still holds the lock's own value (#486).
/// </summary>
public sealed class PositionLockOwnership
{
    private readonly Dictionary<ActorId, Transform> _held = new();

    public void Lock(ActorId actor, Transform held) => _held[actor] = held;

    /// <summary>Forgets the lock and says whether its override should be
    /// cleared: only when the lock made it and nothing has moved it since.</summary>
    public bool Release(ActorId actor, Transform? current) =>
        _held.Remove(actor, out var held)
        && current is { } placement
        && placement.Position == held.Position
        && placement.Rotation == held.Rotation
        && placement.Scale == held.Scale;
}
