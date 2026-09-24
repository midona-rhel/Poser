using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.Config;

/// <summary>
/// Represents a UI color that can either be a custom color or reference an ImGuiCol from the theme.
/// </summary>
public class UIColorEntry
{
    /// <summary>
    /// If true, use CustomColor. If false, use the color from ImGuiCol specified by ThemeColorIndex.
    /// </summary>
    public bool UseCustomColor { get; set; }

    /// <summary>
    /// The custom color value (RGBA).
    /// </summary>
    public Vector4 CustomColor { get; set; }

    /// <summary>
    /// The ImGuiCol index to pull from theme when UseCustomColor is false.
    /// </summary>
    public int ThemeColorIndex { get; set; }

    public UIColorEntry()
    {
        UseCustomColor = false;
        CustomColor = new Vector4(1, 1, 1, 1);
        ThemeColorIndex = (int)ImGuiCol.Text;
    }

    public UIColorEntry(ImGuiCol defaultThemeColor)
    {
        UseCustomColor = false;
        CustomColor = new Vector4(1, 1, 1, 1);
        ThemeColorIndex = (int)defaultThemeColor;
    }

    public UIColorEntry(Vector4 customColor)
    {
        UseCustomColor = true;
        CustomColor = customColor;
        ThemeColorIndex = (int)ImGuiCol.Text;
    }

    /// <summary>
    /// Resolves this entry to an actual color value.
    /// </summary>
    public Vector4 Resolve()
    {
        if (UseCustomColor)
            return CustomColor;

        return ImGui.GetStyle().Colors[ThemeColorIndex];
    }

    /// <summary>
    /// Resolves this entry to a uint color (for ImDrawList).
    /// </summary>
    public uint ResolveU32()
    {
        return ImGui.ColorConvertFloat4ToU32(Resolve());
    }
}
