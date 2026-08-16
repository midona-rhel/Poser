using Microsoft.Win32;
using Poser.Config;

namespace Poser.UI;

internal static class ThemeSelection
{
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

    /// <summary>Returns the opposite explicit brightness mode.</summary>
    public static UITheme NextBrightness(bool isLight) =>
        isLight ? UITheme.Dark : UITheme.Light;

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
