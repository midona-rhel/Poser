using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI;

public static class KeyChordInput
{
    public static IEnumerable<(Dalamud.Game.ClientState.Keys.VirtualKey Key, ImGuiKey ImGui)> CapturableTokens()
    {
        foreach (var key in KeyChord.CapturableKeys())
            yield return ((Dalamud.Game.ClientState.Keys.VirtualKey)(int)key, ToImGui(key));
    }

    public static ImGuiKey ToImGui(KeyCode key)
    {
        if (key >= KeyCode.A && key <= KeyCode.Z) return ImGuiKey.A + ((int)key - (int)KeyCode.A);
        if (key >= KeyCode.KEY_0 && key <= KeyCode.KEY_9) return ImGuiKey.Key0 + ((int)key - (int)KeyCode.KEY_0);
        if (key >= KeyCode.F1 && key <= KeyCode.F12) return ImGuiKey.F1 + ((int)key - (int)KeyCode.F1);
        if (key >= KeyCode.NUMPAD0 && key <= KeyCode.NUMPAD9) return ImGuiKey.Keypad0 + ((int)key - (int)KeyCode.NUMPAD0);
        return key switch
        {
            KeyCode.ESCAPE => ImGuiKey.Escape,
            KeyCode.SPACE => ImGuiKey.Space,
            KeyCode.TAB => ImGuiKey.Tab,
            KeyCode.RETURN => ImGuiKey.Enter,
            KeyCode.BACK => ImGuiKey.Backspace,
            KeyCode.DELETE => ImGuiKey.Delete,
            KeyCode.INSERT => ImGuiKey.Insert,
            KeyCode.HOME => ImGuiKey.Home,
            KeyCode.END => ImGuiKey.End,
            KeyCode.PRIOR => ImGuiKey.PageUp,
            KeyCode.NEXT => ImGuiKey.PageDown,
            KeyCode.LEFT => ImGuiKey.LeftArrow,
            KeyCode.RIGHT => ImGuiKey.RightArrow,
            KeyCode.UP => ImGuiKey.UpArrow,
            KeyCode.DOWN => ImGuiKey.DownArrow,
            KeyCode.OEM_4 => ImGuiKey.LeftBracket,
            KeyCode.OEM_6 => ImGuiKey.RightBracket,
            KeyCode.OEM_5 => ImGuiKey.Backslash,
            KeyCode.OEM_MINUS => ImGuiKey.Minus,
            KeyCode.OEM_PLUS => ImGuiKey.Equal,
            KeyCode.OEM_1 => ImGuiKey.Semicolon,
            KeyCode.OEM_7 => ImGuiKey.Apostrophe,
            KeyCode.OEM_COMMA => ImGuiKey.Comma,
            KeyCode.OEM_PERIOD => ImGuiKey.Period,
            KeyCode.OEM_2 => ImGuiKey.Slash,
            KeyCode.OEM_3 => ImGuiKey.GraveAccent,
            KeyCode.ADD => ImGuiKey.KeypadAdd,
            KeyCode.SUBTRACT => ImGuiKey.KeypadSubtract,
            KeyCode.MULTIPLY => ImGuiKey.KeypadMultiply,
            KeyCode.DIVIDE => ImGuiKey.KeypadDivide,
            KeyCode.DECIMAL => ImGuiKey.KeypadDecimal,
            _ => ImGuiKey.None,
        };
    }
}
