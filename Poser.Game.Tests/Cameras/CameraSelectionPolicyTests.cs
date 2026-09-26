using NSubstitute;
using Poser.Application.Presentation;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraSelectionPolicyTests
{
    [Fact]
    public void Selection_activates_once_without_drawing_and_does_not_override_a_later_manual_switch()
    {
        using var configuration = new ConfigurationService(new Settings());
        configuration.Config.Camera.LookThroughSelectedCamera = true;
        var selection = new SelectionSession();
        var cameras = Substitute.For<ICameraControl>();
        var id = new CameraId(Guid.NewGuid(), 0);
        cameras.Read(id).Returns(Reading(id));
        var policy = new CameraSelectionPolicy(selection, configuration, cameras);
        selection.Select(SelectionId.ForCamera(id));
        policy.Tick();
        policy.Tick();
        cameras.Received(1).SetLive(id, true);
        selection.Clear();
        policy.Tick();
        selection.Select(SelectionId.ForCamera(id));
        policy.Tick();
        cameras.Received(2).SetLive(id, true);
    }

    [Fact]
    public void Disabled_preference_never_changes_the_live_camera()
    {
        using var configuration = new ConfigurationService(new Settings());
        configuration.Config.Camera.LookThroughSelectedCamera = false;
        var selection = new SelectionSession();
        var cameras = Substitute.For<ICameraControl>();
        var id = new CameraId(Guid.NewGuid(), 0);
        cameras.Read(id).Returns(Reading(id));
        var policy = new CameraSelectionPolicy(selection, configuration, cameras);
        selection.Select(SelectionId.ForCamera(id));
        policy.Tick();
        cameras.DidNotReceiveWithAnyArgs().SetLive(default, default);
        configuration.Config.Camera.LookThroughSelectedCamera = true;
        policy.Tick();
        cameras.Received(1).SetLive(id, true);
    }

    private static CameraReading Reading(CameraId id) => new(
        id, true, "Camera", default, false, false, false, default, default,
        0, 0, default, 0, default, default, null, false, false, false, default, default,
        false, false, 0, 0, false, false, 0, 0, 0, default);

    private sealed class Settings : IConfigurationPersistence
    {
        public ConfigurationLoadResult Load() => new(new());
        public void Save(PoserConfiguration configuration) { }
    }
}

