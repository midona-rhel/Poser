using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Animation;

public sealed class AnimationInteractionOwnershipTests
{
    [Fact]
    public void Layer_weight_is_held_released_at_one_and_cleared_by_the_layer_reset()
    {
        var actor = ActorId.New();
        var speed = Substitute.For<IAnimationSpeedPort>();
        speed.SetSlotWeight(actor, AnimationSlot.Additive, Arg.Any<float>()).Returns(Outcome.Ok());
        speed.ClearSlotWeight(actor, AnimationSlot.Additive).Returns(Outcome.Ok());
        var session = new AnimationSession(Substitute.For<IAnimationTimelinePort>(), speed,
            Substitute.For<IAnimationStancePort>(), Substitute.For<IAnimationScrubPort>(),
            Substitute.For<IWorldRenderingRuntimePort>());

        Assert.True(session.SetSlotWeight(actor, AnimationSlot.Additive, 0.4f).Success);
        Assert.Equal(0.4f, session.WeightFor(actor, AnimationSlot.Additive));
        speed.Received(1).SetSlotWeight(actor, AnimationSlot.Additive, 0.4f);

        // Above 1 clamps to 1, which releases the hold.
        Assert.True(session.SetSlotWeight(actor, AnimationSlot.Additive, 2f).Success);
        Assert.Equal(1f, session.WeightFor(actor, AnimationSlot.Additive));
        speed.Received(1).ClearSlotWeight(actor, AnimationSlot.Additive);

        Assert.True(session.SetSlotWeight(actor, AnimationSlot.Additive, 0.5f).Success);
        _ = session.ResetSlot(actor, AnimationSlot.Additive);
        Assert.Equal(1f, session.WeightFor(actor, AnimationSlot.Additive));
        speed.Received(2).ClearSlotWeight(actor, AnimationSlot.Additive);
    }

    [Fact]
    public void Old_scrub_owner_cannot_write_or_end_the_new_owners_drag()
    {
        var actor = ActorId.New();
        var control = new ScrubControlId(0, 0);
        var port = Substitute.For<IAnimationScrubPort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        port.EnumerateControls(actor, out Arg.Any<ulong>()).Returns(call =>
        {
            call[1] = 7UL;
            return new[] { new ScrubControlReading(control, 0, 10, 1) };
        });
        speed.SetOverallSpeed(actor, 0).Returns(Outcome.Ok());
        port.SetControlTime(actor, control, Arg.Any<float>(), 7).Returns(Outcome.Ok());
        var session = new AnimationSession(Substitute.For<IAnimationTimelinePort>(), speed,
            Substitute.For<IAnimationStancePort>(), port, Substitute.For<IWorldRenderingRuntimePort>());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(session.BeginScrub(actor, control, first).Success);
        Assert.True(session.BeginScrub(actor, control, second).Success);
        port.ClearReceivedCalls();
        Assert.False(session.UpdateScrub(actor, 1, first).Success);
        session.EndScrub(first);
        port.DidNotReceive().SetControlTime(actor, control, Arg.Any<float>(), Arg.Any<ulong>());
        Assert.True(session.UpdateScrub(actor, 12, second).Success);
        port.Received(1).SetControlTime(actor, control, 10, 7);
        session.EndScrub(second);
        Assert.False(session.UpdateScrub(actor, 1, second).Success);
        Assert.True(session.IsPaused(actor));

        Assert.True(session.BeginScrub(actor, control, first).Success);
        port.SetControlTime(actor, control, Arg.Any<float>(), 7)
            .Returns(Outcome.Fail("Stale skeleton"));
        Assert.False(session.UpdateScrub(actor, 3, first).Success);
        port.ClearReceivedCalls();
        Assert.False(session.UpdateScrub(actor, 4, first).Success);
        port.DidNotReceive().SetControlTime(actor, control, Arg.Any<float>(), Arg.Any<ulong>());

        Assert.True(session.BeginScrub(actor, control, first).Success);
        session.Reconcile(new SceneSnapshot(1, [], [], [], []));
        port.ClearReceivedCalls();
        Assert.False(session.UpdateScrub(actor, 4, first).Success);
        port.DidNotReceive().SetControlTime(actor, control, Arg.Any<float>(), Arg.Any<ulong>());
    }

    [Fact]
    public void Reset_retires_a_gone_body_without_reporting_its_clears()
    {
        // #495: GPose exit reset an original actor whose body was already
        // gone, and every native clear came back as "unrestored" state.
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        timeline.IsSupported(actor).Returns(true);
        speed.SetOverallSpeed(actor, 0).Returns(Outcome.Ok());
        speed.ClearOverallSpeed(actor).Returns(Outcome.Fail("The actor's native body is no longer available."));
        var session = new AnimationSession(timeline, speed,
            Substitute.For<IAnimationStancePort>(), Substitute.For<IAnimationScrubPort>(),
            Substitute.For<IWorldRenderingRuntimePort>());
        Assert.True(session.Pause(actor).Success);

        // A live actor's refused restore is still reported and kept.
        Assert.False(session.ResetActor(actor).Success);
        Assert.True(session.IsPaused(actor));

        timeline.IsSupported(actor).Returns(false);
        timeline.Read(actor).Returns((ActorAnimationReading?)null);
        speed.ClearReceivedCalls();
        Assert.True(session.ResetActor(actor).Success);
        Assert.False(session.IsPaused(actor));
        speed.Received(1).Forget(actor);
        speed.DidNotReceive().ClearOverallSpeed(actor);
    }
}
