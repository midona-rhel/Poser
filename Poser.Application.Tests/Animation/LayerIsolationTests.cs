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
    public void A_layer_the_game_ended_keeps_its_row_and_play_plays_it_again()
    {
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        timeline.TimelineSlot(EatAppleUpper).Returns(AnimationSlot.UpperBody);
        timeline.Read(actor).Returns(ActorAnimationReading.Empty);
        timeline.Blend(actor, EatAppleUpper, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
            .Returns(Outcome.Ok());
        speed.SetSlotSpeed(actor, Arg.Any<AnimationSlot>(), Arg.Any<float>()).Returns(Outcome.Ok());
        var session = new AnimationSession(timeline, speed, Substitute.For<IAnimationStancePort>(),
            Substitute.For<IAnimationScrubPort>(), Substitute.For<IWorldRenderingRuntimePort>());
        Assert.True(session.ChooseSlot(actor, AnimationSlot.UpperBody, EatAppleUpper).Success);

        timeline.LayerEnded += Raise.Event<System.Action<ActorId, AnimationSlot, float>>(
            actor, AnimationSlot.UpperBody, 4.5f);

        // Nothing holds it: the game unloaded it, the row remembers it.
        speed.DidNotReceive().SetSlotSpeed(actor, AnimationSlot.UpperBody, 0f);
        Assert.Equal(4.5f, session.EndedLength(actor, AnimationSlot.UpperBody));

        Assert.True(session.ResumeSlot(actor, AnimationSlot.UpperBody).Success);

        timeline.Received(1).Blend(actor, EatAppleUpper, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>());
        timeline.Received().SetLayerOwned(actor, AnimationSlot.UpperBody, true);
        Assert.Null(session.EndedLength(actor, AnimationSlot.UpperBody));
    }
}
