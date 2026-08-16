using Poser.Entities;

namespace Poser.ContractTests;

/// <summary>
/// The wheel's fly-speed curve and the readout it raises. Both live where the
/// game does not: the input detour hands over a raw scroll value and a clock
/// reading, and everything that decides what the camera then flies at — and
/// what the overlay then paints — is here, so it can be held to its ends
/// without a client.
///
/// The invariants that matter in the air: a notch is worth the same
/// PROPORTION at every speed (a fixed increment would be unusable at one end
/// of the range and useless at the other), the speed can never leave the
/// range the Speed row can show, and the readout goes away on its own so a
/// single notch cannot leave permanent ink under the cursor.
/// </summary>
public sealed class FreeCameraSpeedContractTests
{
    // ── the wheel's units ────────────────────────────────────────────────

    [Fact]
    public void NoScrollIsNoNotch() =>
        Assert.Equal(0, FreeCameraSpeed.Notches(0));

    [Theory]
    [InlineData(120, 1)]
    [InlineData(-120, -1)]
    [InlineData(360, 3)]
    [InlineData(-240, -2)]
    public void WholeDetentsCountAsDetents(int scroll, int expected) =>
        Assert.Equal(expected, FreeCameraSpeed.Notches(scroll));

    /// <summary>A frame that reports the wheel in plain counts rather than
    /// WHEEL_DELTA still moves the speed: rounding a single notch to nothing
    /// would be a wheel that silently does nothing at all.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, -1)]
    [InlineData(119, 1)]
    [InlineData(-119, -1)]
    public void SubDetentScrollStillCountsOnce(int scroll, int expected) =>
        Assert.Equal(expected, FreeCameraSpeed.Notches(scroll));

    // ── the step curve ───────────────────────────────────────────────────

    [Fact]
    public void OneNotchUpIsOneFactor() =>
        Assert.Equal(
            FreeCameraSpeed.Default * FreeCameraSpeed.NotchFactor,
            FreeCameraSpeed.Step(FreeCameraSpeed.Default, 1),
            3);

    [Fact]
    public void OneNotchDownIsOneFactorBack() =>
        Assert.Equal(
            FreeCameraSpeed.Default / FreeCameraSpeed.NotchFactor,
            FreeCameraSpeed.Step(FreeCameraSpeed.Default, -1),
            3);

    /// <summary>The step is geometric, so a notch is worth the same fraction
    /// of the speed it started from wherever on the range it is turned.
    /// </summary>
    [Fact]
    public void EveryNotchIsWorthTheSameProportion()
    {
        float slow = 0.01f;
        float fast = 0.1f;
        Assert.Equal(
            FreeCameraSpeed.Step(slow, 1) / slow,
            FreeCameraSpeed.Step(fast, 1) / fast,
            3);
    }

    [Fact]
    public void NotchesCompoundLikeRepeatedNotches()
    {
        float once = FreeCameraSpeed.Default;
        for (int i = 0; i < 4; i++)
            once = FreeCameraSpeed.Step(once, 1);
        Assert.Equal(once, FreeCameraSpeed.Step(FreeCameraSpeed.Default, 4), 4);
    }

    [Fact]
    public void UpThenDownReturnsToWhereItStarted()
    {
        float speed = FreeCameraSpeed.Step(FreeCameraSpeed.Default, 3);
        Assert.Equal(FreeCameraSpeed.Default, FreeCameraSpeed.Step(speed, -3), 4);
    }

    [Fact]
    public void ZeroNotchesLeavesAnInRangeSpeedAlone() =>
        Assert.Equal(0.07f, FreeCameraSpeed.Step(0.07f, 0), 6);

    // ── the clamp ────────────────────────────────────────────────────────

    [Fact]
    public void ScrollingUpStopsAtTheCeiling() =>
        Assert.Equal(
            FreeCameraSpeed.Maximum,
            FreeCameraSpeed.Step(FreeCameraSpeed.Maximum, 20),
            6);

    [Fact]
    public void ScrollingDownStopsAtTheFloor() =>
        Assert.Equal(
            FreeCameraSpeed.Minimum,
            FreeCameraSpeed.Step(FreeCameraSpeed.Minimum, -20),
            6);

    [Fact]
    public void TheWholeRangeIsReachableInBothDirections()
    {
        float up = FreeCameraSpeed.Default;
        float down = FreeCameraSpeed.Default;
        for (int i = 0; i < 40; i++)
        {
            up = FreeCameraSpeed.Step(up, 1);
            down = FreeCameraSpeed.Step(down, -1);
        }
        Assert.Equal(FreeCameraSpeed.Maximum, up, 6);
        Assert.Equal(FreeCameraSpeed.Minimum, down, 6);
    }

    /// <summary>A speed that arrived out of range is rescued rather than
    /// stepped further out — including a speed that is not a number at all.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(1000f)]
    public void AnOutOfRangeSpeedIsPulledBackIn(float speed)
    {
        float stepped = FreeCameraSpeed.Step(speed, 1);
        Assert.InRange(stepped, FreeCameraSpeed.Minimum, FreeCameraSpeed.Maximum);
    }

    [Fact]
    public void ANonFiniteSpeedFallsBackToTheDefault() =>
        Assert.Equal(
            FreeCameraSpeed.Default, FreeCameraSpeed.Step(float.NaN, 1), 6);

    // ── the readout ──────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultSpeedReadsAsOne() =>
        Assert.Equal("1×", FreeCameraSpeed.Format(FreeCameraSpeed.Default));

    [Fact]
    public void OneNotchUpReadsAsItsMultiple() =>
        Assert.Equal(
            "1.15×",
            FreeCameraSpeed.Format(
                FreeCameraSpeed.Step(FreeCameraSpeed.Default, 1)));

    [Fact]
    public void ASlowerSpeedReadsBelowOne() =>
        Assert.Equal(
            "0.87×",
            FreeCameraSpeed.Format(
                FreeCameraSpeed.Step(FreeCameraSpeed.Default, -1)));

    // ── the notice's life ────────────────────────────────────────────────

    private static FreeCameraSpeedNotice Notice(long at = 1_000L) =>
        new(FreeCameraSpeed.Default, at);

    [Fact]
    public void AFreshNoticeIsFullyVisible() =>
        Assert.Equal(1f, Notice().Opacity(1_000L));

    [Fact]
    public void TheNoticeHoldsForTheWholeHold() =>
        Assert.Equal(1f, Notice().Opacity(1_000L + FreeCameraSpeedNotice.HoldMs));

    [Fact]
    public void TheNoticeFadesAcrossTheFade()
    {
        float half = Notice().Opacity(
            1_000L + FreeCameraSpeedNotice.HoldMs +
            FreeCameraSpeedNotice.FadeMs / 2);
        Assert.InRange(half, 0.4f, 0.6f);
    }

    [Fact]
    public void TheNoticeIsGoneOnceTheFadeEnds()
    {
        long gone = 1_000L + FreeCameraSpeedNotice.HoldMs +
            FreeCameraSpeedNotice.FadeMs;
        Assert.Equal(0f, Notice().Opacity(gone));
        Assert.False(Notice().IsVisible(gone));
        Assert.False(Notice().IsVisible(gone + 10_000L));
    }

    [Fact]
    public void TheNoticeLastsAboutASecond()
    {
        Assert.True(Notice().IsVisible(1_000L + 900L));
        Assert.False(Notice().IsVisible(1_000L + 1_100L));
    }

    /// <summary>A later notch restarts the whole life — a wheel turned
    /// steadily keeps one readout up rather than flickering it.</summary>
    [Fact]
    public void AFurtherNotchRenewsTheNotice()
    {
        long expired = 1_000L + FreeCameraSpeedNotice.HoldMs +
            FreeCameraSpeedNotice.FadeMs;
        Assert.True(Notice(expired).IsVisible(expired));
    }

    /// <summary>A clock read out of order leaves the readout up rather than
    /// blinking it out.</summary>
    [Fact]
    public void ATimeBeforeTheStampReadsAsFresh() =>
        Assert.Equal(1f, Notice().Opacity(500L));

    [Fact]
    public void TheNoticeCarriesItsOwnText() =>
        Assert.Equal("1×", Notice().Text);
}
