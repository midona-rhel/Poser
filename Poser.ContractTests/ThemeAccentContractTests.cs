extern alias ProductionPoser;

using System.Linq;
using System.Numerics;
using Poser.Config;
using Poser.UI;
using ThemeSelection = ProductionPoser::Poser.UI.ThemeSelection;

namespace Poser.ContractTests;

/// <summary>Tests theme selection and accent contracts.</summary>
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
    public void Visible_themes_and_accents_resolve_to_exact_foundations()
    {
        foreach (bool windowsUsesLightApps in new[] { true, false })
        {
            foreach (ThemeChoice<UITheme> choice in ThemeSelection.VisibleChoices)
            {
                for (int index = 0; index < Theme.AccentOptions.Count; index++)
                {
                    Theme expected = choice.Value switch
                    {
                        UITheme.Auto when windowsUsesLightApps => Theme.PictoLight,
                        UITheme.Auto => Theme.PictoDark,
                        UITheme.Light => Theme.PictoLight,
                        UITheme.LightGray => Theme.PictoLightGray,
                        UITheme.Gray => Theme.PictoGray,
                        UITheme.Dark => Theme.PictoDark,
                        UITheme.Blue => Theme.PictoBlue,
                        UITheme.Purple => Theme.PictoPurple,
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    Vector4 accent = Theme.AccentOptions[index];
                    Theme actual = ThemeSelection.Resolve(
                        choice.Value, index, windowsUsesLightApps);

                    Assert.Equal(expected.WithAccent(accent), actual);
                    Assert.Equal(accent, actual.Accent);
                    Assert.Equal(accent with { W = 0.60f }, actual.AccentHover);
                    Assert.Equal(accent with { W = 0.80f }, actual.AccentActive);
                    Assert.Equal(accent, actual.Chrome.Primary);
                    Assert.Equal(accent with { W = 0.60f }, actual.Chrome.PrimaryHover);
                    Assert.Equal(accent with { W = 0.50f }, actual.Chrome.PrimaryFocus);
                    Assert.Equal(accent with { W = 0.10f }, actual.Chrome.AccentFill);
                    Assert.Equal(accent with { W = 0.30f }, actual.Chrome.AccentFillBorder);
                    Assert.Equal(accent, actual.Palette.Primary);
                }
            }
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
    public void Auto_and_legacy_modes_keep_their_original_theme_choices()
    {
        Assert.True(ThemeSelection.Resolve(UITheme.Auto, 0, true).IsLight);
        Assert.True(ThemeSelection.Resolve(UITheme.LightGray, 0, false).IsLight);
        Assert.False(ThemeSelection.Resolve(UITheme.Gray, 0, true).IsLight);
        Assert.False(ThemeSelection.Resolve(UITheme.Blue, 0, true).IsLight);
        Assert.False(ThemeSelection.Resolve(UITheme.Purple, 0, true).IsLight);
        Assert.Equal(
            ThemeSelection.Resolve(UITheme.Auto, 0, true).Accent,
            ThemeSelection.Resolve(UITheme.Auto, 0, false).Accent);
    }

    [Fact]
    public void Theme_selector_uses_one_ordered_choice_list()
    {
        ThemeChoice<UITheme>[] expectedChoices =
        [
            new(UITheme.Auto, "Auto", Vector4.Zero),
            new(UITheme.Light, "Light", Vector4.One),
            new(UITheme.LightGray, "Light Gray", new(200f / 255f, 202f / 255f, 205f / 255f, 1f)),
            new(UITheme.Gray, "Gray", new(68f / 255f, 68f / 255f, 68f / 255f, 1f)),
            new(UITheme.Dark, "Dark", new(1f / 255f, 1f / 255f, 1f / 255f, 1f)),
            new(UITheme.Blue, "Blue", new(40f / 255f, 53f / 255f, 110f / 255f, 1f)),
            new(UITheme.Purple, "Purple", new(70f / 255f, 50f / 255f, 117f / 255f, 1f)),
        ];
        Assert.Equal(expectedChoices, ThemeSelection.VisibleChoices.ToArray());
    }

    [Fact]
    public void Theme_mode_glyph_is_two_equal_opaque_halves()
    {
        foreach (float scale in new[] { 1f, 2f })
        {
            var center = new Vector2(20f * scale, 20f * scale);
            float radius = 10f * scale;
            ThemeModeGlyphPlan plan = ThemeModeGlyph.Plan(center, radius);

            Assert.Equal(center, plan.Center);
            Assert.Equal(radius, plan.Radius);
            Assert.Equal(new Vector4(0f, 0f, 0f, 1f), plan.BaseColor);
            Assert.Equal(Vector4.One, plan.HalfColor);
            Assert.Equal(
                new[]
                {
                    ThemeModeGlyphPrimitive.CircleFill,
                    ThemeModeGlyphPrimitive.HalfFill,
                },
                plan.Primitives);
            Assert.Equal(ThemeModeGlyph.ArcSegments + 1, plan.Half.Length);
            Assert.Equal(center + new Vector2(radius / MathF.Sqrt(2f), -radius / MathF.Sqrt(2f)), plan.Half[0]);
            Assert.Equal(center + new Vector2(-radius / MathF.Sqrt(2f), radius / MathF.Sqrt(2f)), plan.Half[^1]);
            Assert.DoesNotContain(center, plan.Half);
            Assert.All(plan.Half, point =>
                Assert.InRange(Vector2.Distance(point, center), radius - 0.0001f, radius + 0.0001f));
            float halfArea = PolygonArea(plan.Half, center);
            float fullArea = 0.5f * ThemeModeGlyph.ArcSegments * 2f
                * radius * radius
                * MathF.Sin(MathF.PI / ThemeModeGlyph.ArcSegments);
            Assert.Equal(fullArea * 0.5f, halfArea, 3);
        }
    }

    [Fact]
    public void Shared_swatch_layout_spaces_hits_and_selection_rings()
    {
        SwatchLayoutPlan plan = Crystarium.SwatchLayout(7);

        Assert.Equal(20f, plan.HitSide);
        Assert.Equal(7f, plan.DotRadius);
        Assert.Equal(4f, plan.SlotGap);
        Assert.Equal(24f, plan.CenterPitch);
        Assert.Equal(178f, plan.PaletteWidth);
        Assert.Equal(11f, plan.ActiveOuterRadius);
        Assert.True(plan.CenterPitch > plan.ActiveOuterRadius * 2f);
    }

    private static float PolygonArea(Vector2[] points, Vector2 center)
    {
        float twiceArea = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 a = points[i] - center;
            Vector2 b = points[(i + 1) % points.Length] - center;
            twiceArea += a.X * b.Y - a.Y * b.X;
        }
        return MathF.Abs(twiceArea) * 0.5f;
    }
}
