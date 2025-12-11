using System;
using System.Collections.Generic;
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
        using var row = PoserUI.Row(PoserUI.FrameHeight);
        row.Label(label, labelWidth);
        bool changed = row.Slider($"##{label}", ref value, min, max);
        if (changed)
            ConfigurationService.Instance.Save();
        return changed;
    }

    /// <summary>
    /// Draws a labeled scrubber row for numerical values.
    /// </summary>
    public static bool ScrubberRow(string label, ref float value, float min, float max, float step = 0f, float labelWidth = DefaultLabelWidth)
    {
        using var row = PoserUI.Row(PoserUI.ScrubberHeight);
        row.Label(label, labelWidth);
        bool changed = row.Scrubber($"##{label}", ref value, min, max, step);
        if (changed)
            ConfigurationService.Instance.Save();
        return changed;
    }

    /// <summary>
    /// Draws a labeled color picker row (uint ABGR format).
    /// </summary>
    public static bool ColorRow(string label, ref uint color, float labelWidth = DefaultLabelWidth)
    {
        using var row = PoserUI.Row(PoserUI.FrameHeight);
        row.Label(label, labelWidth);
        row.Stretch();
        bool changed = row.RightColorEdit($"##{label}", ref color);
        if (changed)
            ConfigurationService.Instance.Save();
        return changed;
    }

    /// <summary>
    /// Draws a labeled checkbox row.
    /// </summary>
    public static bool CheckboxRow(string label, ref bool value, float labelWidth = DefaultLabelWidth)
    {
        using var row = PoserUI.Row(PoserUI.FrameHeight);
        row.Label(label, labelWidth);
        bool changed = row.Checkbox($"##{label}", ref value);
        if (changed)
            ConfigurationService.Instance.Save();
        return changed;
    }

    /// <summary>
    /// Draws a UIColorEntry row with dropdown for theme color or custom.
    /// </summary>
    public static void ColorEntryRow(string label, UIColorEntry entry, float labelWidth = DefaultLabelWidth)
    {
        using var row = PoserUI.Row(PoserUI.DropdownHeight);
        row.Label(label, labelWidth);

        // Dropdown index: 0 = Custom, 1+ = ImGuiCol values
        int currentIndex = entry.UseCustomColor ? 0 : entry.ThemeColorIndex + 1;

        if (row.Dropdown($"##combo_{label}", ref currentIndex, ImGuiColNames, 140))
        {
            if (currentIndex == 0)
            {
                if (!entry.UseCustomColor)
                    entry.CustomColor = entry.Resolve();
                entry.UseCustomColor = true;
            }
            else
            {
                entry.UseCustomColor = false;
                entry.ThemeColorIndex = currentIndex - 1;
            }
            ConfigurationService.Instance.Save();
        }

        row.Spacer(8);

        var resolvedColor = entry.Resolve();
        if (row.ColorEdit($"##color_{label}", ref resolvedColor))
        {
            entry.UseCustomColor = true;
            entry.CustomColor = resolvedColor;
            ConfigurationService.Instance.Save();
        }
    }

    /// <summary>
    /// Draws a section header row. Adds blank row above if not at the top of content.
    /// </summary>
    public static void SectionHeader(string text)
    {
        // Add spacing above if not the first element (cursor Y > small threshold)
        if (ImGui.GetCursorPosY() > 5f)
        {
            using var spacer = PoserUI.Row(PoserUI.FrameHeight);
            // Empty row for spacing
        }

        using var row = PoserUI.Row(ImGui.GetTextLineHeight());
        row.Header(text);
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
