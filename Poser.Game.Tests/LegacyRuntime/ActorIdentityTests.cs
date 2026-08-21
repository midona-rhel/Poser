using Poser.Core;
using Poser.Game;

namespace Poser.Game.Tests.LegacyRuntime;

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
    public void Two_actors_sharing_a_game_object_id_get_different_identities()
    {
        // Exactly the clone case: same GameObjectId, different table slots.
        const ulong shared = 0x4000_0001UL;
        Assert.NotEqual(
            Identity(shared, 201),
            Identity(shared, 204));
    }

    [Fact]
    public void One_actor_keeps_its_identity_across_refreshes()
    {
        // Stability is what the lineage depends on: the same actor in the same
        // slot must mint the same id every scan, or its generation churns and
        // every binding is rebuilt for nothing.
        const ulong id = 0x4000_0002UL;

        Assert.Equal(
            Identity(id, 207),
            Identity(id, 207));
    }

    [Fact]
    public void A_reused_slot_with_a_different_actor_is_a_different_identity()
    {
        // Clear-first frees a slot and the respawn can land in it. A different
        // actor in a reused slot must not inherit the dead one's bindings.
        Assert.NotEqual(
            Identity(0x4000_0003UL, 203),
            Identity(0x4000_0004UL, 203));
    }
}
