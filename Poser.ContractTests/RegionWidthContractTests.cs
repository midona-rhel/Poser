using Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the rule that layout math must not throw mid-frame.
///
/// <para>A control width DERIVED by subtraction — a row's control column less
/// the action strip beside it, a scroll region less its gutter, a pane less
/// its insets — reaches zero routinely: on a narrow rail, in a collapsed
/// child, on a first frame that has not measured its region yet. Pinning a
/// control to such a span through <see cref="UiWidth.Fixed"/> answers that by
/// throwing, and a throw out of a draw call takes down the whole window, not
/// the one row that could not be measured. Derived spans go through
/// <see cref="UiWidth.Region"/>, which collapses instead.</para>
///
/// <para>The live case: the expression picker row draws on the pose rail as
/// well as on the wide workspace surface, and its three worded actions measure
/// wider than the rail's whole control column — the trigger's width came out
/// at zero and the window died on the first frame the rail was up.</para>
/// </summary>
public sealed class RegionWidthContractTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(-260f)]
    public void A_region_with_no_room_collapses_rather_than_throwing(
        float width)
    {
        var region = Record.Exception(() => UiWidth.Region(width));
        Assert.Null(region);
        Assert.Equal(UiWidth.Fixed(UiWidth.Minimum), UiWidth.Region(width));
    }

    [Fact]
    public void A_region_with_room_pins_the_width_it_was_given()
    {
        Assert.Equal(UiWidth.Fixed(150f), UiWidth.Region(150f));
    }

    [Fact]
    public void A_region_narrower_than_the_minimum_still_states_the_minimum()
    {
        Assert.Equal(UiWidth.Fixed(UiWidth.Minimum), UiWidth.Region(0.25f));
    }

    /// <summary>An EXACT width is still a caller's own assertion: a
    /// non-positive one is a programming error, and Region — not a relaxed
    /// Fixed — is what derived spans are supposed to use.</summary>
    [Fact]
    public void An_exact_width_still_rejects_a_non_positive_span()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiWidth.Fixed(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiWidth.Fixed(-4f));
    }
}
