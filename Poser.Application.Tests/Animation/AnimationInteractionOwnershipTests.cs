using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Animation;

public sealed class AnimationInteractionOwnershipTests
{
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
        port.SetOverallSpeed(actor, 0).Returns(Outcome.Ok());
        port.SetControlTime(actor, control, Arg.Any<float>(), 7).Returns(Outcome.Ok());
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
}
