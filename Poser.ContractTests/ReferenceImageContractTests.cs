using NSubstitute;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.UI;

namespace Poser.ContractTests;

public sealed class ReferenceImageContractTests
{
    [Fact]
    public void Derived_region_and_scrub_lifetime_reject_stale_surface_owners()
    {
        Assert.Equal(UiWidth.Fixed(UiWidth.Minimum), UiWidth.Region(0f));
        Assert.Equal(UiWidth.Fixed(UiWidth.Minimum), UiWidth.Region(-20f));

        var actor = new ActorId(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
        var other = new ActorId(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1);
        var control = new ScrubControlId(0, 1);
        var reading = new ScrubControlReading(control, 0f, 5f, 1f);
        var port = Substitute.For<IAnimationRuntimePort>();
        port.EnumerateControls(actor, out Arg.Any<ulong>())
            .Returns(call =>
            {
                call[1] = 7UL;
                return new[] { reading };
            });
        port.SetOverallSpeed(Arg.Any<ActorId>(), Arg.Any<float>())
            .Returns(AnimationPortResult.Ok());
        port.SetControlTime(
                Arg.Any<ActorId>(), Arg.Any<ScrubControlId>(), Arg.Any<float>(),
                Arg.Any<ulong>())
            .Returns(AnimationPortResult.Ok());

        var session = new AnimationSession(port);
        Assert.True(session.BeginScrub(actor, control).Success);
        Assert.False(session.UpdateScrub(other, 2f).Success);
        port.DidNotReceive().SetControlTime(
            Arg.Any<ActorId>(), Arg.Any<ScrubControlId>(), Arg.Any<float>(),
            Arg.Any<ulong>());
        Assert.True(session.UpdateScrub(actor, 2f).Success);
        port.Received(1).SetControlTime(actor, control, 2f, 7UL);
    }
}
