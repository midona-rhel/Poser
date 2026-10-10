using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Animation;

public sealed class AnimationInteractionOwnershipTests
{
    [Fact]
    public void Advanced_mode_is_shared_per_generation_and_failed_exit_keeps_it_enabled()
    {
        var actor = ActorId.New();
        var other = ActorId.New();
        var scene = new SceneSession(new SelectionSession());
        scene.Refresh(new SceneSnapshot(1, [new(actor, "Actor", []), new(other, "Other", [])], [], [], []));
        var port = Substitute.For<IAnimationRuntimePort>();
        var session = new AnimationSession(port);
        var expressions = Substitute.For<IExpressionPreview>();
        var steps = new AnimationSteps(session, new ValueJournal(new TransformHistory()), scene, expressions);
        IAnimationPlayback first = session, second = session;

        Assert.True(steps.SetAdvanced(actor, true).Success);
        Assert.True(first.IsAdvanced(actor));
        Assert.True(second.IsAdvanced(actor));
        Assert.False(second.IsAdvanced(other));
        Assert.Empty(port.ReceivedCalls());
        expressions.DidNotReceive().Reset(Arg.Any<ActorId>());
        expressions.Reset(actor).Returns(AnimationResult.Fail("Not ready"));
        Assert.False(steps.SetAdvanced(actor, false).Success);
        Assert.True(second.IsAdvanced(actor));
        expressions.Reset(actor).Returns(AnimationResult.Ok());
        Assert.True(steps.SetAdvanced(actor, false).Success);
        Assert.False(first.IsAdvanced(actor));

        Assert.True(steps.SetAdvanced(actor, true).Success);
        var replacement = actor with { Generation = actor.Generation + 1 };
        session.Reconcile(new SceneSnapshot(2, [new(replacement, "Replacement", [])], [], [], []));
        Assert.False(first.IsAdvanced(actor));
        Assert.False(first.IsAdvanced(replacement));
    }

    [Fact]
    public void Old_scrub_owner_cannot_write_or_end_the_new_owners_drag()
    {
        var actor = ActorId.New();
        var control = new ScrubControlId(0, 0);
        var port = Substitute.For<IAnimationRuntimePort>();
        port.EnumerateControls(actor, out Arg.Any<ulong>()).Returns(call =>
        {
            call[1] = 7UL;
            return new[] { new ScrubControlReading(control, 0, 10, 1) };
        });
        port.SetOverallSpeed(actor, 0).Returns(AnimationPortResult.Ok());
        port.SetControlTime(actor, control, Arg.Any<float>(), 7).Returns(AnimationPortResult.Ok());
        var session = new AnimationSession(port);
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
            .Returns(AnimationPortResult.Fail("Stale skeleton"));
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
}
