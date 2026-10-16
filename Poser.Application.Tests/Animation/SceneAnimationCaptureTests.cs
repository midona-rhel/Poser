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
}
