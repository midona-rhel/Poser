using Poser.Game;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Runtime;

/// <summary>
/// GPose clones may share a GameObjectId, so actor identity also includes the
/// object-table slot occupied by that incarnation.
/// </summary>
public sealed class ActorIdentityTests
{
    private static EntityId Identity(ulong gameObjectId, ushort index) =>
        ActorManager.ActorIdentity.For(gameObjectId, index);

    [Fact]
    public void Identity_is_stable_per_occupant_and_unique_per_slot()
    {
        // The clone case: same GameObjectId, different table slots.
        const ulong shared = 0x4000_0001UL;
        Assert.NotEqual(Identity(shared, 201), Identity(shared, 204));
        // The same actor in the same slot mints the same id every scan.
        Assert.Equal(Identity(shared, 207), Identity(shared, 207));
        // A different actor in a reused slot must not inherit the dead one's bindings.
        Assert.NotEqual(Identity(0x4000_0003UL, 203), Identity(0x4000_0004UL, 203));
    }
}
