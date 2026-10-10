using Poser.Game;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Runtime;

/// <summary>
/// The actor identity has to be unique among actors that COEXIST, not merely
/// derived from the game's own id.
///
/// <para>A GPose clone shares its source's GameObjectId, so cloning the local
/// player produces an actor the game calls the same thing. Two actors sharing
/// an identity share a binding lineage and the registry's per-actor bone keys,
/// and the second one bound overwrites the first — leaving every bone of the
/// loser resolving to a BoneId that binds to the winner's bone object. That is
/// a bone-dead actor: no pose import, no overlay toggles.</para>
/// </summary>
public sealed class ActorIdentityTests
{
    /// <summary>The identity formula's inputs, stated directly: the test
    /// project has no game-object substitute, and the formula is a pure
    /// function of these two values.</summary>
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
