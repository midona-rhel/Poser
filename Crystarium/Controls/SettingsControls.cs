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
        bool changed = false;
        float localValue = value;

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label(label, labelWidth);
            row.Fill(w =>
            {
                ImGui.SetNextItemWidth(w);
                if (ImGui.SliderFloat($"##{label}", ref localValue, min, max))
                    changed = true;
            });
        }

        if (changed)
        {
            value = localValue;
            ConfigurationService.Instance.Save();
        }
        return changed;
    }

    /// <summary>
    /// Draws a labeled scrubber row for numerical values.
    /// </summary>
    public static bool ScrubberRow(string label, ref float value, float min, float max, float step = 0f, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        float localValue = value;

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label(label, labelWidth);
            row.Fill(w =>
            {
                if (Scrubber.Draw($"##{label}", ref localValue, min, max, step, w))
                    changed = true;
            });
        }

        if (changed)
        {
            value = localValue;
            ConfigurationService.Instance.Save();
        }
        return changed;
    }

    /// <summary>
    /// Draws a labeled color picker row (uint ABGR format).
    /// </summary>
    public static bool ColorRow(string label, ref uint color, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        uint localColor = color;

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label(label, labelWidth);
            row.Spacer();
            row.Fixed(Flex.RowHeight, (w, h) =>
            {
                var vec4 = ImGui.ColorConvertU32ToFloat4(localColor);
                if (ImGui.ColorEdit4($"##{label}", ref vec4, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    localColor = ImGui.ColorConvertFloat4ToU32(vec4);
                    changed = true;
                }
            });
        }

        if (changed)
        {
            color = localColor;
            ConfigurationService.Instance.Save();
        }
        return changed;
    }

    /// <summary>
    /// Draws a labeled checkbox row.
    /// </summary>
    public static bool CheckboxRow(string label, ref bool value, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        bool localValue = value;

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label(label, labelWidth);
            row.Fixed(PoserCheckbox.Size / PoserUI.Scale, (w, h) =>
            {
                float offsetY = (h - PoserCheckbox.Size) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

                if (PoserCheckbox.Draw($"##{label}", ref localValue))
                    changed = true;
            });
        }

        if (changed)
        {
            value = localValue;
            ConfigurationService.Instance.Save();
        }
        return changed;
    }

    /// <summary>
    /// Draws a UIColorEntry row with dropdown for theme color or custom.
    /// </summary>
    public static void ColorEntryRow(string label, UIColorEntry entry, float labelWidth = DefaultLabelWidth)
    {
        int currentIndex = entry.UseCustomColor ? 0 : entry.ThemeColorIndex + 1;
        var resolvedColor = entry.Resolve();

        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label(label, labelWidth);

            row.Fixed(140, (w, h) =>
            {
                if (PoserDropdown.Draw($"##combo_{label}", ref currentIndex, ImGuiColNames, w))
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
            });

            row.Fixed(Flex.RowHeight, (w, h) =>
            {
                if (ImGui.ColorEdit4($"##color_{label}", ref resolvedColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    entry.UseCustomColor = true;
                    entry.CustomColor = resolvedColor;
                    ConfigurationService.Instance.Save();
                }
            });
        }
    }

    /// <summary>
    /// Draws a section header row. Adds blank row above if not at the top of content.
    /// </summary>
    public static void SectionHeader(string text)
    {
        // Add spacing above if not the first element
        if (ImGui.GetCursorPosY() > 5f)
        {
            using var spacer = Flex.Row();
            // Empty row for spacing
        }

        using (var row = Flex.Row())
        {
            row.Fill((w, h) =>
            {
                float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                ImGui.TextColored(UIColors.TextDisabled, text);
            });
        }
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
