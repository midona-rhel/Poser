using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Animation;

/// <summary>The Pose tab holds an expression still; the Animation tab's Facial is a looping layer.</summary>
public sealed class ExpressionOwnershipTests
{
    private const ushort Smile = 605;

    private sealed class Rig
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly IAnimationTimelinePort Timeline = Substitute.For<IAnimationTimelinePort>();
        public readonly IAnimationSpeedPort Speed = Substitute.For<IAnimationSpeedPort>();
        public readonly AnimationSession Session;
        public ushort LiveFacial;

        public Rig()
        {
            Timeline.TimelineSlot(Smile).Returns(AnimationSlot.Facial);
            Timeline.Read(Actor).Returns(_ => ActorAnimationReading.Empty with
            {
                Slots = new[] { new AnimationSlotReading(AnimationSlot.Facial, LiveFacial, 1f) },
            });
            Timeline.Blend(Actor, Smile, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
                .Returns(_ => { LiveFacial = Smile; return Outcome.Ok(); });
            Timeline.SetSlotLoop(Actor, AnimationSlot.Facial, Arg.Any<ushort>()).Returns(Outcome.Ok());
            Timeline.ClearSlotLoop(Actor, AnimationSlot.Facial).Returns(Outcome.Ok());
            Speed.SetSlotSpeed(Actor, AnimationSlot.Facial, Arg.Any<float>()).Returns(Outcome.Ok());
            Speed.ClearSlotSpeed(Actor, AnimationSlot.Facial, Arg.Any<float>()).Returns(Outcome.Ok());
            Speed.ClearOverallSpeed(Actor).Returns(Outcome.Ok());
            Session = new AnimationSession(Timeline, Speed, Substitute.For<IAnimationStancePort>(),
                Substitute.For<IAnimationScrubPort>(), Substitute.For<IWorldRenderingRuntimePort>());
        }

        public void PlayFacialLayer()
        {
            Assert.True(Session.ChooseSlot(Actor, AnimationSlot.Facial, Smile).Success);
            Assert.True(Session.PlaySelectedSlot(Actor, AnimationSlot.Facial, null, false, resume: false).Success);
        }
    }

    [Fact]
    public void Sidebar_play_all_keeps_a_held_expression_held()
    {
        var rig = new Rig();
        Assert.True(rig.Session.HoldExpression(rig.Actor, Smile).Success);
        rig.Speed.ClearReceivedCalls();

        Assert.True(rig.Session.Resume(rig.Actor).Success);

        Assert.Equal(Smile, rig.Session.HeldExpressionFor(rig.Actor));
        rig.Speed.DidNotReceive().SetSlotSpeed(rig.Actor, AnimationSlot.Facial, Arg.Is<float>(s => s > 0f));
        rig.Speed.DidNotReceive().ClearSlotSpeed(rig.Actor, AnimationSlot.Facial, Arg.Any<float>());
    }

    [Fact]
    public void Facial_layer_apply_takes_over_a_held_expression_and_loops_it()
    {
        var rig = new Rig();
        Assert.True(rig.Session.HoldExpression(rig.Actor, Smile).Success);

        Assert.True(rig.Session.PlaySelectedSlot(rig.Actor, AnimationSlot.Facial, null, false, resume: false).Success);

        Assert.Null(rig.Session.HeldExpressionFor(rig.Actor));
        Assert.True(rig.Session.LoopWantedFor(rig.Actor, AnimationSlot.Facial));
        rig.Timeline.Received(1).SetSlotLoop(rig.Actor, AnimationSlot.Facial, Smile);
    }

    [Fact]
    public void Facial_layer_play_icon_takes_over_a_held_expression()
    {
        var rig = new Rig();
        Assert.True(rig.Session.HoldExpression(rig.Actor, Smile).Success);

        Assert.True(rig.Session.ResumeSlot(rig.Actor, AnimationSlot.Facial).Success);

        Assert.Null(rig.Session.HeldExpressionFor(rig.Actor));
    }

    [Fact]
    public void A_held_expression_ends_the_facial_layer_loop()
    {
        var rig = new Rig();
        rig.PlayFacialLayer();
        Assert.True(rig.Session.LoopWantedFor(rig.Actor, AnimationSlot.Facial));

        Assert.True(rig.Session.HoldExpression(rig.Actor, Smile).Success);

        Assert.False(rig.Session.LoopWantedFor(rig.Actor, AnimationSlot.Facial));
        rig.Timeline.Received(1).ClearSlotLoop(rig.Actor, AnimationSlot.Facial);
        Assert.Equal(Smile, rig.Session.HeldExpressionFor(rig.Actor));
    }
}
