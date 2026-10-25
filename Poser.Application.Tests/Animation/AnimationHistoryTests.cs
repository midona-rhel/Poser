using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using static Poser.Application.Tests.Fixtures.JournalUndo;

namespace Poser.Application.Tests.Animation;

/// <summary>Every animation action is one undo step of the actor's snapshot.</summary>
public sealed class AnimationHistoryTests
{
    private const ushort EatAppleUpper = 8008;

    private sealed class Rig
    {
        public readonly ActorId Actor = ActorId.New();
        public readonly IAnimationTimelinePort Timeline = Substitute.For<IAnimationTimelinePort>();
        public readonly IAnimationSpeedPort Speed = Substitute.For<IAnimationSpeedPort>();
        public readonly IAnimationScrubPort Scrub = Substitute.For<IAnimationScrubPort>();
        public readonly EditHistory History = new();
        public readonly ValueJournal Journal;
        public readonly AnimationSession Session;
        public float Clock = 2.5f;
        public ushort Live;

        public Rig()
        {
            Journal = new ValueJournal(History);
            Timeline.TimelineSlot(EatAppleUpper).Returns(AnimationSlot.UpperBody);
            Timeline.Read(Actor).Returns(_ => ActorAnimationReading.Empty with
            {
                Slots = new[] { new AnimationSlotReading(AnimationSlot.UpperBody, Live, 1f) },
            });
            Timeline.Blend(Actor, EatAppleUpper, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
                .Returns(_ => { Live = EatAppleUpper; return Outcome.Ok(); });
            Speed.SetSlotSpeed(Actor, Arg.Any<AnimationSlot>(), Arg.Any<float>()).Returns(Outcome.Ok());
            Speed.ClearSlotSpeed(Actor, Arg.Any<AnimationSlot>()).Returns(Outcome.Ok());
            Scrub.FindSlotControl(Actor, AnimationSlot.UpperBody, out Arg.Any<ulong>())
                .Returns(_ => new ScrubControlReading(ScrubControlId.Timeline(AnimationSlot.UpperBody), Clock, 12f, 0f));
            Scrub.SetControlTime(Actor, Arg.Any<ScrubControlId>(), Arg.Any<float>(), Arg.Any<ulong>()).Returns(Outcome.Ok());
            Session = new AnimationSession(Timeline, Speed, Substitute.For<IAnimationStancePort>(),
                Scrub, Substitute.For<IWorldRenderingRuntimePort>());
            // Match the journal callback installed by AnimationSteps in production.
            Session.Journal = (actor, description, action) =>
            {
                var before = Session.Capture(actor);
                var result = action();
                if (result.Success)
                    Journal.Record((actor, "Animation", description), description, before, Session.Capture(actor),
                        snapshot => Session.Restore(actor, snapshot));
                return result;
            };
            Assert.True(Session.ChooseSlot(Actor, AnimationSlot.UpperBody, EatAppleUpper).Success);
            Assert.True(Session.PlaySelectedSlot(Actor, AnimationSlot.UpperBody, null, false).Success);
        }

        /// <summary>The recorded steps (counted, not run).</summary>
        public int Steps()
        {
            int count = 0;
            while (History.PeekUndo() is { } step)
            {
                History.CommitUndo(step);
                count++;
            }
            return count;
        }
    }

    [Fact]
    public void Every_pause_and_play_click_is_its_own_step()
    {
        var rig = new Rig();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(rig.Session.PauseSlot(rig.Actor, AnimationSlot.UpperBody).Success);
            Assert.True(rig.Session.ResumeSlot(rig.Actor, AnimationSlot.UpperBody).Success);
        }

        // The initial apply plus six pause/resume clicks.
        Assert.Equal(7, rig.Steps());
    }

    [Fact]
    public void Undoing_into_a_held_layer_puts_it_back_on_its_frame()
    {
        var rig = new Rig();
        Assert.True(rig.Session.PauseSlot(rig.Actor, AnimationSlot.UpperBody).Success);
        Assert.True(rig.Session.ResumeSlot(rig.Actor, AnimationSlot.UpperBody).Success);
        rig.Scrub.ClearReceivedCalls();

        Assert.True(Undo(rig.History));

        // Held again (speed 0) and seeked to the frame the pause stored.
        rig.Speed.Received().SetSlotSpeed(rig.Actor, AnimationSlot.UpperBody, 0f);
        rig.Scrub.Received().SetControlTime(rig.Actor, ScrubControlId.Timeline(AnimationSlot.UpperBody), 2.5f, Arg.Any<ulong>());
    }

    [Fact]
    public void A_speed_drag_is_one_step_on_release()
    {
        var rig = new Rig();
        rig.Journal.BeginEdit("speed");
        foreach (var speed in new[] { 0.9f, 0.7f, 0.5f })
            Assert.True(rig.Session.SetSlotSpeed(rig.Actor, AnimationSlot.UpperBody, speed).Success);
        rig.Journal.EndEdit();
        rig.Journal.CommitEdit("speed");

        Assert.True(Undo(rig.History));
        rig.Speed.Received().ClearSlotSpeed(rig.Actor, AnimationSlot.UpperBody);
        // Only the initial apply remains: three speed writes formed one step.
        Assert.Equal(1, rig.Steps());
    }

    [Fact]
    public void Untracked_actions_record_nothing()
    {
        var rig = new Rig();
        using (rig.Session.Untracked())
            Assert.True(rig.Session.PauseSlot(rig.Actor, AnimationSlot.UpperBody).Success);

        Assert.Equal(1, rig.Steps());
    }
}
