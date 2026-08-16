using Microsoft.Win32;
using System.Collections.Generic;
using System.Numerics;
using Poser.Config;

namespace Poser.UI;

internal static class ThemeSelection
{
    internal static readonly ThemeSelectionPlan VisiblePlan =
        new(
            [
                new(UITheme.Auto, "Auto"),
                new(UITheme.Light, "Light"),
                new(UITheme.LightGray, "Light Gray"),
                new(UITheme.Gray, "Gray"),
                new(UITheme.Dark, "Dark"),
                new(UITheme.Blue, "Blue"),
                new(UITheme.Purple, "Purple"),
            ],
            [
                UITheme.Auto,
                UITheme.Light,
                UITheme.LightGray,
                UITheme.Gray,
                UITheme.Dark,
                UITheme.Blue,
                UITheme.Purple,
            ],
            [
                Vector4.Zero,
                new(1f, 1f, 1f, 1f),
                new(200f / 255f, 202f / 255f, 205f / 255f, 1f),
                new(68f / 255f, 68f / 255f, 68f / 255f, 1f),
                new(1f / 255f, 1f / 255f, 1f / 255f, 1f),
                new(40f / 255f, 53f / 255f, 110f / 255f, 1f),
                new(70f / 255f, 50f / 255f, 117f / 255f, 1f),
            ],
            [
                "Auto",
                "Light",
                "Light Gray",
                "Gray",
                "Dark",
                "Blue",
                "Purple",
            ]);

    public static Theme Resolve(UITheme selection, int accentIndex)
        => Resolve(selection, accentIndex, WindowsUsesLightApps());

    // Explicit system input keeps Auto resolution deterministic for callers.
    internal static Theme Resolve(
        UITheme selection,
        int accentIndex,
        bool windowsUsesLightApps)
    {
        var theme = selection switch
        {
            UITheme.Auto => windowsUsesLightApps
                ? Theme.PictoLight
                : Theme.PictoDark,
            UITheme.Light => Theme.PictoLight,
            UITheme.LightGray => Theme.PictoLightGray,
            UITheme.Gray => Theme.PictoGray,
            UITheme.Blue => Theme.PictoBlue,
            UITheme.Purple => Theme.PictoPurple,
            _ => Theme.PictoDark,
        };
        return theme.WithAccent(Theme.AccentOptions[
            NormalizeAccentIndex(accentIndex)]);
    }

    // Invalid config values use the first concrete accent.
    public static int NormalizeAccentIndex(int accentIndex) =>
        accentIndex >= 0 && accentIndex < Theme.AccentOptions.Count
            ? accentIndex
            : 0;

    internal static int VisibleIndex(UITheme value)
    {
        for (int i = 0; i < VisiblePlan.Values.Count; i++)
            if (VisiblePlan.Values[i] == value)
                return i;
        return 0;
    }

    public static void Apply(UITheme selection, int accentIndex) =>
        Crystarium.UseTheme(Resolve(selection, accentIndex));

    private static bool WindowsUsesLightApps()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int enabled && enabled != 0;
        }
        catch
        {
            return false;
        }
    }
}

internal readonly record struct ThemeSelectionChoice(
    UITheme Value,
    string Label);

internal readonly record struct ThemeSelectionPlan(
    IReadOnlyList<ThemeSelectionChoice> Choices,
    IReadOnlyList<UITheme> Values,
    IReadOnlyList<Vector4> Swatches,
    IReadOnlyList<string> Labels);
