using Microsoft.Win32;
using Poser.Config;

namespace Poser.UI;

internal static class ThemeSelection
{
    public static Theme Resolve(UITheme selection, int accentIndex)
    {
        var theme = Base(selection);
        var options = theme.Settings.AccentOptions;
        // Index 0 means "the theme keeps its own primary". The swatch row is
        // shared by every theme, but its first entry is only the DARK primary
        // — re-applying it would recolor the light schemes.
        if (accentIndex <= 0 || options is null || accentIndex >= options.Length)
            return theme;
        return theme.WithAccent(options[accentIndex]);
    }

    public static void Apply(UITheme selection, int accentIndex) =>
        Crystarium.UseTheme(Resolve(selection, accentIndex));

    /// <summary>Config-driven apply: the accent comes from the saved
    /// configuration, so startup and reloads keep the chosen accent.</summary>
    public static void Apply(UITheme selection) =>
        Apply(selection, ConfigurationService.Instance.Config.UI.AccentIndex);

    private static Theme Base(UITheme selection) =>
        selection switch
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
