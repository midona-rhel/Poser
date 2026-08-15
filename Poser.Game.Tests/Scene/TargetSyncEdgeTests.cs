using Poser.Domain.Identity;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The selection-swap contract. The game target drives the selection, so the
/// question that decides whether the user keeps what they picked is "did the
/// target CHANGE" — and losing the target is not a change.
/// </summary>
public class TargetSyncEdgeTests
{
    private static readonly Guid FirstActor = Guid.NewGuid();
    private static readonly Guid SecondActor = Guid.NewGuid();

    private static ActorId Actor(Guid id) => new(id, 1);

    [Fact]
    public void AFreshTargetIsAnEdge()
    {
        var edges = new TargetSyncEdges();

        Assert.True(edges.TargetMoved(0x100));
    }

    [Fact]
    public void TheSameTargetIsNotAnEdgeTwice()
    {
        var edges = new TargetSyncEdges();
        edges.Record(null, 0x100);

        Assert.False(edges.TargetMoved(0x100));
    }

    [Fact]
    public void NoTargetIsNeverAnEdge()
    {
        var edges = new TargetSyncEdges();
        edges.Record(null, 0x100);

        Assert.False(edges.TargetMoved(0));
    }

    /// <summary>
    /// THE CYCLE, reproduced. Clicking empty world space drops GPose's target;
    /// GPose then restores it. Recording the dropped target made its return
    /// look like a fresh edge, so the promote re-ran and yanked the selection
    /// back onto the actor — over and over, as the user clicked around
    /// (user 2026-08-15).
    /// </summary>
    [Fact]
    public void ATargetLostAndRestoredIsNotASecondEdge()
    {
        var edges = new TargetSyncEdges();

        // Tick 1: the actor is targeted, the selection follows it.
        Assert.True(edges.TargetMoved(0x100));
        edges.Record(Actor(FirstActor), 0x100);

        // Tick 2: the user picks a light in Poser. No actor is primary now,
        // and the target has not moved.
        Assert.False(edges.TargetMoved(0x100));
        edges.Record(null, 0x100);

        // Tick 3: a click on empty world space drops the GPose target.
        Assert.False(edges.TargetMoved(0));
        edges.Record(null, 0);

        // Tick 4: GPose restores the same target. The light must survive.
        Assert.False(edges.TargetMoved(0x100));
    }

    [Fact]
    public void ARealRetargetAfterALossStillLands()
    {
        var edges = new TargetSyncEdges();
        edges.Record(Actor(FirstActor), 0x100);
        edges.Record(null, 0);

        Assert.True(edges.TargetMoved(0x200));
    }

    [Fact]
    public void ASelectedActorIsAnEdgeOnlyOnce()
    {
        var edges = new TargetSyncEdges();

        Assert.True(edges.SelectionMoved(Actor(FirstActor)));
        edges.Record(Actor(FirstActor), 0x100);
        Assert.False(edges.SelectionMoved(Actor(FirstActor)));
        Assert.True(edges.SelectionMoved(Actor(SecondActor)));
    }

    /// <summary>A bone or a light selection yields no actor, and no actor is
    /// not a change: non-actor work must not retarget the game.</summary>
    [Fact]
    public void ANonActorSelectionIsNotAnEdge()
    {
        var edges = new TargetSyncEdges();
        edges.Record(Actor(FirstActor), 0x100);

        Assert.False(edges.SelectionMoved(null));
    }
}
