using System;
using Dalamud.Bindings.ImGui;

namespace Poser.Config;
public enum UITheme
{
    Auto,
    Light,
    LightGray,
    Gray,
    Dark,
    Blue,
    Purple,
}

public class UIConfiguration
{
    // Below this alpha, translucent surfaces no longer read reliably.
    public const float MinimumFillOpacity = 0.66f;

    private float _fillOpacity = 1f;
    public UITheme Theme { get; set; } = UITheme.Dark;
    public int AccentIndex { get; set; } = 0;
    public float FillOpacity
    {
        get => _fillOpacity;
        set => _fillOpacity = ClampFillOpacity(value);
    }
    public bool BackdropBlur { get; set; } = true;
    // Config files may be edited outside the slider's range.
    public static float ClampFillOpacity(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, MinimumFillOpacity, 1f) : 1f;
    public bool DetachedShell { get; set; }
    public bool ShowTreeGuides { get; set; } = true;
    public bool MapMirrorSelection { get; set; }
    public bool ShowInGPose { get; set; } = true;
    public bool ShowInCutscene { get; set; } = true;
    public bool ShowWhenGameUiHidden { get; set; }
    public bool SwapRotationXY { get; set; }
    public System.Collections.Generic.Dictionary<string, string> Keybinds { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, KeybindSlots> Bindings { get; set; } = new();
    public void MigrateKeybindsToSlots()
    {
        foreach (var (action, chord) in Keybinds)
        {
            if (string.IsNullOrWhiteSpace(chord) || Bindings.ContainsKey(action))
                continue;
            Bindings[action] = new KeybindSlots(chord);
        }
        Keybinds.Clear();
    }
    public UIColorEntry Background { get; set; } = new(ImGuiCol.WindowBg);
    public UIColorEntry ControlBackground { get; set; } = new(ImGuiCol.FrameBg);
    public UIColorEntry Text { get; set; } = new(ImGuiCol.Text);
    public UIColorEntry TextDisabled { get; set; } = new(ImGuiCol.TextDisabled);
    public UIColorEntry Border { get; set; } = new(ImGuiCol.Border);
    public UIColorEntry SelectionActive { get; set; } = new(ImGuiCol.Header);
    public UIColorEntry SelectionActiveHovered { get; set; } = new(ImGuiCol.HeaderHovered);
    public UIColorEntry SelectionHovered { get; set; } = new(ImGuiCol.HeaderHovered);
    public UIColorEntry TitleBar { get; set; } = new(ImGuiCol.TitleBg);
    public UIColorEntry TitleBarActive { get; set; } = new(ImGuiCol.TitleBgActive);
    public UIColorEntry Button { get; set; } = new(ImGuiCol.Button);
    public UIColorEntry ButtonHovered { get; set; } = new(ImGuiCol.ButtonHovered);
    public UIColorEntry ButtonActive { get; set; } = new(ImGuiCol.ButtonActive);
}
