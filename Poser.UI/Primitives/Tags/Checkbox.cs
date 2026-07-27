using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public static float CheckboxSize =>
        ActiveTheme.Controls.CheckboxSize * ImGuiHelpers.GlobalScale;

    public static bool Checkbox(
        string id,
        bool value,
        Action<bool> onChange,
        bool disabled = false,
        string? help = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float size = ActiveTheme.Controls.CheckboxSize * scale;
        var hit = Interactive.Reserve(
            id, new Vector2(size), disabled, Norvrandt.AvailableHeight);
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
            hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            radius);

        if (!value)
        {
            var border = ActiveTheme.Glass.BorderBottom;
            border.W *= opacity;
            float inset = 0.5f * scale;
            draw.AddRect(
                hit.ScreenMin + new Vector2(inset),
                hit.ScreenMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                scale);
        }
        else
        {
            var check = ActiveTheme.Chrome.Checkmark;
            check.W *= opacity;
            float iconSpan = size * (10f / 14f);
            float unit = iconSpan / 24f;
            var origin = hit.ScreenMin +
                new Vector2((size - iconSpan) * 0.5f);
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
