using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace Poser.Config;

/// <summary>
/// THE chord vocabulary. Persisted config text, the settings capture and the
/// runtime matcher all speak it, so a chord the user captures is a chord the
/// binder can fire and a chord a preset states is a chord a badge can show.
///
/// <para>Unbound is a first-class value — empty text, <see cref="VirtualKey.NO_KEY"/>
/// — not a missing entry: a slot the user deliberately cleared has to survive a
/// save, and a preset that leaves an action unbound has to be able to say so.
/// </para>
/// </summary>
public readonly record struct KeyChord(
    bool Ctrl, bool Shift, bool Alt, VirtualKey Key)
{
    public static KeyChord None => default;

    public bool IsBound => Key != VirtualKey.NO_KEY;

    /// <summary>
    /// One keyboard key in all three alphabets at once: the text a config
    /// file stores, the <see cref="VirtualKey"/> the game's key state is read
    /// with, and the <see cref="ImGuiKey"/> a capture frame reports. Keeping
    /// them in one row is what stops a captured key from being unparseable
    /// and a preset key from being uncapturable.
    /// </summary>
    private readonly record struct KeyToken(
        string Text, VirtualKey Key, ImGuiKey ImGuiKey);

    // Ordered as the rebind capture scans them: letters and digits first
    // (overwhelmingly what a user presses), then the named keys, then the
    // punctuation Ktisis and Brio actually bind.
    private static readonly KeyToken[] Tokens = BuildTokens();

    private static readonly Dictionary<string, KeyToken> ByText =
        BuildTextIndex();

    private static readonly Dictionary<VirtualKey, string> ByKey =
        BuildKeyIndex();

    private static KeyToken[] BuildTokens()
    {
        var tokens = new List<KeyToken>(80);
        for (int i = 0; i < 26; i++)
            tokens.Add(new(
                ((char)('A' + i)).ToString(),
                (VirtualKey)((int)VirtualKey.A + i),
                (ImGuiKey)((int)ImGuiKey.A + i)));
        for (int i = 0; i < 10; i++)
            tokens.Add(new(
                ((char)('0' + i)).ToString(),
                (VirtualKey)((int)VirtualKey.KEY_0 + i),
                (ImGuiKey)((int)ImGuiKey.Key0 + i)));
        for (int i = 0; i < 12; i++)
            tokens.Add(new(
                "F" + (i + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                (VirtualKey)((int)VirtualKey.F1 + i),
                (ImGuiKey)((int)ImGuiKey.F1 + i)));
        tokens.AddRange(
        [
            new("Escape", VirtualKey.ESCAPE, ImGuiKey.Escape),
            new("Space", VirtualKey.SPACE, ImGuiKey.Space),
            new("Tab", VirtualKey.TAB, ImGuiKey.Tab),
            new("Enter", VirtualKey.RETURN, ImGuiKey.Enter),
            new("Backspace", VirtualKey.BACK, ImGuiKey.Backspace),
            new("Delete", VirtualKey.DELETE, ImGuiKey.Delete),
            new("Insert", VirtualKey.INSERT, ImGuiKey.Insert),
            new("Home", VirtualKey.HOME, ImGuiKey.Home),
            new("End", VirtualKey.END, ImGuiKey.End),
            new("PageUp", VirtualKey.PRIOR, ImGuiKey.PageUp),
            new("PageDown", VirtualKey.NEXT, ImGuiKey.PageDown),
            new("Left", VirtualKey.LEFT, ImGuiKey.LeftArrow),
            new("Right", VirtualKey.RIGHT, ImGuiKey.RightArrow),
            new("Up", VirtualKey.UP, ImGuiKey.UpArrow),
            new("Down", VirtualKey.DOWN, ImGuiKey.DownArrow),
            // The punctuation the references bind: Ktisis cycles cameras on
            // the bracket keys and selects siblings on the backslash.
            new("[", VirtualKey.OEM_4, ImGuiKey.LeftBracket),
            new("]", VirtualKey.OEM_6, ImGuiKey.RightBracket),
            new("\\", VirtualKey.OEM_5, ImGuiKey.Backslash),
            new("-", VirtualKey.OEM_MINUS, ImGuiKey.Minus),
            new("=", VirtualKey.OEM_PLUS, ImGuiKey.Equal),
            new(";", VirtualKey.OEM_1, ImGuiKey.Semicolon),
            new("'", VirtualKey.OEM_7, ImGuiKey.Apostrophe),
            new(",", VirtualKey.OEM_COMMA, ImGuiKey.Comma),
            new(".", VirtualKey.OEM_PERIOD, ImGuiKey.Period),
            new("/", VirtualKey.OEM_2, ImGuiKey.Slash),
            new("`", VirtualKey.OEM_3, ImGuiKey.GraveAccent),
        ]);
        // The numeric keypad, which a photographer's off-hand can reach
        // without leaving the mouse. Named in WORDS rather than symbols
        // because the chord text is split on '+' — a key called "Num+" would
        // parse as a modifier followed by nothing.
        for (int i = 0; i < 10; i++)
            tokens.Add(new(
                "Num" + i.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                (VirtualKey)((int)VirtualKey.NUMPAD0 + i),
                (ImGuiKey)((int)ImGuiKey.Keypad0 + i)));
        tokens.AddRange(
        [
            new("NumPlus", VirtualKey.ADD, ImGuiKey.KeypadAdd),
            new("NumMinus", VirtualKey.SUBTRACT, ImGuiKey.KeypadSubtract),
            new("NumMultiply", VirtualKey.MULTIPLY, ImGuiKey.KeypadMultiply),
            new("NumDivide", VirtualKey.DIVIDE, ImGuiKey.KeypadDivide),
            new("NumDecimal", VirtualKey.DECIMAL, ImGuiKey.KeypadDecimal),
            // Keypad Enter is deliberately absent: Windows reports it as
            // VK_RETURN, so it cannot be told from the main Enter in the key
            // state a chord is matched against, and claiming the code here
            // would rename the Enter token.
        ]);
        return tokens.ToArray();
    }

    private static Dictionary<string, KeyToken> BuildTextIndex()
    {
        var index = new Dictionary<string, KeyToken>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokens)
            index[token.Text] = token;
        // The stored text a hand-edited config may carry: the raw VirtualKey
        // member name. Accepted on the way in, never written on the way out.
        foreach (var token in Tokens)
            index.TryAdd(token.Key.ToString(), token);
        return index;
    }

    private static Dictionary<VirtualKey, string> BuildKeyIndex()
    {
        var index = new Dictionary<VirtualKey, string>();
        foreach (var token in Tokens)
            index[token.Key] = token.Text;
        return index;
    }

    /// <summary>The key a capture frame saw, or null when the reported ImGui
    /// key is not one this vocabulary can store.</summary>
    public static VirtualKey? FromImGui(ImGuiKey key)
    {
        foreach (var token in Tokens)
            if (token.ImGuiKey == key)
                return token.Key;
        return null;
    }

    /// <summary>Every capturable key, in scan order — the settings rebind
    /// walks exactly this and nothing else, so the set it can capture is the
    /// set a chord can name.</summary>
    public static IEnumerable<ImGuiKey> CapturableKeys()
    {
        foreach (var token in Tokens)
            yield return token.ImGuiKey;
    }

    /// <summary>The same set in the alphabet the RUNTIME MATCHER reads
    /// (<see cref="VirtualKey"/> via the game's key state). The rebind
    /// capture reads this source too, so a chord it can capture is a
    /// chord the matcher can fire — capture through ImGui broke the
    /// moment key events stopped reaching an unfocused widget.</summary>
    public static IEnumerable<VirtualKey> CapturableVirtualKeys()
    {
        foreach (var token in Tokens)
            yield return token.Key;
    }

    /// <summary>Both alphabets of every capturable key. The capture polls
    /// BOTH sources: with the settings window focused ImGui eats the key
    /// before the game's state sees it, and unfocused the reverse — one
    /// source alone is blind half the time.</summary>
    public static IEnumerable<(VirtualKey Key, ImGuiKey ImGui)>
        CapturableTokens()
    {
        foreach (var token in Tokens)
            yield return (token.Key, token.ImGuiKey);
    }

    /// <summary>Unrecognised text is UNBOUND, never a partial chord: a
    /// half-understood binding that fires on the modifier alone is worse than
    /// one that does not fire.</summary>
    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;

        bool ctrl = false, shift = false, alt = false;
        var key = VirtualKey.NO_KEY;
        foreach (var raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0)
                continue;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
                continue;
            }
            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
                continue;
            }
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
                continue;
            }
            if (!ByText.TryGetValue(part, out var token))
                return None;
            key = token.Key;
        }
        return key == VirtualKey.NO_KEY
            ? None
            : new KeyChord(ctrl, shift, alt, key);
    }

    /// <summary>The canonical text: modifiers in Ctrl, Shift, Alt order — the
    /// order the shipped defaults were written in — then the key. Unbound
    /// renders EMPTY; the display word for that belongs to the UI.</summary>
    public override string ToString()
    {
        if (!IsBound || !ByKey.TryGetValue(Key, out var name))
            return string.Empty;
        if (!Ctrl && !Shift && !Alt)
            return name;
        return (Ctrl ? "Ctrl+" : string.Empty)
            + (Shift ? "Shift+" : string.Empty)
            + (Alt ? "Alt+" : string.Empty)
            + name;
    }
}
