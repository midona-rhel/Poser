using Poser.Config;

namespace Poser.UI;

/// <summary>
/// The ONE keybind resolution — configured slots over
/// <see cref="KeybindRegistry"/>'s defaults — used identically by keybind
/// EXECUTION (UIManager) and shortcut-badge DISPLAY (hover help), so a card
/// can never advertise a chord that would not fire.
/// </summary>
internal static class PoserKeybinds
{
    /// <summary>
    /// Both of an action's chords, defaults filled in. READ-ONLY: the fallback
    /// hands back the registry's OWN default instance rather than a copy of
    /// it, because this is the keybind pump's per-tick question — see
    /// <see cref="KeybindRegistry.SharedDefault"/>. Neither caller (the pump,
    /// the hover badges) writes to what it is given.
    /// </summary>
    public static KeybindSlots Slots(string action)
    {
        var bindings = ConfigurationService.Instance.Config.UI.Bindings;
        return bindings.TryGetValue(action, out var slots)
            ? slots
            : KeybindRegistry.SharedDefault(action);
    }

    /// <summary>
    /// The chord a badge shows: the primary, or the secondary when the
    /// primary is unbound. A badge states ONE chord, and the one worth
    /// stating is the one that will actually fire.
    /// </summary>
    public static string Effective(string action)
    {
        var slots = Slots(action);
        return slots.Primary.Length > 0 ? slots.Primary : slots.Secondary;
    }
}
