using Poser.Config;
using Poser.Entities;

namespace Poser.Tests.Core;

/// <summary>
/// The camera settings' one standing promise: every default is the number the
/// camera was already hardcoded to before the setting existed, so a config
/// written by an older build flies identically.
/// </summary>
public class CameraConfigurationTests
{
    [Fact]
    public void DefaultsAreTheNumbersTheCameraAlreadyUsed()
    {
        var camera = new CameraConfiguration();

        Assert.Equal(FreeCameraSpeed.Default, camera.DefaultMovementSpeed);
        Assert.Equal(0.1f, camera.DefaultMouseSensitivity);
        // Brio's Ctrl ×3 / Alt ×0.3, which is what the input detour applied
        // unconditionally.
        Assert.Equal(3f, camera.FastMultiplier);
        Assert.Equal(0.3f, camera.SlowMultiplier);
    }

    [Fact]
    public void InputPolicyDefaultsChangeNothing()
    {
        var camera = new CameraConfiguration();

        // On: the modifiers were consumed unconditionally before the toggle.
        Assert.True(camera.ConsumeModifiersWhileFlying);
        // Off: neither behaviour existed at all, and both references ship
        // them off.
        Assert.False(camera.ConsumeAllGameInput);
        Assert.False(camera.FlipBindsPastNinety);
    }

    [Fact]
    public void ANewConfigurationCarriesACameraSection()
    {
        Assert.NotNull(new PoserConfiguration().Camera);
    }
}
