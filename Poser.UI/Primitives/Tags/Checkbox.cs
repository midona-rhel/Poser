using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class LegacyCrystarium
{
    public static Vector2 MeasureCheckbox(ControlStyle style = default)
    {
        // Square by default: the box's content width IS its resolved side.
        float side = ControlSizing.Height(
            style.Height, ActiveTheme.Controls.CheckboxSize);
        return ControlSizing.Resolve(style, side, side).Size;
    }

    public static bool Checkbox(
        string id,
        bool value,
        Action<bool> onChange,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null)
    {
        var measured = MeasureCheckbox(style);
        var hit = Interactive.Reserve(id, measured, disabled);
        if (hit.Clicked)
        {
            value = !value;
            onChange(value);
        }

        PaintCheckboxBox(
            ImGui.GetWindowDrawList(), hit.ScreenMin, measured.Y, value,
            disabled);

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>
    /// The box's PAINT alone — the fill, the unchecked inset ring, the
    /// Tabler check polyline, the disabled fade — so the retained twin
    /// drives the same pixels. <paramref name="side"/> is the resolved
    /// PHYSICAL side, the leading square of whatever was reserved.
    /// </summary>
    internal static void PaintCheckboxBox(
        ImDrawListPtr draw, Vector2 boxMin, float side, bool value,
        bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var boxMax = boxMin + new Vector2(side);
        float opacity = disabled ? ActiveTheme.Chrome.DisabledOpacity : 1f;
        var background = (value
            ? ActiveTheme.Chrome.Primary
            : ActiveTheme.Chrome.InputWell).Fade(opacity);
        float radius = ActiveTheme.Radii.Medium * scale;
        draw.AddRectFilled(
            boxMin,
            boxMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(background)),
            radius);

        if (!value)
        {
            var border = ActiveTheme.Glass.BorderBottom.Fade(opacity);
            float inset = 0.5f * scale;
            draw.AddRect(
                boxMin + new Vector2(inset),
                boxMax - new Vector2(inset),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
                MathF.Max(0f, radius - inset),
                ImDrawFlags.None,
                scale);
        }
        else
        {
            var check = ActiveTheme.Chrome.Checkmark.Fade(opacity);
            float iconSpan = side * (10f / 14f);
            float unit = iconSpan / 24f;
            var origin = boxMin + new Vector2((side - iconSpan) * 0.5f);
            draw.PathLineTo(origin + new Vector2(5f, 12f) * unit);
            draw.PathLineTo(origin + new Vector2(10f, 17f) * unit);
            draw.PathLineTo(origin + new Vector2(20f, 7f) * unit);
            draw.PathStroke(
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(check)),
                ImDrawFlags.None,
                2f * unit);
        }
    }
}
