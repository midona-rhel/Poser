using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI;

/// <summary>
/// Static helper to resolve UI colors from configuration.
/// Use this instead of directly accessing ImGui.GetStyle().Colors.
/// </summary>
public static class UIColors
{
    private static UIConfiguration Config => ConfigurationService.Instance?.Config.UI ?? new UIConfiguration();

    // Background colors
    public static Vector4 Background => Config.Background.Resolve();
    public static Vector4 ControlBackground => Config.ControlBackground.Resolve();
    public static Vector4 ControlBackgroundHovered => ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered];

    // Text colors
    public static Vector4 Text => Config.Text.Resolve();
    public static Vector4 TextDisabled => Config.TextDisabled.Resolve();

    // Border
    public static Vector4 Border => Config.Border.Resolve();

    // Selection
    public static Vector4 SelectionActive => Config.SelectionActive.Resolve();
    public static Vector4 SelectionActiveHovered => Config.SelectionActiveHovered.Resolve();
    public static Vector4 SelectionHovered => Config.SelectionHovered.Resolve();

    // Title bar
    public static Vector4 TitleBar => Config.TitleBar.Resolve();
    public static Vector4 TitleBarActive => Config.TitleBarActive.Resolve();

    // Button states
    public static Vector4 Button => Config.Button.Resolve();
    public static Vector4 ButtonHovered => Config.ButtonHovered.Resolve();
    public static Vector4 ButtonActive => Config.ButtonActive.Resolve();

    // U32 versions for ImDrawList
    public static uint BackgroundU32 => ImGui.ColorConvertFloat4ToU32(Background);
    public static uint ControlBackgroundU32 => ImGui.ColorConvertFloat4ToU32(ControlBackground);
    public static uint TextU32 => ImGui.ColorConvertFloat4ToU32(Text);
    public static uint TextDisabledU32 => ImGui.ColorConvertFloat4ToU32(TextDisabled);
    public static uint BorderU32 => ImGui.ColorConvertFloat4ToU32(Border);
    public static uint SelectionActiveU32 => ImGui.ColorConvertFloat4ToU32(SelectionActive);
    public static uint SelectionActiveHoveredU32 => ImGui.ColorConvertFloat4ToU32(SelectionActiveHovered);
    public static uint SelectionHoveredU32 => ImGui.ColorConvertFloat4ToU32(SelectionHovered);
    public static uint TitleBarU32 => ImGui.ColorConvertFloat4ToU32(TitleBar);
    public static uint TitleBarActiveU32 => ImGui.ColorConvertFloat4ToU32(TitleBarActive);
    public static uint ButtonU32 => ImGui.ColorConvertFloat4ToU32(Button);
    public static uint ButtonHoveredU32 => ImGui.ColorConvertFloat4ToU32(ButtonHovered);
    public static uint ButtonActiveU32 => ImGui.ColorConvertFloat4ToU32(ButtonActive);

    // Standard colors
    public static Vector4 Black => new(0f, 0f, 0f, 1f);
    public static Vector4 White => new(1f, 1f, 1f, 1f);
    public static Vector4 Red => new(1f, 0f, 0f, 1f);
    public static Vector4 Green => new(0f, 1f, 0f, 1f);
    public static Vector4 Blue => new(0f, 0f, 1f, 1f);
    public static Vector4 Yellow => new(1f, 1f, 0f, 1f);
    public static Vector4 Purple => new(0.5f, 0f, 0.5f, 1f);
    public static Vector4 Orange => new(1f, 0.5f, 0f, 1f);
    public static Vector4 Gray => new(0.5f, 0.5f, 0.5f, 1f);

    // Standard colors U32
    public static uint BlackU32 => ImGui.ColorConvertFloat4ToU32(Black);
    public static uint WhiteU32 => ImGui.ColorConvertFloat4ToU32(White);
    public static uint RedU32 => ImGui.ColorConvertFloat4ToU32(Red);
    public static uint GreenU32 => ImGui.ColorConvertFloat4ToU32(Green);
    public static uint BlueU32 => ImGui.ColorConvertFloat4ToU32(Blue);
    public static uint YellowU32 => ImGui.ColorConvertFloat4ToU32(Yellow);
    public static uint PurpleU32 => ImGui.ColorConvertFloat4ToU32(Purple);
    public static uint OrangeU32 => ImGui.ColorConvertFloat4ToU32(Orange);
    public static uint GrayU32 => ImGui.ColorConvertFloat4ToU32(Gray);
}
