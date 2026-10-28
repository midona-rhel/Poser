using Poser.Documents.Config;

namespace Poser.UI;

/// <summary>
/// Resolves configured keybind slots over <see cref="KeybindRegistry"/>'s
/// defaults. UIManager and shortcut badges both use this path so a badge
/// cannot advertise a chord that would not fire.
/// </summary>
internal static class PoserKeybinds
{
    /// <summary>Both of an action's chords, defaults filled in.</summary>
    public static KeybindSlots Slots(UIConfiguration settings, string action)
    {
        var bindings = settings.Bindings;
        return bindings.TryGetValue(action, out var slots)
            ? slots
            : KeybindRegistry.Default(action);
    }

    /// <summary>
    /// The chord a badge shows: the primary, or the secondary when the
    /// primary is unbound. A badge shows one chord, and the useful one
    /// stating is the one that will actually fire.
    /// </summary>
    public static string Effective(UIConfiguration settings, string action)
    {
        var slots = Slots(settings, action);
        return slots.Primary.Length > 0 ? slots.Primary : slots.Secondary;
    }
}
