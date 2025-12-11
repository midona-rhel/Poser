using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;

namespace Poser.UI.Controls;

/// <summary>
/// Shared helper methods for drawing settings controls.
/// All controls use UIColors for consistent theming.
/// </summary>
public static class SettingsControls
{
    private const float DefaultLabelWidth = 140f;

    // ImGuiCol names for dropdown - built once
    private static readonly string[] ImGuiColNames;

    static SettingsControls()
    {
        var names = new List<string> { "Custom" };
        foreach (ImGuiCol col in Enum.GetValues(typeof(ImGuiCol)))
        {
            names.Add(col.ToString());
        }
        ImGuiColNames = names.ToArray();
    }

    /// <summary>
    /// Draws a labeled slider row.
    /// </summary>
    public static bool SliderRow(string label, ref float value, float min, float max, float labelWidth = DefaultLabelWidth)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(labelWidth * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.SliderFloat($"##{label}", ref value, min, max))
        {
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a labeled color picker row (uint ABGR format).
    /// </summary>
    public static bool ColorRow(string label, ref uint color, float labelWidth = DefaultLabelWidth)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(labelWidth * ImGuiHelpers.GlobalScale);
        var colorVec = ImGui.ColorConvertU32ToFloat4(color);
        if (ImGui.ColorEdit4($"##{label}", ref colorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            color = ImGui.ColorConvertFloat4ToU32(colorVec);
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a labeled checkbox row.
    /// </summary>
    public static bool CheckboxRow(string label, ref bool value, float labelWidth = DefaultLabelWidth)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(labelWidth * ImGuiHelpers.GlobalScale);
        if (ImGui.Checkbox($"##{label}", ref value))
        {
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a UIColorEntry row with dropdown for theme color or custom.
    /// </summary>
    public static void ColorEntryRow(string label, UIColorEntry entry, float labelWidth = DefaultLabelWidth)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(labelWidth * ImGuiHelpers.GlobalScale);

        // Dropdown index: 0 = Custom, 1+ = ImGuiCol values
        int currentIndex = entry.UseCustomColor ? 0 : entry.ThemeColorIndex + 1;

        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo($"##combo_{label}", ref currentIndex, ImGuiColNames, ImGuiColNames.Length))
        {
            if (currentIndex == 0)
            {
                // Switching to custom - copy current resolved color
                if (!entry.UseCustomColor)
                {
                    entry.CustomColor = entry.Resolve();
                }
                entry.UseCustomColor = true;
            }
            else
            {
                entry.UseCustomColor = false;
                entry.ThemeColorIndex = currentIndex - 1;
            }
            ConfigurationService.Instance.Save();
        }

        ImGui.SameLine();

        // Always show editable color picker - switches to custom when edited
        var resolvedColor = entry.Resolve();
        var flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;

        if (ImGui.ColorEdit4($"##color_{label}", ref resolvedColor, flags))
        {
            // Switch to custom mode and save the new color
            entry.UseCustomColor = true;
            entry.CustomColor = resolvedColor;
            ConfigurationService.Instance.Save();
        }
    }

    /// <summary>
    /// Draws a section header (disabled text + separator + spacing).
    /// </summary>
    public static void SectionHeader(string text)
    {
        ImGui.TextDisabled(text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws section spacing (spacing + separator + spacing).
    /// </summary>
    public static void SectionEnd()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }
}
