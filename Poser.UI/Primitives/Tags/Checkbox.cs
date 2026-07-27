using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static Vector2 MeasureCheckbox(ControlStyle style = default)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float side = ControlSizing.Height(
            style.Height, ActiveTheme.Controls.CheckboxSize);
        float width = ControlSizing.Width(
            style.Width,
            side,
            ImGui.GetContentRegionAvail().X / scale);
        return new Vector2(width, side) * scale;
    }

    public static bool Checkbox(
        string id,
        bool value,
        Action<bool> onChange,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var measured = MeasureCheckbox(style);
        float side = measured.Y;
        float width = measured.X;
        var hit = Interactive.Reserve(
            id, new Vector2(width, side), disabled);
        var boxMax = hit.ScreenMin + new Vector2(side);
        if (hit.Clicked)
        {
            value = !value;
            onChange(value);
        }

        float opacity = disabled ? ActiveTheme.Chrome.DisabledOpacity : 1f;
        var background = value
            ? ActiveTheme.Chrome.Primary
            : ActiveTheme.Chrome.InputWell;
        background.W *= opacity;
        var draw = ImGui.GetWindowDrawList();
        float radius = ActiveTheme.Radii.Medium * scale;
        draw.AddRectFilled(
            hit.ScreenMin,
            boxMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            radius);

        if (!value)
        {
            var border = ActiveTheme.Glass.BorderBottom;
            border.W *= opacity;
            float inset = 0.5f * scale;
            draw.AddRect(
                hit.ScreenMin + new Vector2(inset),
                boxMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                scale);
        }
        else
        {
            var check = ActiveTheme.Chrome.Checkmark;
            check.W *= opacity;
            float iconSpan = side * (10f / 14f);
            float unit = iconSpan / 24f;
            var origin = hit.ScreenMin +
                new Vector2((side - iconSpan) * 0.5f);
            draw.PathLineTo(origin + new Vector2(5f, 12f) * unit);
            draw.PathLineTo(origin + new Vector2(10f, 17f) * unit);
            draw.PathLineTo(origin + new Vector2(20f, 7f) * unit);
            draw.PathStroke(
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(check)),
                ImDrawFlags.None,
                2f * unit);
        }

        if (!string.IsNullOrEmpty(help) &&
            (hit.Hovered || (hit.Disabled &&
                HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }
}
