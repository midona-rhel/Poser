extern alias ProductionPoser;

using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Animation;
using ProductionPoser::Poser.Config;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

public sealed class ReferenceImageContractTests
{
    [Fact]
    public void Geometry_keeps_picture_ratio_and_uses_the_axis_the_user_dragged()
    {
        var widthDriven = ReferenceImageGeometry.ResolveAspect(
            new Vector2(300f, 150f), new Vector2(340f, 180f), 2f);
        var heightDriven = ReferenceImageGeometry.ResolveAspect(
            new Vector2(300f, 150f), new Vector2(310f, 190f), 2f);

        Assert.Equal(new Vector2(340f, 170f), widthDriven);
        Assert.Equal(new Vector2(380f, 190f), heightDriven);
        Assert.Equal(
            2f,
            ReferenceImageGeometry.InitialSize(
                new Vector2(1600f, 800f), new Vector2(1000f, 600f)).X /
            ReferenceImageGeometry.InitialSize(
                new Vector2(1600f, 800f), new Vector2(1000f, 600f)).Y,
            5);
        Assert.Equal(
            ReferenceImageConfiguration.MinimumOpacity,
            ReferenceImageConfiguration.ClampOpacity(0f));
        Assert.Equal(1f, ReferenceImageConfiguration.ClampOpacity(float.NaN));
    }

    [Fact]
    public void Session_persists_identity_placement_opacity_and_hidden_lifetime()
    {
        var stored = new PoserConfiguration();
        var roster = stored.ReferenceImages;
        var first = roster.Add(@"C:\poses\sheet.png");
        first.X = 12f;
        first.Y = 24f;
        first.Width = 640f;
        first.Height = 320f;
        first.Hidden = true;

        var second = roster.Add(first.FilePath);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("sheet", first.Name);

        var plugin = Substitute.For<IDalamudPluginInterface>();
        plugin.GetPluginConfig().Returns(stored);
        var notices = Substitute.For<INotificationManager>();
        var configuration = new ConfigurationService(plugin);
        var textures = Substitute.For<ITextureProvider>();
        using var session = new ReferenceImageSession(
            textures, configuration, new UserNotices(notices));

        session.Restore();
        var instance = Assert.Single(session.Instances, item => item.Id == first.Id);
        session.SetOpacity(instance, 0f);
        session.SetPlacement(
            instance, new Vector2(100f, 110f), new Vector2(800f, 400f));
        session.SetHidden(instance, false);

        Assert.Equal(ReferenceImageConfiguration.MinimumOpacity,
            instance.Entry.Opacity);
        Assert.Equal(100f, instance.Entry.X);
        Assert.Equal(110f, instance.Entry.Y);
        Assert.Equal(800f, instance.Entry.Width);
        Assert.Equal(400f, instance.Entry.Height);
        Assert.False(ReferenceImageSession.IsHidden(instance));

        var duplicate = session.Duplicate(instance);
        Assert.NotEqual(instance.Id, duplicate.Id);
        Assert.Equal(instance.Entry.Opacity, duplicate.Entry.Opacity);
        session.Close(duplicate);
        Assert.DoesNotContain(duplicate.Entry, roster.Images);

        session.Close(instance);
        Assert.DoesNotContain(first, roster.Images);
        Assert.Empty(session.Instances);
        plugin.Received().SavePluginConfig(stored);
    }

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
