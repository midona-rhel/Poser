using Poser.Config;
using Poser.UI;

namespace Poser.ContractTests;

/// <summary>Contracts for the persisted surface-only transparency and blur recipe.</summary>
public sealed class UiSurfaceEffectsContractTests : IDisposable
{
    public UiSurfaceEffectsContractTests() =>
        Crystarium.FloatingSurface.ConfigureEffects(1f, backdropBlur: true);

    public void Dispose()
    {
        Crystarium.FloatingSurface.ConfigureEffects(1f, backdropBlur: true);
        Crystarium.FloatingSurface.BackdropBlurAvailable = false;
    }

    [Theory]
    [InlineData(-1f, UIConfiguration.MinimumFillOpacity)]
    [InlineData(0.8f, 0.8f)]
    [InlineData(2f, 1f)]
    public void Persisted_fill_opacity_stays_in_the_readable_range(
        float stored, float expected)
    {
        var config = new UIConfiguration { FillOpacity = stored };

        Assert.Equal(expected, config.FillOpacity);
    }

    [Fact]
    public void Blur_toggle_is_independent_from_fill_opacity()
    {
        Crystarium.FloatingSurface.BackdropBlurAvailable = true;
        Crystarium.FloatingSurface.ConfigureEffects(0.72f, backdropBlur: false);

        Assert.False(GlassChrome.ShouldPrependBackdropBlur);
    }

    [Fact]
    public void Surface_fill_alpha_changes_without_mutating_theme_colors()
    {
        Crystarium.FloatingSurface.BackdropBlurAvailable = false;
        Crystarium.FloatingSurface.ConfigureEffects(1f, backdropBlur: false);
        float opaqueAlpha = Crystarium.FloatingSurface.FillColor.W;
        var text = Crystarium.ActiveTheme.Text;
        var controlFill = Crystarium.ActiveTheme.Chrome.ControlFill;

        Crystarium.FloatingSurface.ConfigureEffects(0.72f, backdropBlur: false);

        Assert.Equal(opaqueAlpha * 0.72f, Crystarium.FloatingSurface.FillColor.W);
        Assert.Equal(text, Crystarium.ActiveTheme.Text);
        Assert.Equal(controlFill, Crystarium.ActiveTheme.Chrome.ControlFill);
    }
}
