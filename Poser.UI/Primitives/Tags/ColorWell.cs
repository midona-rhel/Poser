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
    public static bool ColorWell(
        string id,
        Vector4 color,
        System.Action<Vector4> onChange,
        ControlStyle style = default,
        bool rgbOnly = false,
        bool disabled = false,
        string? help = null)
    {
        // The well is square by default: its content width IS the resolved
        // side, so the side is settled first and fed back as the content.
        float side = ControlSizing.Height(
            style.Height, Crystarium.ActiveTheme.Controls.ColorWellSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        float scale = metrics.Scale;
        var hit = Interactive.Reserve(id, metrics.Size, disabled);
        var wellMax = hit.ScreenMin + new Vector2(side * scale);

        var dl = ImGui.GetWindowDrawList();
        float r = Crystarium.ActiveTheme.Radii.Control * scale;
        dl.AddRectFilled(hit.ScreenMin, wellMax,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                disabled
                    ? Crystarium.ActiveTheme.Chrome.UnavailableFill
                    : color with { W = 1f })), r);
        // 1px border painted inside the box edge (CSS border-box)
        dl.AddRect(hit.ScreenMin + new Vector2(0.5f, 0.5f), wellMax - new Vector2(0.5f, 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.ControlBorder)), r, ImDrawFlags.None, 1f * scale);

        string popupId = id + "_picker";
        if (hit.Clicked && !disabled)
            FloatingSurface.OpenPopup(popupId);

        bool changed = false;
        var popupColor = color;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = Crystarium.ActiveTheme.Floating.ColorPickerWidth,
                Height = Crystarium.ActiveTheme.Floating.ColorPickerHeight,
                Padding = Crystarium.ActiveTheme.Floating.ColorPickerPadding,
                AnchorMin = hit.ScreenMin,
                AnchorMax = wellMax,
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
            onChange(popupColor);
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, disabled, hit.ScreenMin, hit.ScreenMax))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return changed;
    }

    /// <summary>
    /// Accent swatch — picto M5 <c>.swatch</c>: 28px circle, inset 1px ring
    /// white @ .18; active state adds a 2px <c>--color-bg-app</c> gap ring plus a
    /// 2px <c>--color-primary</c> outer ring. Returns true when clicked.
    /// </summary>
    public static bool Swatch(
        string id,
        Vector4 color,
        bool active,
        ControlStyle style = default,
        string? help = null)
    {
        // Same square contract as ColorWell above.
        float side = ControlSizing.Height(
            style.Height, Crystarium.ActiveTheme.Controls.ColorWellSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        float scale = metrics.Scale;
        var hit = Interactive.Reserve(id, metrics.Size, disabled: false);

        var dl = ImGui.GetWindowDrawList();
        var center = hit.ScreenMin + new Vector2(side * 0.5f * scale);
        float radius = side * 0.5f * scale;

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

        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }
}
