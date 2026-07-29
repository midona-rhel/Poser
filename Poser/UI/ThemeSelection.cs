using Microsoft.Win32;
using Poser.Config;

namespace Poser.UI;

internal static class ThemeSelection
{
    public static Theme Resolve(UITheme selection) =>
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

    public static void Apply(UITheme selection) =>
        Crystarium.UseTheme(Resolve(selection));

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
