using Dalamud.Game.ClientState.Keys;
using Poser.Game.Cameras;

namespace Poser.Game.Tests.Cameras;

/// <summary>
/// The free camera's key map, and what a live free camera may take off the
/// game. The camera used to eat Ctrl and Alt on every frame it was merely
/// live, which killed every game chord built on them — the reporting user's
/// own hide-UI is Alt+NumPlus, and it stopped working for as long as a free
/// camera existed (user 2026-08-15).
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

    // ---- the user's map, 2026-08-15 ------------------------------------

    [Fact]
    public void SpaceRisesAndCFalls()
    {
        Assert.Equal(1, FreeCameraInputPolicy.UpDownAxis(space: true, c: false));
        Assert.Equal(-1, FreeCameraInputPolicy.UpDownAxis(space: false, c: true));
    }

    [Fact]
    public void HoldingBothVerticalKeysCancels()
    {
        Assert.Equal(0, FreeCameraInputPolicy.UpDownAxis(space: true, c: true));
        Assert.Equal(0, FreeCameraInputPolicy.UpDownAxis(space: false, c: false));
    }

    /// <summary>The keys Brio's map used for the vertical pair are no longer
    /// the camera's: Q and E move nothing, so nothing may take them off the
    /// game.</summary>
    [Fact]
    public void QAndEAreNoLongerTheCamerasKeys()
    {
        Assert.DoesNotContain(VirtualKey.Q, FreeCameraInputPolicy.MovementKeys);
        Assert.DoesNotContain(VirtualKey.E, FreeCameraInputPolicy.MovementKeys);
    }

    [Fact]
    public void TheMovementKeysAreExactlyTheSixThatMove()
    {
        Assert.Equal(
            new[]
            {
                VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D,
                VirtualKey.SPACE, VirtualKey.C,
            },
            FreeCameraInputPolicy.MovementKeys);
    }

    /// <summary>A speed modifier moves nothing on its own, which is the whole
    /// reason it is consumed under a narrower gate than the fly keys. A key
    /// in both lists would lose that gate.</summary>
    [Fact]
    public void NoSpeedModifierIsAlsoAMovementKey()
    {
        foreach (var key in FreeCameraInputPolicy.SpeedModifierKeys)
            Assert.DoesNotContain(key, FreeCameraInputPolicy.MovementKeys);
    }

    /// <summary>Alt is nobody's key now: it used to be the slow modifier and
    /// was consumed for it, which cost the user their Alt+NumPlus hide-UI.
    /// </summary>
    [Fact]
    public void AltIsNoLongerConsumedByTheCamera()
    {
        Assert.DoesNotContain(VirtualKey.MENU, FreeCameraInputPolicy.MovementKeys);
        Assert.DoesNotContain(
            VirtualKey.MENU, FreeCameraInputPolicy.SpeedModifierKeys);
    }

    [Fact]
    public void WGoesForwardAndSBack()
    {
        Assert.Equal(-1, FreeCameraInputPolicy.ForwardBackAxis(w: true, s: false));
        Assert.Equal(1, FreeCameraInputPolicy.ForwardBackAxis(w: false, s: true));
        Assert.Equal(0, FreeCameraInputPolicy.ForwardBackAxis(w: true, s: true));
    }

    /// <summary>A and D are one mirrored pair: the reported left-strafe
    /// stutter has no home in the map itself, and this pins that.</summary>
    [Fact]
    public void AAndDAreExactMirrors()
    {
        Assert.Equal(-1, FreeCameraInputPolicy.LeftRightAxis(a: true, d: false));
        Assert.Equal(1, FreeCameraInputPolicy.LeftRightAxis(a: false, d: true));
        Assert.Equal(0, FreeCameraInputPolicy.LeftRightAxis(a: true, d: true));
        Assert.Equal(0, FreeCameraInputPolicy.LeftRightAxis(a: false, d: false));
    }

    [Fact]
    public void ShiftIsFastAndCtrlIsSlow()
    {
        Assert.Equal(
            3f,
            FreeCameraInputPolicy.SpeedMultiplier(
                shift: true, ctrl: false, fastMultiplier: 3f, slowMultiplier: 0.3f));
        Assert.Equal(
            0.3f,
            FreeCameraInputPolicy.SpeedMultiplier(
                shift: false, ctrl: true, fastMultiplier: 3f, slowMultiplier: 0.3f));
    }

    [Fact]
    public void NoModifierLeavesTheSpeedAlone()
    {
        Assert.Equal(
            1f,
            FreeCameraInputPolicy.SpeedMultiplier(
                shift: false, ctrl: false, fastMultiplier: 3f, slowMultiplier: 0.3f));
    }

    [Fact]
    public void ShiftBeatsCtrlWhenBothAreHeld()
    {
        Assert.Equal(
            3f,
            FreeCameraInputPolicy.SpeedMultiplier(
                shift: true, ctrl: true, fastMultiplier: 3f, slowMultiplier: 0.3f));
    }

    // ---- the UI text-focus gate ----------------------------------------

    [Fact]
    public void AFreshTextFocusReportSilencesTheCamera()
    {
        Assert.True(FreeCameraInputPolicy.UiTextFocusHolds(true, sinceMs: 0));
        Assert.True(FreeCameraInputPolicy.UiTextFocusHolds(true, sinceMs: 16));
    }

    [Fact]
    public void ANegativeReportNeverSilencesTheCamera()
    {
        Assert.False(FreeCameraInputPolicy.UiTextFocusHolds(false, sinceMs: 0));
    }

    /// <summary>The stamp must expire: only a DRAWN frame renews it, and a
    /// hidden HUD stops drawing, so a stamp that never lapsed would leave the
    /// camera permanently deaf.</summary>
    [Fact]
    public void AStaleReportLapses()
    {
        Assert.False(
            FreeCameraInputPolicy.UiTextFocusHolds(
                true, sinceMs: FreeCameraInputPolicy.UiTextFocusLapseMs));
        Assert.False(
            FreeCameraInputPolicy.UiTextFocusHolds(true, sinceMs: 10_000));
    }

    /// <summary>A clock that went backwards reads as lapsed, not as eternal:
    /// the failure a stuck gate causes is a camera that never flies again.
    /// </summary>
    [Fact]
    public void ABackwardsClockLapsesRatherThanSticking()
    {
        Assert.False(FreeCameraInputPolicy.UiTextFocusHolds(true, sinceMs: -1));
    }
}
