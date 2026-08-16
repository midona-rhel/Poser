using Poser.Config;
using Poser.Entities;

namespace Poser.Tests.Core;

public sealed class CameraConfigurationTests
{
    [Fact]
    public void Camera_defaults_preserve_the_legacy_speed_sensitivity_and_input_policy()
    {
        var camera = new CameraConfiguration();

        Assert.Equal(FreeCameraSpeed.Default, camera.DefaultMovementSpeed);
        Assert.Equal(0.1f, camera.DefaultMouseSensitivity);
        Assert.Equal(3f, camera.FastMultiplier);
        Assert.Equal(0.3f, camera.SlowMultiplier);
        Assert.True(camera.ConsumeModifiersWhileFlying);
        Assert.False(camera.ConsumeAllGameInput);
        Assert.False(camera.FlipBindsPastNinety);
        Assert.NotNull(new PoserConfiguration().Camera);
    }
}
