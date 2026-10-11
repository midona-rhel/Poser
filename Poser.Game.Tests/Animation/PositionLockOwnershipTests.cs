using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

public sealed class PositionLockOwnershipTests
{
    private static Transform At(float x) => new() { Position = new Vector3(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };

    [Fact]
    public void Unlocking_keeps_a_placement_made_while_locked()
    {
        var actor = ActorId.New();
        var locks = new PositionLockOwnership();

        // Untouched: the lock removes the override it made.
        locks.Lock(actor, At(77.655f));
        Assert.True(locks.Release(actor, At(77.655f)));

        // #486: a placement made while locked is the user's from then on.
        locks.Lock(actor, At(77.655f));
        Assert.False(locks.Release(actor, At(78.655f)));

        // A lock this session never made owns nothing.
        Assert.False(locks.Release(actor, At(77.655f)));
        Assert.False(locks.Release(ActorId.New(), null));
    }
}
