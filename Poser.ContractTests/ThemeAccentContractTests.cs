extern alias ProductionPoser;

using System.Linq;
using System.Numerics;
using Poser.Config;
using Poser.UI;
using ThemeSelection = ProductionPoser::Poser.UI.ThemeSelection;

namespace Poser.ContractTests;

/// <summary>Tests accent and brightness state contracts.</summary>
public sealed class ThemeAccentContractTests
{
    [Fact]
    public void Palette_has_ten_distinct_accents_in_bright_then_dark_order()
    {
        Vector4[] expected =
        [
            new(50f / 255f, 151f / 255f, 1f, 1f),
            new(126f / 255f, 211f / 255f, 160f / 255f, 1f),
            new(232f / 255f, 193f / 255f, 90f / 255f, 1f),
            new(183f / 255f, 140f / 255f, 1f, 1f),
            new(1f, 143f / 255f, 163f / 255f, 1f),
            new(37f / 255f, 99f / 255f, 235f / 255f, 1f),
            new(45f / 255f, 130f / 255f, 95f / 255f, 1f),
            new(173f / 255f, 128f / 255f, 25f / 255f, 1f),
            new(110f / 255f, 72f / 255f, 186f / 255f, 1f),
            new(170f / 255f, 63f / 255f, 109f / 255f, 1f),
        ];

        Assert.Equal(10, Theme.AccentOptions.Count);
        Assert.Equal(expected, Theme.AccentOptions.ToArray());
        Assert.Equal(10, Theme.AccentOptions.Distinct().Count());
    }

    [Fact]
    public void Every_valid_accent_index_survives_light_and_dark_resolution()
    {
        for (int index = 0; index < Theme.AccentOptions.Count; index++)
        {
            var light = ThemeSelection.Resolve(UITheme.Light, index);
            var dark = ThemeSelection.Resolve(UITheme.Dark, index);

            Assert.Equal(Theme.AccentOptions[index], light.Accent);
            Assert.Equal(index, ThemeSelection.NormalizeAccentIndex(index));
            Assert.Equal(light.Accent, dark.Accent);
            Assert.Equal(light.Accent, light.Chrome.Primary);
            Assert.Equal(light.Accent, light.Palette.Primary);
            Assert.Equal(light.Accent with { W = 0.60f }, light.AccentHover);
        }
    }

    [Fact]
    public void Invalid_stored_accent_uses_the_first_concrete_choice()
    {
        Assert.Equal(0, ThemeSelection.NormalizeAccentIndex(-1));
        Assert.Equal(0, ThemeSelection.NormalizeAccentIndex(Theme.AccentOptions.Count));
        Assert.Equal(Theme.AccentOptions[0],
            ThemeSelection.Resolve(UITheme.Light, -1).Accent);
    }

    [Fact]
    public void Persisted_accent_round_trips_without_theme_remapping()
    {
        var config = new UIConfiguration
        {
            Theme = UITheme.Light,
            AccentIndex = 8,
        };

        string json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var restored = Newtonsoft.Json.JsonConvert
            .DeserializeObject<UIConfiguration>(json)!;

        Assert.Equal(8, restored.AccentIndex);
        Assert.Equal(
            ThemeSelection.Resolve(UITheme.Light, restored.AccentIndex).Accent,
            ThemeSelection.Resolve(UITheme.Dark, restored.AccentIndex).Accent);
    }

    [Fact]
    public void Auto_and_legacy_modes_toggle_from_their_resolved_brightness()
    {
        Assert.True(ThemeSelection.Resolve(UITheme.Auto, 0, true).IsLight);
        Assert.Equal(UITheme.Dark, ThemeSelection.NextBrightness(true));
        Assert.True(ThemeSelection.Resolve(UITheme.LightGray, 0, false).IsLight);
        Assert.Equal(UITheme.Dark, ThemeSelection.NextBrightness(true));
        Assert.Equal(UITheme.Light, ThemeSelection.NextBrightness(false));
        Assert.False(ThemeSelection.Resolve(UITheme.Gray, 0, true).IsLight);
        Assert.False(ThemeSelection.Resolve(UITheme.Blue, 0, true).IsLight);
        Assert.False(ThemeSelection.Resolve(UITheme.Purple, 0, true).IsLight);
    }

    [Fact]
    public void Theme_mode_glyph_is_opaque_and_keeps_a_slash_edge_when_scaled()
    {
        foreach (float scale in new[] { 1f, 2f })
        {
            var center = new Vector2(20f * scale, 20f * scale);
            float radius = 10f * scale;
            ThemeModeGlyphPlan plan = ThemeModeGlyph.Plan(center, radius);

            Assert.Equal(new Vector4(0f, 0f, 0f, 1f), plan.BaseColor);
            Assert.Equal(Vector4.One, plan.SectorColor);
            Assert.Equal(center, plan.Sector[0]);
            Assert.Equal(center + new Vector2(radius / MathF.Sqrt(2f), -radius / MathF.Sqrt(2f)), plan.Sector[1]);
            Assert.Equal(center + new Vector2(-radius / MathF.Sqrt(2f), radius / MathF.Sqrt(2f)), plan.Sector[^1]);
            Assert.All(plan.Sector[1..], point =>
                Assert.InRange(Vector2.Distance(point, center), radius - 0.0001f, radius + 0.0001f));
            Assert.Equal(ThemeModeGlyph.HitSide * scale, 22f * scale);
        }
    }
}
