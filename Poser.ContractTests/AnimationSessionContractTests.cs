using NSubstitute;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.ContractTests;

public sealed class AnimationSessionContractTests
{
    [Fact]
    public void Animation_scrub_rejects_stale_actor_owners()
    {
        var actor = new ActorId(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
        var other = new ActorId(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1);
        var control = new ScrubControlId(0, 1);
        var reading = new ScrubControlReading(control, 0f, 5f, 1f);
        var port = Substitute.For<IAnimationRuntimePort>();
        port.EnumerateControls(actor, out Arg.Any<ulong>())
            .Returns(call =>
            {
                call[1] = 7UL;
                return new[] { reading };
            });
        port.SetOverallSpeed(Arg.Any<ActorId>(), Arg.Any<float>())
            .Returns(AnimationPortResult.Ok());
        port.SetControlTime(
                Arg.Any<ActorId>(), Arg.Any<ScrubControlId>(), Arg.Any<float>(),
                Arg.Any<ulong>())
            .Returns(AnimationPortResult.Ok());

        var session = new AnimationSession(port);
        Assert.True(session.BeginScrub(actor, control).Success);
        Assert.False(session.UpdateScrub(other, 2f).Success);
        port.DidNotReceive().SetControlTime(
            Arg.Any<ActorId>(), Arg.Any<ScrubControlId>(), Arg.Any<float>(),
            Arg.Any<ulong>());
        Assert.True(session.UpdateScrub(actor, 2f).Success);
        port.Received(1).SetControlTime(actor, control, 2f, 7UL);
    }

    /// <summary>
    /// A scene's armed Upper Body repeat has to actually replay. The live
    /// toggle only re-arms this session's own last Apply target, and a restore
    /// has applied nothing — it used to return Ok having armed nothing.
    /// </summary>
    [Fact]
    public void Scene_replay_of_an_upper_body_repeat_plays_the_saved_timeline()
    {
        var actor = new ActorId(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), 1);
        const ushort saved = 412;
        var port = Substitute.For<IAnimationRuntimePort>();
        port.Read(actor).Returns(ActorAnimationReading.Empty);
        port.Blend(
                Arg.Any<ActorId>(), Arg.Any<ushort>(),
                Arg.Any<BaseAnimationCapture?>(),
                out Arg.Any<BaseAnimationCapture?>())
            .Returns(AnimationPortResult.Ok());
        port.SetSlotLoop(
                Arg.Any<ActorId>(), Arg.Any<AnimationSlot>(), Arg.Any<ushort>())
            .Returns(AnimationPortResult.Ok());

        var session = new AnimationSession(port);

        // The live toggle is the no-op this route exists to replace.
        Assert.True(
            session.SetSlotLoop(actor, AnimationSlot.UpperBody, saved, true).Success);
        port.DidNotReceive().SetSlotLoop(
            Arg.Any<ActorId>(), Arg.Any<AnimationSlot>(), Arg.Any<ushort>());

        Assert.True(
            session.ReplaySlotLoop(actor, AnimationSlot.UpperBody, saved).Success);
        port.Received(1).Blend(
            actor, saved, Arg.Any<BaseAnimationCapture?>(),
            out Arg.Any<BaseAnimationCapture?>());
        port.Received(1).SetSlotLoop(actor, AnimationSlot.UpperBody, saved);
        Assert.Equal(
            saved,
            session.OverridesFor(actor).LoopedSlots[AnimationSlot.UpperBody]);
    }

    /// <summary>A repeat the file recorded no timeline for is refused BY NAME.
    /// The one thing it may never be is a silent success.</summary>
    [Fact]
    public void Scene_replay_refuses_an_upper_body_repeat_with_no_timeline()
    {
        var actor = new ActorId(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), 1);
        var port = Substitute.For<IAnimationRuntimePort>();
        port.Read(actor).Returns(ActorAnimationReading.Empty);

        var session = new AnimationSession(port);
        var refused = session.ReplaySlotLoop(actor, AnimationSlot.UpperBody, 0);

        Assert.False(refused.Success);
        Assert.Contains("nothing to replay", refused.Detail);
        port.DidNotReceive().SetSlotLoop(
            Arg.Any<ActorId>(), Arg.Any<AnimationSlot>(), Arg.Any<ushort>());
    }
}
