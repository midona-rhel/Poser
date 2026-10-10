using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using static Poser.UI.Widgets.TextWidgets;

namespace Poser.UI.Widgets;

public static class Themes
{
    private static Theme _activeTheme = Theme.PictoDark;
    private static readonly ThemeActivation _themes = new(Theme.PictoDark);

    /// <summary>The active theme as a value for public consumers.</summary>
    public static Theme ActiveTheme => _activeTheme;

    // The UI renderer reads several tokens for every text submission. Keep
    // those reads on the current immutable value without copying Theme.
    internal static ref readonly Theme ActiveThemeRef => ref _activeTheme;

    /// <summary>Queues a full token replacement until its font atlas is ready.</summary>
    public static void UseTheme(Theme theme)
    {
        if (!FontRegistry.Registered)
            _themes.ActivateWithoutFonts(theme);
        else
            _themes.Request(theme);
        _activeTheme = _themes.Active;
    }

    /// <summary>Advances a staged theme at the frame boundary before drawing.</summary>
    public static bool AdvanceTheme()
    {
        _themes.Advance(candidate => FontRegistry.Activate(candidate));
        _activeTheme = _themes.Active;
        // Glyphs first drawn last frame bake in one rebuild; runs measured
        // with the fallback glyph are re-measured once it lands.
        if (FontRegistry.SyncGlyphs())
            _measureCache.Clear();
        // Once the fonts have been ready the UI draws every frame; a
        // handle that reads unavailable for a frame falls back to the
        // default font in the text pipeline instead of hiding everything.
        return FontRegistry.Ready || FontRegistry.EverReady;
    }
}
