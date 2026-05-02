using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Config;

namespace Poser.UI.Controls;

/// <summary>
/// Helpers for the Settings modal panes — labelled rows of common controls.
/// Built on the typed Crystarium element system.
/// </summary>
public static class SettingsControls
{
    private const float DefaultLabelWidth = 140f;

    private static readonly string[] ImGuiColNames;

    static SettingsControls()
    {
        var names = new List<string> { "Custom" };
        foreach (ImGuiCol col in Enum.GetValues(typeof(ImGuiCol))) names.Add(col.ToString());
        ImGuiColNames = names.ToArray();
    }

    public static bool SliderRow(string label, ref float value, float min, float max, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        float v = value;
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            DrawLabel(label, labelWidth);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } }, () =>
            {
                if (Crystarium.Slider($"##{label}", ref v, min, max)) changed = true;
            });
        });
        if (changed) { value = v; ConfigurationService.Instance.Save(); }
        return changed;
    }

    public static bool ScrubberRow(string label, ref float value, float min, float max, float step = 0f, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        float v = value;
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            DrawLabel(label, labelWidth);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } }, () =>
            {
                if (Crystarium.Scrubber($"##{label}", ref v, min, max, step)) changed = true;
            });
        });
        if (changed) { value = v; ConfigurationService.Instance.Save(); }
        return changed;
    }

    public static bool ColorRow(string label, ref uint color, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        uint c = color;
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            DrawLabel(label, labelWidth);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } });
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(Flex.RowHeight) } }, () =>
            {
                var vec4 = ImGui.ColorConvertU32ToFloat4(c);
                if (ImGui.ColorEdit4($"##{label}", ref vec4, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    c = ImGui.ColorConvertFloat4ToU32(vec4);
                    changed = true;
                }
            });
        });
        if (changed) { color = c; ConfigurationService.Instance.Save(); }
        return changed;
    }

    public static bool CheckboxRow(string label, ref bool value, float labelWidth = DefaultLabelWidth)
    {
        bool changed = false;
        bool v = value;
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            DrawLabel(label, labelWidth);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(Crystarium.CheckboxSize / PoserUI.Scale) } }, () =>
            {
                if (Crystarium.Checkbox($"##{label}", ref v)) changed = true;
            });
        });
        if (changed) { value = v; ConfigurationService.Instance.Save(); }
        return changed;
    }

    public static void ColorEntryRow(string label, UIColorEntry entry, float labelWidth = DefaultLabelWidth)
    {
        int currentIndex = entry.UseCustomColor ? 0 : entry.ThemeColorIndex + 1;
        var resolvedColor = entry.Resolve();

        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            DrawLabel(label, labelWidth);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(140) } }, () =>
            {
                if (Crystarium.Dropdown($"##combo_{label}", ImGuiColNames, ref currentIndex))
                {
                    if (currentIndex == 0)
                    {
                        if (!entry.UseCustomColor) entry.CustomColor = entry.Resolve();
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
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(Flex.RowHeight) } }, () =>
            {
                if (ImGui.ColorEdit4($"##color_{label}", ref resolvedColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    entry.UseCustomColor = true;
                    entry.CustomColor = resolvedColor;
                    ConfigurationService.Instance.Save();
                }
            });
        });
    }

    public static void SectionHeader(string text)
    {
        if (ImGui.GetCursorPosY() > 5f)
        {
            // Empty row for spacing
            Crystarium.Element(new ElementProps { Classes = Cls.Row });
        }
        Crystarium.Text(text, Cls.DisabledText);
    }

    public static void SectionEnd()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawLabel(string label, float labelWidth)
    {
        Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(labelWidth) } }, () =>
        {
            float h = Crystarium.AvailableHeight;
            if (h > 0)
            {
                float oy = (h - ImGui.GetTextLineHeight()) / 2f;
                if (oy > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + oy);
            }
            ImGui.Text(label);
        });
    }
}
