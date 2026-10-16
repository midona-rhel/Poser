using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Animation;

/// <summary>Layers are independent: one layer's pause is never another's.</summary>
public sealed class LayerIsolationTests
{
    private const ushort Toast = 6310, EatAppleUpper = 8008;

    [Fact]
    public void Pausing_the_only_moving_layer_does_not_pause_the_next_layer_applied()
    {
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        var live = new Dictionary<AnimationSlot, ushort> { [AnimationSlot.Base] = Toast };
        timeline.TimelineSlot(EatAppleUpper).Returns(AnimationSlot.UpperBody);
        timeline.Read(actor).Returns(_ => ActorAnimationReading.Empty with
        {
            BaseTimeline = live[AnimationSlot.Base],
            Slots = live.Select(e => new AnimationSlotReading(e.Key, e.Value, 1f)).ToArray(),
        });
        timeline.Blend(actor, EatAppleUpper, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
            .Returns(_ => { live[AnimationSlot.UpperBody] = EatAppleUpper; return Outcome.Ok(); });
        speed.SetSlotSpeed(actor, Arg.Any<AnimationSlot>(), Arg.Any<float>()).Returns(Outcome.Ok());
        speed.SetOverallSpeed(actor, Arg.Any<float>()).Returns(Outcome.Ok());
        var session = new AnimationSession(timeline, speed, Substitute.For<IAnimationStancePort>(),
            Substitute.For<IAnimationScrubPort>(), Substitute.For<IWorldRenderingRuntimePort>());

        Assert.True(session.PauseSlot(actor, AnimationSlot.Base).Success);
        Assert.False(session.IsPaused(actor));

        Assert.True(session.ChooseSlot(actor, AnimationSlot.UpperBody, EatAppleUpper).Success);
        Assert.True(session.PlaySelectedSlot(actor, AnimationSlot.UpperBody, null, false, resume: false).Success);

        speed.DidNotReceive().SetOverallSpeed(actor, 0f);
        speed.DidNotReceive().SetSlotSpeed(actor, AnimationSlot.UpperBody, 0f);
        Assert.True(session.AnyPlaying(actor));
    }

    [Fact]
    public void A_layer_that_ran_out_holds_on_its_last_frame_and_play_restarts_it()
    {
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        var scrub = Substitute.For<IAnimationScrubPort>();
        timeline.Read(actor).Returns(ActorAnimationReading.Empty with
        {
            Slots = new[] { new AnimationSlotReading(AnimationSlot.UpperBody, EatAppleUpper, 1f) },
        });
        speed.SetSlotSpeed(actor, Arg.Any<AnimationSlot>(), Arg.Any<float>()).Returns(Outcome.Ok());
        scrub.SetControlTime(actor, ScrubControlId.Timeline(AnimationSlot.UpperBody), 0f, Arg.Any<ulong>())
            .Returns(Outcome.Ok());
        var session = new AnimationSession(timeline, speed, Substitute.For<IAnimationStancePort>(),
            scrub, Substitute.For<IWorldRenderingRuntimePort>());

        timeline.LayerEnded += Raise.Event<System.Action<ActorId, AnimationSlot>>(actor, AnimationSlot.UpperBody);

        speed.Received(1).SetSlotSpeed(actor, AnimationSlot.UpperBody, 0f);
        Assert.Contains(AnimationSlot.UpperBody, session.OverridesFor(actor).EndHeldSlots);

        Assert.True(session.ResumeSlot(actor, AnimationSlot.UpperBody).Success);

        scrub.Received(1).SetControlTime(actor, ScrubControlId.Timeline(AnimationSlot.UpperBody), 0f, Arg.Any<ulong>());
        Assert.DoesNotContain(AnimationSlot.UpperBody, session.OverridesFor(actor).EndHeldSlots);
    }
}
