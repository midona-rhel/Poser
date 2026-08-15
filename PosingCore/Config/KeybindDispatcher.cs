using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;

namespace Poser.Config;

/// <summary>
/// THE keybind pump: the one place a held key becomes a fired action.
///
/// <para>It is deliberately free of both Dalamud and ImGui — the caller hands
/// it a key reader and a binding resolver — because a chord is INPUT, not
/// drawing. Poser used to poll chords from the end of its ImGui draw
/// callback, which Dalamud stops raising the moment the game's HUD is hidden:
/// every chord went silent in exactly the state a photographer works in. The
/// pump belongs on the framework tick, and a pump with no ImGui in it is one
/// that can run there.</para>
///
/// <para>The edge is the ACTION's, not the slot's: both chords are the same
/// command, so holding one while tapping the other must not fire twice.</para>
/// </summary>
public sealed class KeybindDispatcher
{
    /// <summary>Whether a key is held THIS frame. The caller decides what an
    /// unreadable key answers — a key the host cannot poll must read as up
    /// rather than throw, because one unsupported chord would otherwise take
    /// every other action down with it.</summary>
    public delegate bool KeyDownReader(VirtualKey key);

    private readonly Entry[] _entries;

    /// <summary>Bound once, in the caller's order. An action with no handler
    /// is a build-time hole, so the caller supplies the whole table.</summary>
    public KeybindDispatcher(IEnumerable<KeyValuePair<string, Action>> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var entries = new List<Entry>();
        foreach (var (id, run) in actions)
            entries.Add(new Entry(id, run));
        _entries = entries.ToArray();
    }

    /// <summary>
    /// Forgets every edge. Called for a frame the pump is gated out of, so a
    /// chord is judged fresh on the first frame the gate opens again rather
    /// than staying stuck down from before it closed.
    /// </summary>
    public void Suspend()
    {
        foreach (var entry in _entries)
            entry.Down = false;
    }

    /// <summary>
    /// One frame. <paramref name="resolve"/> hands back the stored slots for
    /// an action — the SAME resolver the hover badges display, so a shown
    /// chord always matches one that fires — and unchanged text is never
    /// re-parsed.
    /// </summary>
    public void Pump(Func<string, KeybindSlots> resolve, KeyDownReader isDown)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isDown);
        foreach (var entry in _entries)
        {
            entry.Sync(resolve(entry.Name));
            bool active = ChordDown(entry.Primary, isDown)
                || ChordDown(entry.Secondary, isDown);
            if (active && !entry.Down)
            {
                entry.Down = true;
                entry.Run();
            }
            else if (!active)
            {
                entry.Down = false;
            }
        }
    }

    /// <summary>
    /// Whether the chord is held. Modifiers match EXACTLY — Ctrl+Z is not
    /// Ctrl+Shift+Z — and an unbound chord is never held.
    /// </summary>
    public static bool ChordDown(KeyChord chord, KeyDownReader isDown)
    {
        ArgumentNullException.ThrowIfNull(isDown);
        if (!chord.IsBound)
            return false;
        if (chord.Ctrl != isDown(VirtualKey.CONTROL))
            return false;
        if (chord.Shift != isDown(VirtualKey.SHIFT))
            return false;
        if (chord.Alt != isDown(VirtualKey.MENU))
            return false;
        return isDown(chord.Key);
    }

    /// <summary>One configured keybind: the action id the resolver and the
    /// hover badges key on, the delegate it runs, and its TWO chords parsed —
    /// string work happens only when the configured text actually changes.
    /// </summary>
    private sealed class Entry(string name, Action run)
    {
        public string Name { get; } = name;
        public Action Run { get; } = run;
        public KeyChord Primary { get; private set; }
        public KeyChord Secondary { get; private set; }

        private string _primaryText = string.Empty;
        private string _secondaryText = string.Empty;

        /// <summary>Edge state: the action was down on the previous pump.</summary>
        public bool Down { get; set; }

        public void Sync(KeybindSlots slots)
        {
            if (!string.Equals(
                slots.Primary, _primaryText, StringComparison.Ordinal))
            {
                _primaryText = slots.Primary;
                Primary = KeyChord.Parse(slots.Primary);
            }
            if (!string.Equals(
                slots.Secondary, _secondaryText, StringComparison.Ordinal))
            {
                _secondaryText = slots.Secondary;
                Secondary = KeyChord.Parse(slots.Secondary);
            }
        }
    }
}
