using Microsoft.Win32;
using Poser.Config;

namespace Poser.UI;

internal static class ThemeSelection
{
    public static Theme Resolve(UITheme selection, int accentIndex)
    {
        var theme = selection switch
        {
            UITheme.Auto => WindowsUsesLightApps()
                ? Theme.PictoLight
                : Theme.PictoDark,
            UITheme.Light => Theme.PictoLight,
            UITheme.LightGray => Theme.PictoLightGray,
            UITheme.Gray => Theme.PictoGray,
            UITheme.Blue => Theme.PictoBlue,
            UITheme.Purple => Theme.PictoPurple,
            _ => Theme.PictoDark,
        };
        // Index 0 is "the theme's own primary": the baked value stays
        // untouched so the accepted baseline is reproduced exactly.
        var options = theme.Settings.AccentOptions;
        return accentIndex > 0 && accentIndex < options.Length
            ? theme.WithAccent(options[accentIndex])
            : theme;
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
