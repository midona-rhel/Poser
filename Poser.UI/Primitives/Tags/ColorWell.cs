using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Color well — picto M5 <c>.well</c>: 28×28, radius 6, 1px
    /// <c>--color-border-primary</c> border, filled with the current color.
    /// Clicking opens an ImGui color picker in a glass popup (documented
    /// deviation: the picker interior is ImGui's, only the chrome is picto).
    /// Returns true while the color is being edited.
    /// </summary>
    public static bool ColorWell(string id, ref Vector4 color)
        => ColorWell(id, ref color, default);

    /// <summary>
    /// Color well with an RGB-only option: the picker hides alpha and the
    /// value's EXISTING alpha channel is preserved exactly — for values
    /// like whole-model tints whose alpha belongs to the game.
    /// </summary>
    public static bool ColorWell(string id, ref Vector4 color, bool rgbOnly)
        => ColorWell(id, ref color, new ColorWellProps { RgbOnly = rgbOnly });

    public static bool ColorWell(string id, ref Vector4 color, in ColorWellProps props)
    {
        Stylesheet.EnsureInitialized();
        float scale = ImGuiHelpers.GlobalScale;

        var size = new Vector2(
            Crystarium.ActiveTheme.Controls.ColorWellSize,
            Crystarium.ActiveTheme.Controls.ColorWellSize) * scale;
        var hit = Interactive.Reserve(id, size, props.Disabled, Norvrandt.AvailableHeight);

        var dl = ImGui.GetWindowDrawList();
        float r = Crystarium.ActiveTheme.Radii.Control * scale;
        dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                props.Disabled
                    ? Crystarium.ActiveTheme.Chrome.UnavailableFill
                    : color with { W = 1f })), r);
        // 1px border painted inside the box edge (CSS border-box)
        dl.AddRect(hit.ScreenMin + new Vector2(0.5f, 0.5f), hit.ScreenMax - new Vector2(0.5f, 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.ControlBorder)), r, ImDrawFlags.None, 1f * scale);

        string popupId = id + "_picker";
        if (hit.Clicked && !props.Disabled) ImGui.OpenPopup(popupId);

        bool changed = false;
        var popupColor = color;
        bool rgbOnly = props.RgbOnly;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = Crystarium.ActiveTheme.Floating.ColorPickerWidth,
                Height = Crystarium.ActiveTheme.Floating.ColorPickerHeight,
                Padding = Crystarium.ActiveTheme.Floating.ColorPickerPadding,
                AnchorMin = hit.ScreenMin,
                AnchorMax = hit.ScreenMax,
            },
            () =>
            {
                ImGui.SetNextItemWidth(
                    (Crystarium.ActiveTheme.Floating.ColorPickerWidth
                        - Crystarium.ActiveTheme.Floating.ColorPickerPadding * 2f)
                    * scale);
                var flags = ImGuiColorEditFlags.NoSidePreview
                    | ImGuiColorEditFlags.NoSmallPreview;
                if (rgbOnly)
                    flags |= ImGuiColorEditFlags.NoAlpha;
                float keepAlpha = popupColor.W;
                changed = ImGui.ColorPicker4(id + "_pk", ref popupColor, flags);
                if (rgbOnly)
                    popupColor.W = keepAlpha;
            });
        if (changed)
            color = popupColor;
        if (!string.IsNullOrEmpty(props.Tooltip)
            && (hit.Hovered || (props.Disabled
                && HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, props.Tooltip!);
        return changed;
    }

    /// <summary>
    /// Accent swatch — picto M5 <c>.swatch</c>: 28px circle, inset 1px ring
    /// white @ .18; active state adds a 2px <c>--color-bg-app</c> gap ring plus a
    /// 2px <c>--color-primary</c> outer ring. Returns true when clicked.
    /// </summary>
    public static bool Swatch(string id, Vector4 color, bool active)
    {
        Stylesheet.EnsureInitialized();
        float scale = ImGuiHelpers.GlobalScale;

        var size = new Vector2(
            Crystarium.ActiveTheme.Controls.ColorWellSize,
            Crystarium.ActiveTheme.Controls.ColorWellSize) * scale;
        var hit = Interactive.Reserve(id, size, disabled: false, Norvrandt.AvailableHeight);

        var dl = ImGui.GetWindowDrawList();
        var center = (hit.ScreenMin + hit.ScreenMax) * 0.5f;
        float radius = Crystarium.ActiveTheme.Controls.ColorWellSize * 0.5f * scale;

        // box-shadow spread rings drawn as filled discs back-to-front — one clean
        // AA boundary per edge (stroked circles fringe on both stroke edges).
        if (active)
        {
            dl.AddCircleFilled(center, radius + 4f * scale,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.Primary)), 64);
            dl.AddCircleFilled(center, radius + 2f * scale,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.PickerWell)), 64);
        }
        dl.AddCircleFilled(center, radius,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color with { W = 1f })), 64);
        // inset ring: box-shadow inset 0 0 0 1px rgba(255,255,255,.18) — 13..14px band
        dl.AddCircle(center, radius - 0.5f * scale,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.PickerBorder)), 64, 1f * scale);

        return hit.Clicked;
    }
}
