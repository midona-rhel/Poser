using Dalamud.Game.ClientState.Keys;
using Poser.Game.Cameras;

namespace Poser.Game.Tests.Cameras;

/// <summary>
/// What a live free camera may take off the game. The camera used to eat Ctrl
/// and Alt on every frame it was merely live, which killed every game chord
/// built on them — the reporting user's own hide-UI is Alt+NumPlus, and it
/// stopped working for as long as a free camera existed (user 2026-08-15).
/// </summary>
public class FreeCameraInputPolicyTests
{
    [Fact]
    public void AStillCameraIsNotFlying()
    {
        Assert.False(FreeCameraInputPolicy.IsFlying(0, 0, 0));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void AnyDrivenAxisIsFlying(int forwardBack, int leftRight, int upDown)
    {
        Assert.True(
            FreeCameraInputPolicy.IsFlying(forwardBack, leftRight, upDown));
    }

    [Fact]
    public void EscapeAndReturnAreNeverConsumed()
    {
        Assert.True(FreeCameraInputPolicy.NeverConsumed((int)VirtualKey.ESCAPE));
        Assert.True(FreeCameraInputPolicy.NeverConsumed((int)VirtualKey.RETURN));
    }

    [Fact]
    public void TheWholeFrameConsumptionStillTakesOrdinaryKeys()
    {
        Assert.False(FreeCameraInputPolicy.NeverConsumed((int)VirtualKey.W));
        Assert.False(FreeCameraInputPolicy.NeverConsumed((int)VirtualKey.MENU));
    }
}
