using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class LegacyCrystarium
{
    /// <summary>
    /// iOS-style toggle switch — pixel transcription of picto
    /// shared/ui/ToggleSwitch/ToggleSwitch.module.css: 32×20 track (radius 10),
    /// off bg rgba(128,128,128,.25), on bg #3297FF; 16px white knob at (2,2),
    /// opacity .6 off → 1 on, sliding +12px; knob shadow 0 1px 3px rgba(0,0,0,.2).
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
            style.Height, Crystarium.ActiveTheme.Controls.SwitchHeight);
        float controlScale =
            logicalHeight / Crystarium.ActiveTheme.Controls.SwitchHeight;
        float logicalWidth = ControlSizing.Width(
            style.Width,
            Crystarium.ActiveTheme.Controls.SwitchWidth * controlScale,
            ImGui.GetContentRegionAvail().X / scale);
        var size = new Vector2(
            logicalWidth,
            logicalHeight) * scale;
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.Clicked)
        {
            value = !value;
            onChange(value);
        }

        PaintSwitch(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            value, disabled);

        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>
    /// The toggle's pixels alone — pill, knob shadow rings, knob — so the
    /// retained twin drives the SAME paint. The knob metrics are
    /// PROPORTIONAL to the resolved box, so the seam recovers the caller's
    /// control scale from the rect it is handed rather than taking it as a
    /// parameter: the box is the single source both paths already agree on.
    /// </summary>
    internal static void PaintSwitch(
        ImDrawListPtr dl, Vector2 min, Vector2 max, bool value, bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float logicalWidth = (max.X - min.X) / scale;
        float logicalHeight = (max.Y - min.Y) / scale;
        float controlScale =
            logicalHeight / Crystarium.ActiveTheme.Controls.SwitchHeight;
        // Shared disabled fade for retained controls.
        float opacity = disabled ? Crystarium.ActiveTheme.Chrome.ControlDisabledOpacity : 1f;

        var trackColor = (value
            ? Crystarium.ActiveTheme.Chrome.Primary          // --color-primary
            : Crystarium.ActiveTheme.Chrome.SwitchOff)     // rgba(128,128,128,.25)
            .Fade(opacity);
        dl.AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(trackColor)),
            Crystarium.ActiveTheme.Controls.SwitchHeight * 0.5f * scale);

        // knob: 16px circle, left 2px (off) / 14px (on), bottom 2px
        float knobInset = Crystarium.ActiveTheme.Spacing.One * controlScale;
        float knobSize =
            Crystarium.ActiveTheme.Controls.SwitchKnobSize * controlScale;
        float knobTravel = logicalWidth - knobSize - knobInset * 2f;
        float knobLeft = (knobInset + (value ? knobTravel : 0f)) * scale;
        float knobRadius = knobSize * 0.5f * scale;
        var center = min + new Vector2(
            knobLeft + knobRadius,
            logicalHeight * 0.5f * scale);

        // knob drop shadow (0 1px 3px rgba(0,0,0,.2)) — cheap two-ring approximation
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SwitchShadow.Fade(opacity))), 32);
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + 0.4f * scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SwitchHighlight.Fade(opacity))), 32);

        var knobColor = (value
            ? Crystarium.ActiveTheme.Chrome.Text
            : Crystarium.ActiveTheme.Chrome.TextMuted).Fade(opacity);
        dl.AddCircleFilled(center, knobRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(knobColor)), 32);
    }
}
