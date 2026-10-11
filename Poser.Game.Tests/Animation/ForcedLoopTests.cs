using Poser.Game.Animation;
using static Poser.Game.Animation.AnimationTimelineRuntimePort;

namespace Poser.Game.Tests.Animation;

public sealed class ForcedLoopTests
{
    [Fact]
    public void A_held_full_body_loop_is_rearmed_never_replayed()
    {
        // #485: a scrub cleared the forced field while paused and the loop
        // replayed from the start under the held frame, then kept running.
        Assert.Equal(ForcedLoop.Rearm, ForcedLoopAction(fieldOwned: false, held: true));
        Assert.Equal(ForcedLoop.Replay, ForcedLoopAction(fieldOwned: false, held: false));
        Assert.Equal(ForcedLoop.Keep, ForcedLoopAction(fieldOwned: true, held: true));
        Assert.Equal(ForcedLoop.Keep, ForcedLoopAction(fieldOwned: true, held: false));
    }
}
