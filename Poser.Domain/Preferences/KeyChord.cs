using System;
using System.Collections.Generic;

namespace Poser.Domain.Preferences;

/// <summary>A key and its modifiers, with shared parsing for saved bindings.
/// A chord without a key is unbound.</summary>
public readonly record struct KeyChord(bool Ctrl, bool Shift, bool Alt, KeyCode Key)
{
    public static KeyChord None => default;
    public bool IsBound => Key != KeyCode.NO_KEY;
    private readonly record struct KeyToken(string Text, KeyCode Key);
    private static readonly KeyToken[] Tokens = BuildTokens();
    private static readonly Dictionary<string, KeyToken> ByText = BuildTextIndex();
    private static readonly Dictionary<KeyCode, string> ByKey = BuildKeyIndex();

    private static KeyToken[] BuildTokens()
    {
        var tokens = new List<KeyToken>();
        for (int i = 0; i < 26; i++) tokens.Add(new(((char)('A' + i)).ToString(), (KeyCode)(65 + i)));
        for (int i = 0; i < 10; i++) tokens.Add(new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), (KeyCode)(48 + i)));
        for (int i = 0; i < 12; i++) tokens.Add(new("F" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), (KeyCode)(112 + i)));
        tokens.AddRange([
            new("Escape", KeyCode.ESCAPE),
            new("Space", KeyCode.SPACE),
            new("Tab", KeyCode.TAB),
            new("Enter", KeyCode.RETURN),
            new("Backspace", KeyCode.BACK),
            new("Delete", KeyCode.DELETE),
            new("Insert", KeyCode.INSERT),
            new("Home", KeyCode.HOME),
            new("End", KeyCode.END),
            new("PageUp", KeyCode.PRIOR),
            new("PageDown", KeyCode.NEXT),
            new("Left", KeyCode.LEFT),
            new("Right", KeyCode.RIGHT),
            new("Up", KeyCode.UP),
            new("Down", KeyCode.DOWN),
            new("[", KeyCode.OEM_4),
            new("]", KeyCode.OEM_6),
            new("\\", KeyCode.OEM_5),
            new("-", KeyCode.OEM_MINUS),
            new("=", KeyCode.OEM_PLUS),
            new(";", KeyCode.OEM_1),
            new("'", KeyCode.OEM_7),
            new(",", KeyCode.OEM_COMMA),
            new(".", KeyCode.OEM_PERIOD),
            new("/", KeyCode.OEM_2),
            new("`", KeyCode.OEM_3),
        ]);
        for (int i = 0; i < 10; i++) tokens.Add(new("Num" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), (KeyCode)(96 + i)));
        tokens.AddRange([
            new("NumPlus", KeyCode.ADD),
            new("NumMinus", KeyCode.SUBTRACT),
            new("NumMultiply", KeyCode.MULTIPLY),
            new("NumDivide", KeyCode.DIVIDE),
            new("NumDecimal", KeyCode.DECIMAL),
        ]);
        return tokens.ToArray();
    }

    public static IEnumerable<KeyCode> CapturableKeys()
    {
        foreach (var token in Tokens) yield return token.Key;
    }

    private static Dictionary<string, KeyToken> BuildTextIndex()
    {
        var index = new Dictionary<string, KeyToken>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokens)
            index[token.Text] = token;
        // Also accept enum names from existing or hand-edited configuration.
        foreach (var token in Tokens)
            index.TryAdd(token.Key.ToString(), token);
        return index;
    }

    private static Dictionary<KeyCode, string> BuildKeyIndex()
    {
        var index = new Dictionary<KeyCode, string>();
        foreach (var token in Tokens)
            index[token.Key] = token.Text;
        return index;
    }

    /// <summary>Returns an unbound chord if any nonempty token is unknown
    /// or no key is supplied. If several keys are supplied, the last wins.</summary>
    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return None;

        bool ctrl = false, shift = false, alt = false;
        var key = KeyCode.NO_KEY;
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
        return key == KeyCode.NO_KEY
            ? None
            : new KeyChord(ctrl, shift, alt, key);
    }

    /// <summary>Formats Ctrl, Shift and Alt in that order, followed by the
    /// key. An unbound or unsupported key produces an empty string.</summary>
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
