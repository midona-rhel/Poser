using Poser.Core;
using Poser.Domain.Identity;
using Poser.Game;

namespace Poser.Game.Tests.LegacyRuntime;

/// <summary>
/// The parking lot a pose crosses a redraw in. It is keyed by STABLE identity
/// because the actor address, the skeleton instance and the draw object all
/// change while an actor is rebuilt — an MCDF import being the case that
/// rebuilds them all at once.
///
/// <para>The rule these pin: a parked pose is taken exactly ONCE, by whichever
/// adoption point reaches it first. Both the SkeletonCreated handler and the
/// first store access are adoption points, and a pose that neither takes is a
/// pose the user watches disappear.</para>
/// </summary>
public sealed class PoseCarryoverStoreTests
{
    [Fact]
    public void A_parked_pose_is_taken_once_and_only_by_its_own_slot()
    {
        var store = new PoseCarryoverStore();
        var actor = Guid.NewGuid();
        var pose = new SkeletonPoseInfo();
        store.Park(
            actor,
            PoseSlot.Character,
            new CarryoverEntry(pose, null, global::System.Environment.TickCount64));

        // A different slot on the same actor is a different skeleton.
        Assert.Null(store.Take(actor, PoseSlot.MainHand));
        // A different actor never sees it.
        Assert.Null(store.Take(Guid.NewGuid(), PoseSlot.Character));

        Assert.Same(pose, store.Take(actor, PoseSlot.Character)?.Pose);

        // Taken means taken: a second adoption point must not re-apply a pose
        // the first one already owns.
        Assert.Null(store.Take(actor, PoseSlot.Character));
    }

    [Fact]
    public void A_pose_parked_long_enough_ago_is_never_resurrected()
    {
        var store = new PoseCarryoverStore();
        var actor = Guid.NewGuid();
        store.Park(
            actor,
            PoseSlot.Character,
            new CarryoverEntry(
                new SkeletonPoseInfo(),
                null,
                global::System.Environment.TickCount64 - 120_000));

        // A rebuild that never completed — actor despawned mid reload, GPose
        // torn down — must not drop its pose onto an unrelated later skeleton.
        Assert.Null(store.Take(actor, PoseSlot.Character));
    }

    [Fact]
    public void Re_parking_the_same_slot_keeps_the_newer_pose()
    {
        var store = new PoseCarryoverStore();
        var actor = Guid.NewGuid();
        var older = new SkeletonPoseInfo();
        var newer = new SkeletonPoseInfo();
        long now = global::System.Environment.TickCount64;

        store.Park(actor, PoseSlot.Character, new CarryoverEntry(older, null, now));
        store.Park(actor, PoseSlot.Character, new CarryoverEntry(newer, null, now));

        Assert.Same(newer, store.Take(actor, PoseSlot.Character)?.Pose);
    }
}
