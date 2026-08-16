extern alias ProductionPoser;

using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using NSubstitute;
using Poser.Application.Animation;
using Poser.Config;
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
        string? storedJson = null;
        var plugin = Substitute.For<IDalamudPluginInterface>();
        plugin.GetPluginConfig().Returns(_ => storedJson is null
            ? null
            : JsonConvert.DeserializeObject<PoserConfiguration>(storedJson));
        plugin.SavePluginConfig(Arg.Any<IPluginConfiguration>()).Do(call =>
        {
            var saved = Assert.IsType<PoserConfiguration>(
                call.Arg<IPluginConfiguration>());
            storedJson = JsonConvert.SerializeObject(saved);
        });
        var notices = Substitute.For<INotificationManager>();
        var configuration = new ConfigurationService(plugin);
        var textures = Substitute.For<ITextureProvider>();
        using var session = new ReferenceImageSession(
            textures, configuration, new UserNotices(notices));

        var instance = session.Add(@"C:\poses\sheet.png");
        session.SetOpacity(instance, 0.4f);
        session.SetPlacement(
            instance, new Vector2(100f, 110f), new Vector2(800f, 400f));
        session.SetHidden(instance, true);
        session.Dispose();

        Assert.NotNull(storedJson);
        var stored = JsonConvert.DeserializeObject<PoserConfiguration>(
            storedJson!)!;
        var savedEntry = Assert.Single(stored.ReferenceImages.Images);
        Assert.Equal(0.4f, savedEntry.Opacity);
        Assert.Equal(100f, savedEntry.X);
        Assert.Equal(110f, savedEntry.Y);
        Assert.Equal(800f, savedEntry.Width);
        Assert.Equal(400f, savedEntry.Height);
        Assert.True(savedEntry.Hidden);
        Assert.NotSame(configuration.Config, stored);

        var reloadedConfiguration = new ConfigurationService(plugin);
        using var reloadedSession = new ReferenceImageSession(
            textures, reloadedConfiguration, new UserNotices(notices));
        reloadedSession.Restore();

        Assert.NotSame(stored, reloadedConfiguration.Config);
        var restored = Assert.Single(reloadedSession.Instances);
        Assert.Equal(savedEntry.Id, restored.Id);
        Assert.Equal(savedEntry.FilePath, restored.Entry.FilePath);
        Assert.Equal(0.4f, restored.Entry.Opacity);
        Assert.Equal(100f, restored.Entry.X);
        Assert.Equal(110f, restored.Entry.Y);
        Assert.Equal(800f, restored.Entry.Width);
        Assert.Equal(400f, restored.Entry.Height);
        Assert.True(ReferenceImageSession.IsHidden(restored));
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
