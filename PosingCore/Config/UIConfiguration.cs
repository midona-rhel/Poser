using Dalamud.Bindings.ImGui;

namespace Poser.Config;

/// <summary>
/// Configuration for Poser UI colors.
/// Each color can either use a custom value or reference an ImGuiCol from the Dalamud theme.
/// </summary>
public enum PanelDock { Left, Right, Floating, Hidden }

public class UIConfiguration
{
    // Settings -> Display/UI (Crystarium shell; the ImGuiCol entries below are legacy-window theming)
    public int AccentIndex { get; set; } = 0;
    public PanelDock SidebarDock { get; set; } = PanelDock.Left;
    public PanelDock InspectorDock { get; set; } = PanelDock.Right;
    public bool ShowTreeGuides { get; set; } = true;
    public System.Collections.Generic.Dictionary<string, string> Keybinds { get; set; } = new();

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
