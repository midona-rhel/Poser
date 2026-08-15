using NSubstitute;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.ContractTests;

/// <summary>
/// The scrub stale-actor guard is part of the update signature, so no
/// surface can feed a newly selected actor's slider value into the
/// previous actor's gesture.
/// </summary>
public sealed class ScrubGuardContractTests
{
    private static readonly ActorId ActorA =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
    private static readonly ActorId ActorB =
        new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1);
    private static readonly ScrubControlId Control = new(0, 1);
    private static readonly ScrubControlReading Reading =
        new(Control, 0f, 5f, 1f);

    [Fact]
    public void Update_refuses_a_wrong_actor_without_a_port_write_and_keeps_the_gesture()
    {
        var port = Substitute.For<IAnimationRuntimePort>();
        port.EnumerateControls(ActorA, out Arg.Any<ulong>())
            .Returns(call =>
            {
                call[1] = 7UL;
                return new[] { Reading };
            });
        port.SetOverallSpeed(Arg.Any<ActorId>(), Arg.Any<float>())
            .Returns(AnimationPortResult.Ok());
        port.SetControlTime(
                Arg.Any<ActorId>(),
                Arg.Any<ScrubControlId>(),
                Arg.Any<float>(),
                Arg.Any<ulong>())
            .Returns(AnimationPortResult.Ok());
        var session = new AnimationSession(port);
        Assert.True(session.BeginScrub(ActorA, Control).Success);

        var refused = session.UpdateScrub(ActorB, 2f);

        Assert.False(refused.Success);
        Assert.Contains("different actor", refused.Detail!);
        port.DidNotReceive().SetControlTime(
            Arg.Any<ActorId>(),
            Arg.Any<ScrubControlId>(),
            Arg.Any<float>(),
            Arg.Any<ulong>());

        // The refusal did not end the drag: the owning actor still scrubs.
        var accepted = session.UpdateScrub(ActorA, 2f);

        Assert.True(accepted.Success);
        port.Received(1).SetControlTime(ActorA, Control, 2f, 7UL);
    }
}
