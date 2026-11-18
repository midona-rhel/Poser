using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class SwitchWidgets
{
    /// <summary>
    /// An on/off switch like the ones on phones, drawn to match the picto
    /// design's toggle switch: a 32×20 rounded track, grey when off and the
    /// accent colour when on, and a 16px white knob that slides 12px, goes from
    /// 60% to full opacity when switched on, and has a soft shadow.
    /// </summary>
    public static bool Switch(
        string id,
        bool value,
        System.Action<bool> onChange,
        ControlStyle style = default,
        bool disabled = false,
        string? help = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float logicalHeight = ControlSizing.Height(
            style.Height, ActiveTheme.Controls.SwitchHeight);
        float controlScale =
            logicalHeight / ActiveTheme.Controls.SwitchHeight;
        float logicalWidth = ControlSizing.Width(
            style,
            ActiveTheme.Controls.SwitchWidth * controlScale,
            ImGui.GetContentRegionAvail().X / scale);
        var size = new Vector2(
            logicalWidth,
            logicalHeight) * scale;
        disabled |= Interactive.GreyedOut;
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.Clicked)
        {
            value = !value;
            onChange(value);
        }

        PaintSwitch(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            value, disabled, ImGui.GetID(id));

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>How the switch animates, as in the picto design: the knob's
    /// slide, its opacity and the track colour all follow this one curve over
    /// 0.2 seconds.</summary>
    private static readonly Transition SwitchTransition =
        Transition.CubicBezier(0.2f, 0.25f, 0.1f, 0.25f, 1f);

    /// <summary>
    /// Draws the switch alone: track, knob shadow and knob. The knob's size
    /// follows the box it is given, so the box alone sets the switch's scale.
    /// The animation is tracked per <paramref name="identity"/>; 0 draws the
    /// end state with no animation.
    /// </summary>
    private static void PaintSwitch(
        ImDrawListPtr dl, Vector2 min, Vector2 max, bool value, bool disabled,
        uint identity = 0)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float logicalWidth = (max.X - min.X) / scale;
        float logicalHeight = (max.Y - min.Y) / scale;
        float controlScale =
            logicalHeight / ActiveTheme.Controls.SwitchHeight;
        // Disabled controls fade by the same amount everywhere.
        float opacity = disabled ? ActiveTheme.Chrome.ControlDisabledOpacity : 1f;

        float eased = value ? 1f : 0f;
        if (identity != 0)
            eased = SwitchTransition.Evaluate(Motion.Progress(
                identity, value, SwitchTransition.DurationSeconds));

        var trackColor = ColorEx.PremultipliedLerp(
                ActiveTheme.Chrome.SwitchOff,   // rgba(128,128,128,.25)
                ActiveTheme.Chrome.Primary,     // --color-primary
                eased)
            .Fade(opacity);
        dl.AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(trackColor)),
            ActiveTheme.Controls.SwitchHeight * 0.5f * scale);

        // The knob: a 16px circle, 2px from the left when off and 14px when on.
        float knobInset = ActiveTheme.Spacing.One * controlScale;
        float knobSize =
            ActiveTheme.Controls.SwitchKnobSize * controlScale;
        float knobTravel = logicalWidth - knobSize - knobInset * 2f;
        float knobLeft = (knobInset + knobTravel * eased) * scale;
        float knobRadius = knobSize * 0.5f * scale;
        var center = min + new Vector2(
            knobLeft + knobRadius,
            logicalHeight * 0.5f * scale);

        // Two rings stand in for the soft shadow under the knob.
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveTheme.Chrome.SwitchShadow.Fade(opacity))), 32);
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + 0.4f * scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveTheme.Chrome.SwitchHighlight.Fade(opacity))), 32);

        // The knob is white in every theme: 60% opacity when off, full when on.
        var knobColor = ColorEx.PremultipliedLerp(
                ActiveTheme.Chrome.SwitchKnob.Fade(0.6f),
                ActiveTheme.Chrome.SwitchKnob,
                eased)
            .Fade(opacity);
        dl.AddCircleFilled(center, knobRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(knobColor)), 32);
    }
}
