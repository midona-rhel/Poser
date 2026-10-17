using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Tests.Animation;

public sealed class LayerResetTests
{
    private const ushort Emote = 8135, Carry = 208, Nod = 671;

    [Fact]
    public void Resetting_one_layer_leaves_the_others_and_the_base_where_they_are()
    {
        var actor = ActorId.New();
        var timeline = Substitute.For<IAnimationTimelinePort>();
        var live = new Dictionary<AnimationSlot, ushort>
        {
            [AnimationSlot.Base] = Emote,
            [AnimationSlot.UpperBody] = Carry,
        };
        timeline.TimelineSlot(Nod).Returns(AnimationSlot.Additive);
        timeline.Read(actor).Returns(_ => ActorAnimationReading.Empty with
        {
            BaseTimeline = live[AnimationSlot.Base],
            Slots = live.Select(e => new AnimationSlotReading(e.Key, e.Value, 1f)).ToArray(),
        });
        timeline.Blend(actor, Nod, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>())
            .Returns(_ => { live[AnimationSlot.Additive] = Nod; return Outcome.Ok(); });
        // The slot cancel takes only its own layer.
        timeline.ClearSlotTimeline(actor, AnimationSlot.Additive)
            .Returns(_ => { live.Remove(AnimationSlot.Additive); return Outcome.Ok(); });
        timeline.CaptureBase(actor).Returns(new BaseAnimationCapture(0, 0, Emote));
        var session = new AnimationSession(timeline, Substitute.For<IAnimationSpeedPort>(),
            Substitute.For<IAnimationStancePort>(), Substitute.For<IAnimationScrubPort>(),
            Substitute.For<IWorldRenderingRuntimePort>());

        Assert.True(session.ChooseSlot(actor, AnimationSlot.Additive, Nod).Success);
        Assert.True(session.PlaySelectedSlot(actor, AnimationSlot.Additive, null, false, resume: false).Success);
        timeline.ClearReceivedCalls();

        Assert.True(session.ResetSlot(actor, AnimationSlot.Additive).Success);

        timeline.Received(1).ClearSlotTimeline(actor, AnimationSlot.Additive);
        timeline.DidNotReceive().Blend(actor, Carry, Arg.Any<BaseAnimationCapture?>(), out Arg.Any<BaseAnimationCapture?>());
        timeline.DidNotReceive().RestoreBase(actor, Arg.Any<BaseAnimationCapture>());
    }
}
