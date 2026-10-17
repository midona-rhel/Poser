using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Animation;

/// <summary>A scene keeps a paused game animation on its frame; undo leaves it the game's.</summary>
public sealed class SceneAnimationCaptureTests
{
    private const ushort RidingIdle = 166;

    private static (AnimationSession Session, ActorId Actor) Rig()
    {
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var speed = Substitute.For<IAnimationSpeedPort>();
        var scrub = Substitute.For<IAnimationScrubPort>();
        timeline.Read(actor).Returns(ActorAnimationReading.Empty with
        {
            Slots = new[] { new AnimationSlotReading(AnimationSlot.Base, RidingIdle, 1f) },
        });
        speed.SetOverallSpeed(actor, Arg.Any<float>()).Returns(Outcome.Ok());
        scrub.FindSlotControl(actor, AnimationSlot.Base, out Arg.Any<ulong>())
            .Returns(new ScrubControlReading(ScrubControlId.Timeline(AnimationSlot.Base), 2.25f, 2.33f, 0f));
        var session = new AnimationSession(timeline, speed, Substitute.For<IAnimationStancePort>(),
            scrub, Substitute.For<IWorldRenderingRuntimePort>());
        return (session, actor);
    }

    [Fact]
    public void A_paused_game_animation_is_saved_held_on_its_frame()
    {
        var (session, actor) = Rig();
        Assert.True(session.Pause(actor).Success);

        var scene = session.CaptureForScene(actor);

        Assert.Equal(RidingIdle, scene.Base.Applied);
        Assert.True(scene.Base.Live);
        Assert.True(scene.Base.Loop);
        Assert.Equal(2.25f, scene.Base.HeldSeconds);
        // Undo's snapshot still leaves the game's idle alone.
        Assert.Null(session.Capture(actor).Base.Applied);
    }

    [Fact]
    public void A_running_game_animation_is_left_to_the_game()
    {
        var (session, actor) = Rig();

        Assert.Null(session.CaptureForScene(actor).Base.Applied);
    }

    [Fact]
    public void A_restored_stance_keeps_the_full_body_layer_put_back_with_it()
    {
        const ushort SitLoop = 654;
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var stance = Substitute.For<IAnimationStancePort>();
        ushort live = 0;
        timeline.Read(actor).Returns(_ => ActorAnimationReading.Empty with
        {
            Slots = new[] { new AnimationSlotReading(AnimationSlot.Base, live, 1f) },
        });
        timeline.TimelineSlot(SitLoop).Returns(AnimationSlot.Base);
        timeline.PlayBase(actor, SitLoop, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
            .Returns(_ => { live = SitLoop; return Outcome.Ok(); });
        timeline.Blend(actor, SitLoop, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
            .Returns(_ => { live = SitLoop; return Outcome.Ok(); });
        stance.SetStance(actor, AnimationStance.SitGround, 0).Returns(Outcome.Ok());
        var session = new AnimationSession(timeline, Substitute.For<IAnimationSpeedPort>(), stance,
            Substitute.For<IAnimationScrubPort>(), Substitute.For<IWorldRenderingRuntimePort>());
        var none = new LayerSnapshot(null, false, false, null, 1f, null, null);
        var target = new AnimationSnapshot(
            new LayerSnapshot(SitLoop, true, true, null, 1f, null, null), none, none, none,
            null, null, null, AnimationStance.SitGround, 0, false, false, false);

        session.Restore(actor, target);

        stance.Received().SetStance(actor, AnimationStance.SitGround, 0);
        // Setting the stance resets the Full body layer: it has to come first.
        Assert.Equal(SitLoop, session.Capture(actor).Base.Applied);
    }
}
