using System.Numerics;
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
    [InlineData(0.25f, UIConfiguration.MinimumFillOpacity)]
    [InlineData(0.8f, 0.8f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(float.NegativeInfinity, 1f)]
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
    public void Blur_recipe_has_no_tint_luminosity_or_noise_pass()
    {
        GlassBlurPlan plan = GlassChrome.BlurPlan;

        Assert.Equal(1, plan.SubmissionCount);
        Assert.Equal(1f, plan.BlurStrength);
        Assert.Equal(Vector4.Zero, plan.TintColor);
        Assert.Equal(Vector4.Zero, plan.LuminosityColor);
        Assert.Equal(0f, plan.NoiseOpacity);
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

    [Fact]
    public void Blur_keeps_the_existing_surface_fill_recipe()
    {
        Crystarium.FloatingSurface.BackdropBlurAvailable = true;
        Crystarium.FloatingSurface.ConfigureEffects(0.72f, backdropBlur: false);
        var noBlur = Crystarium.FloatingSurface.FillColor;

        Crystarium.FloatingSurface.ConfigureEffects(0.72f, backdropBlur: true);
        var withBlur = Crystarium.FloatingSurface.FillColor;
        var expected = Crystarium.ActiveTheme.Glass.Background with
        {
            W = Crystarium.ActiveTheme.Glass.Background.W * 0.72f,
        };

        Assert.Equal(expected, noBlur);
        Assert.Equal(noBlur, withBlur);
        Assert.NotEqual(
            Crystarium.ActiveTheme.Glass.BlurBackground with
            {
                W = Crystarium.ActiveTheme.Glass.BlurBackground.W * 0.72f,
            },
            withBlur);
    }
}
