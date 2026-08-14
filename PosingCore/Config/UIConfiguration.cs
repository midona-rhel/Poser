using Dalamud.Bindings.ImGui;

namespace Poser.Config;

/// <summary>
/// Configuration for Poser UI colors.
/// Each color can either use a custom value or reference an ImGuiCol from the Dalamud theme.
/// </summary>
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
    // Settings -> Display/UI (Crystarium shell; the ImGuiCol entries below are legacy-window theming)
    public UITheme Theme { get; set; } = UITheme.Dark;
    public int AccentIndex { get; set; } = 0;
    // ONE toggle: detached mode floats the toolbar and the sidebar as their
    // own windows while the main content keeps the inspector. Off is the
    // compact single-window UI.
    public bool DetachedShell { get; set; }
    public bool ShowTreeGuides { get; set; } = true;
    public bool MapMirrorSelection { get; set; }

    /// <summary>
    /// Keep Poser's windows up while GPose has the game's own UI hidden.
    /// Defaults ON because that is what the shell forced before the toggle
    /// existed — a posing tool that vanishes on entering GPose is useless.
    /// </summary>
    public bool ShowInGPose { get; set; } = true;

    /// <summary>Keep Poser's windows up during cutscenes. Same history as
    /// <see cref="ShowInGPose"/>: forced on before it was a choice.</summary>
    public bool ShowInCutscene { get; set; } = true;

    /// <summary>
    /// Keep Poser's windows up when the GAME's UI is hidden — the automatic
    /// hide (a cutscene or a duty starting) and the user's own Scroll Lock
    /// hide alike. Off is what Poser did before this existed: the photographer
    /// hides the HUD for the shot and Poser goes with it.
    /// </summary>
    public bool ShowWhenGameUiHidden { get; set; }

    /// <summary>
    /// Show the rotation row's first two columns exchanged — Brio's
    /// <c>SwapRotationXandY</c> (PosingConfiguration.cs:44), for people whose
    /// reference tool labels those axes the other way round. Default OFF, the
    /// order every other Poser surface uses. It is a reading convention: the
    /// stored rotation is untouched either way, so turning it on cannot alter
    /// a pose or make one file import differently from another.
    /// </summary>
    public bool SwapRotationXY { get; set; }

    /// <summary>
    /// The pre-dual-slot single binding per action. Kept so a config written
    /// before the second slot existed still deserializes; emptied by
    /// <see cref="MigrateKeybindsToSlots"/> the first time such a config
    /// loads, and never written again — one live home for a binding.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> Keybinds { get; set; } = new();

    /// <summary>Each action's primary and secondary chords. Absent actions
    /// take <see cref="KeybindRegistry.Default"/>; an EMPTY slot is a
    /// deliberate unbind and outranks the default.</summary>
    public System.Collections.Generic.Dictionary<string, KeybindSlots> Bindings { get; set; } = new();

    /// <summary>
    /// Config v3: an existing single binding becomes that action's PRIMARY
    /// chord and its secondary starts empty. A binding already carried in
    /// <see cref="Bindings"/> wins — the migration only fills gaps, so
    /// running it twice cannot undo an edit made after the first run.
    /// </summary>
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

    // Background colors
    public UIColorEntry Background { get; set; } = new(ImGuiCol.WindowBg);
    public UIColorEntry ControlBackground { get; set; } = new(ImGuiCol.FrameBg);

    // Text colors
    public UIColorEntry Text { get; set; } = new(ImGuiCol.Text);
    public UIColorEntry TextDisabled { get; set; } = new(ImGuiCol.TextDisabled);

    // Border
    public UIColorEntry Border { get; set; } = new(ImGuiCol.Border);

    // Selection (active = selected item, hovered = mouse over)
    public UIColorEntry SelectionActive { get; set; } = new(ImGuiCol.Header);
    public UIColorEntry SelectionActiveHovered { get; set; } = new(ImGuiCol.HeaderHovered);
    public UIColorEntry SelectionHovered { get; set; } = new(ImGuiCol.HeaderHovered);

    // Title bar
    public UIColorEntry TitleBar { get; set; } = new(ImGuiCol.TitleBg);
    public UIColorEntry TitleBarActive { get; set; } = new(ImGuiCol.TitleBgActive);

    // Button states
    public UIColorEntry Button { get; set; } = new(ImGuiCol.Button);
    public UIColorEntry ButtonHovered { get; set; } = new(ImGuiCol.ButtonHovered);
    public UIColorEntry ButtonActive { get; set; } = new(ImGuiCol.ButtonActive);
}
