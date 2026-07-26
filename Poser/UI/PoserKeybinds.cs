using Poser.Config;

namespace Poser.UI;

/// <summary>
/// The ONE keybind resolution — configured override or built-in
/// fallback — used identically by keybind EXECUTION (UIManager) and
/// shortcut-badge DISPLAY (hover help), so a card can never advertise a
/// chord that would not fire.
/// </summary>
internal static class PoserKeybinds
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Fallbacks = new()
    {
        ["Undo"] = "Ctrl+Z",
        ["Redo"] = "Ctrl+Y",
        ["Translate mode"] = "Ctrl+1",
        ["Rotate mode"] = "Ctrl+2",
        ["Scale mode"] = "Ctrl+3",
        ["Universal mode"] = "Ctrl+4",
        ["Hide UI"] = "Ctrl+H",
    };

    /// <summary>The chord that will actually fire for an action.</summary>
    public static string Effective(string action)
    {
        var overrides = ConfigurationService.Instance.Config.UI.Keybinds;
        return overrides.TryGetValue(action, out var bound) ? bound : Fallbacks[action];
    }
}
