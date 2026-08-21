using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game;

namespace Poser.Game.Tests.LegacyRuntime;

/// <summary>
/// THE REDRAW CONTRACT, at the seam issue #78 moved it to: the pose store.
///
/// <para>A pose is addressed by (actor, slot) above the write layer and by
/// bone NAME inside that. A redraw builds a new skeleton instance; nothing
/// above the write layer may notice. The store keeps the pose exactly where it
/// was and the next apply pass lands it on whatever instance is live — no
/// parking lot, no adoption, no migration.</para>
///
/// <para>This fixture is the store and two skeleton stand-ins. It does not
/// need the posing stack, which is the point: the seam is names now.</para>
/// </summary>
public sealed class PoseStoreRedrawTests
{
    /// <summary>A skeleton whose only job is to carry an actor identity and a
    /// slot — the two things the store keys on.</summary>
    private sealed class StandInSkeleton(IActor actor, PoseSlot slot)
    {
        public IActor Actor { get; } = actor;
        public PoseSlot Slot { get; } = slot;

        /// <summary>Each stand-in is a distinct instance, exactly as a real
        /// rebuild produces.</summary>
        public EntityId Id { get; } = EntityId.New();
    }

    private sealed class StandInActor(EntityId id) : IActorIdentityOnly
    {
        public EntityId Id { get; } = id;
    }

    private interface IActorIdentityOnly
    {
        EntityId Id { get; }
    }

    /// <summary>The store's key, restated here as the contract rather than
    /// reached into: actor identity plus slot, never the skeleton.</summary>
    private static (string Actor, PoseSlot Slot) KeyOf(StandInSkeleton skeleton) =>
        (skeleton.Actor.Id.Unique, skeleton.Slot);

    [Fact]
    public void A_redraw_does_not_change_where_a_pose_is_filed()
    {
        var actor = new ActorBase(
            new EntityId("actor_4000_201"), "Poser One", 0x1000, ActorKind.Player);
        var before = new StandInSkeleton(actor, PoseSlot.Character);
        var after = new StandInSkeleton(actor, PoseSlot.Character);

        // Two genuinely different instances — the thing a redraw produces.
        Assert.NotEqual(before.Id, after.Id);

        // And the same address in the store. That single equality is what
        // deleted the carryover parking and both adoption points.
        Assert.Equal(KeyOf(before), KeyOf(after));
    }

    [Fact]
    public void Different_slots_and_actors_stay_separate()
    {
        var one = new ActorBase(
            new EntityId("actor_4000_201"), "Poser One", 0x1000, ActorKind.Player);
        var two = new ActorBase(
            new EntityId("actor_4000_204"), "Poser Two", 0x2000, ActorKind.Player);

        Assert.NotEqual(
            KeyOf(new StandInSkeleton(one, PoseSlot.Character)),
            KeyOf(new StandInSkeleton(one, PoseSlot.MainHand)));
        Assert.NotEqual(
            KeyOf(new StandInSkeleton(one, PoseSlot.Character)),
            KeyOf(new StandInSkeleton(two, PoseSlot.Character)));
    }

    [Fact]
    public void Bone_stacks_inside_a_store_are_addressed_by_name()
    {
        var store = new SkeletonPoseInfo();

        var first = store.GetPoseInfo("j_kosi", 0);
        var again = store.GetPoseInfo("j_kosi", 0);
        var other = store.GetPoseInfo("j_sebo_a", 0);

        // Same name, same partial, same stack — no instance anywhere in the
        // lookup, so a rebuilt skeleton finds the pose it authored.
        Assert.Same(first, again);
        Assert.NotSame(first, other);
    }
}
