namespace Poser.UI.Widgets;

public static class Themes
{
    /// <summary>The visible theme and the one waiting for its font atlas.</summary>
    internal sealed class ThemeState
    {
        internal Theme Active = Theme.Dark;
        internal readonly ThemeActivation Activation = new(Theme.Dark);
    }

    private static ThemeState State => UiContext.Current.Theme;

    /// <summary>The active theme as a value for public consumers.</summary>
    public static Theme ActiveTheme => State.Active;

    // The UI renderer reads several tokens for every text submission. Keep
    // those reads on the current immutable value without copying Theme.
    internal static ref readonly Theme ActiveThemeRef => ref State.Active;

    /// <summary>Queues a full token replacement until its font atlas is ready.</summary>
    public static void UseTheme(Theme theme)
    {
        var state = State;
        if (!FontRegistry.Registered)
            state.Activation.ActivateWithoutFonts(theme);
        else
            state.Activation.Request(theme);
        state.Active = state.Activation.Active;
    }

    /// <summary>Advances a staged theme at the frame boundary before drawing.</summary>
    public static bool AdvanceTheme()
    {
        var ui = UiContext.Current;
        ui.Theme.Activation.Advance(candidate => FontRegistry.Activate(candidate));
        ui.Theme.Active = ui.Theme.Activation.Active;
        // Glyphs first drawn last frame bake in one rebuild; runs measured
        // with the fallback glyph are re-measured once it lands.
        if (FontRegistry.SyncGlyphs())
            ui.Text.MeasureCache.Clear();
        // Once the fonts have been ready the UI draws every frame; a
        // handle that reads unavailable for a frame falls back to the
        // default font in the text pipeline instead of hiding everything.
        return FontRegistry.Ready || FontRegistry.EverReady;
    }
}
