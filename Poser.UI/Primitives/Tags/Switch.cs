using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// iOS-style toggle switch — pixel transcription of picto
    /// shared/ui/ToggleSwitch/ToggleSwitch.module.css: 32×20 track (radius 10),
    /// off bg rgba(128,128,128,.25), on bg #3297FF; 16px white knob at (2,2),
    /// opacity .6 off → 1 on, sliding +12px; knob shadow 0 1px 3px rgba(0,0,0,.2).
    /// </summary>
    public static bool Switch(string id, ref bool value) => Switch(id, ref value, disabled: false);

    public static bool Switch(string id, ref bool value, bool disabled)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(
            Crystarium.ActiveTheme.Controls.SwitchWidth,
            Crystarium.ActiveTheme.Controls.SwitchHeight) * scale;
        var hit = Interactive.Reserve(id, size, disabled, Norvrandt.AvailableHeight);
        if (hit.Clicked) value = !value;

        var dl = ImGui.GetWindowDrawList();
        // Shared disabled fade (matches the .btn:disabled stylesheet opacity).
        float opacity = disabled ? Crystarium.ActiveTheme.Chrome.ControlDisabledOpacity : 1f;

        var trackColor = value
            ? Crystarium.ActiveTheme.Chrome.Primary          // --color-primary
            : Crystarium.ActiveTheme.Chrome.SwitchOff;     // rgba(128,128,128,.25)
        trackColor = trackColor with { W = trackColor.W * opacity };
        dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(trackColor)),
            Crystarium.ActiveTheme.Controls.SwitchHeight * 0.5f * scale);

        // knob: 16px circle, left 2px (off) / 14px (on), bottom 2px
        float knobInset = Crystarium.ActiveTheme.Spacing.One;
        float knobTravel = Crystarium.ActiveTheme.Controls.SwitchWidth
            - Crystarium.ActiveTheme.Controls.SwitchKnobSize - knobInset * 2f;
        float knobLeft = (knobInset + (value ? knobTravel : 0f)) * scale;
        float knobRadius = Crystarium.ActiveTheme.Controls.SwitchKnobSize * 0.5f * scale;
        var center = hit.ScreenMin + new Vector2(
            knobLeft + knobRadius,
            Crystarium.ActiveTheme.Controls.SwitchHeight * 0.5f * scale);

        // knob drop shadow (0 1px 3px rgba(0,0,0,.2)) — cheap two-ring approximation
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SwitchShadow with { W = Crystarium.ActiveTheme.Chrome.SwitchShadow.W * opacity })), 32);
        dl.AddCircleFilled(center + new Vector2(0f, 1f * scale), knobRadius + 0.4f * scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.SwitchHighlight with { W = Crystarium.ActiveTheme.Chrome.SwitchHighlight.W * opacity })), 32);

        var knobColor = (value ? Crystarium.ActiveTheme.Chrome.Text : Crystarium.ActiveTheme.Chrome.TextMuted) with { W = (value ? Crystarium.ActiveTheme.Chrome.Text.W : Crystarium.ActiveTheme.Chrome.TextMuted.W) * opacity };
        dl.AddCircleFilled(center, knobRadius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(knobColor)), 32);

        return hit.Clicked;
    }
}
