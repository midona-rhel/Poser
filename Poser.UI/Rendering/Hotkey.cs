using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public enum HotkeyScope
{
    /// <summary>Always fires when the ImGui IO is processing keyboard input.</summary>
    Global,
    /// <summary>Only fires when the current ImGui window is focused.</summary>
    WindowFocused,
    /// <summary>Only fires when an active modal popup is on top of the stack.</summary>
    ModalOnly,
}

/// <summary>
/// Registers keyboard hotkeys for the current frame. Callers re-register every
/// frame they want the hotkey live (declarative, like the rest of Norvrandt).
/// At end-of-frame the registry processes the queue against
/// <see cref="ImGui.IsKeyPressed(ImGuiKey)"/>.
///
/// <para>This is independent of focus management: hotkeys fire at scope-level
/// (Global / WindowFocused / ModalOnly), not based on a focused widget.</para>
/// </summary>
public static class Hotkey
{
    private struct Entry
    {
        public ImGuiKey Key;
        public ImGuiKey[]? Modifiers;
        public Action Callback;
        public HotkeyScope Scope;
        public bool WindowFocused; // captured at registration; ImGui state isn't valid at frame end
        public bool TopMostPopup;
    }

    [System.ThreadStatic]
    private static List<Entry>? _frameQueue;
    [System.ThreadStatic]
    private static int _lastProcessedFrame;

    /// <summary>Register a hotkey for the current frame.</summary>
    public static void Register(ImGuiKey key, Action callback, HotkeyScope scope = HotkeyScope.Global, ImGuiKey[]? modifiers = null)
    {
        _frameQueue ??= new List<Entry>();
        _frameQueue.Add(new Entry
        {
            Key = key,
            Modifiers = modifiers,
            Callback = callback,
            Scope = scope,
            WindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows),
            TopMostPopup = ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopup),
        });
    }

    /// <summary>
    /// Process registered hotkeys for the current frame. Idempotent — calling this
    /// multiple times in the same frame does nothing on subsequent calls.
    /// </summary>
    public static void ProcessFrame()
    {
        int frame = ImGui.GetFrameCount();
        if (_lastProcessedFrame == frame) return;
        _lastProcessedFrame = frame;

        if (_frameQueue == null || _frameQueue.Count == 0) return;
        try
        {
            foreach (var entry in _frameQueue)
            {
                if (!ImGui.IsKeyPressed(entry.Key, false)) continue;
                if (!ModifiersHeld(entry.Modifiers)) continue;
                if (entry.Scope == HotkeyScope.WindowFocused && !entry.WindowFocused) continue;
                if (entry.Scope == HotkeyScope.ModalOnly && !entry.TopMostPopup) continue;

                try { entry.Callback(); }
                catch { /* swallow — one bad hotkey shouldn't kill the rest */ }
            }
        }
        finally
        {
            _frameQueue.Clear();
        }
    }

    private static bool ModifiersHeld(ImGuiKey[]? mods)
    {
        if (mods == null) return true;
        for (int i = 0; i < mods.Length; i++)
            if (!ImGui.IsKeyDown(mods[i])) return false;
        return true;
    }
}
