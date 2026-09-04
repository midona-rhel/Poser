using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal readonly record struct SwatchLayoutPlan(
    float HitSide,
    float DotRadius,
    float SlotGap,
    float CenterPitch,
    float PaletteWidth,
    float ActiveOuterRadius);

public static partial class Crystarium
{
    private const float SwatchWrapSize = 20f;
    private const float SwatchDotSize = 14f;
    private const float SwatchInsetRing = 1f;
    private const float SwatchHoverRing = 1f;
    private const float SwatchActiveGap = 2f;
    private const float SwatchActiveRing = 2f;
    private const int SwatchSegments = 64;
    // ImGui subtracts half a framebuffer pixel from stroked circles.
    private const float CircleStrokeBias = 0.5f;
    internal const float PaletteMinHeight = 26f;
    private const float PalettePaddingX = 6f;
    private const float PaletteGap = 4f;
    private const float PaletteBorder = 1f;
    private const float PaletteRadius = 40f;
    private static readonly Vector4 PaletteFill = new(0f, 0f, 0f, 0.15f);

    internal static SwatchLayoutPlan SwatchLayout(int count) => new(
        SwatchWrapSize,
        SwatchDotSize * 0.5f,
        PaletteGap,
        SwatchWrapSize + PaletteGap,
        PaletteBorder * 2f
            + PalettePaddingX * 2f
            + count * SwatchWrapSize
            + MathF.Max(0f, count - 1f) * PaletteGap,
        SwatchDotSize * 0.5f + SwatchActiveGap + SwatchActiveRing);

    /// <summary>Draws a color well and its picker.</summary>
    public static bool ColorWell(
        string id,
        Vector4 color,
        System.Action<Vector4> onChange,
        ControlStyle style = default,
        bool rgbOnly = false,
        bool disabled = false,
        string? help = null,
        bool hdr = false,
        Action? onBegin = null,
        Action? onCommit = null)
    {
        var theme = ActiveTheme;
        float side = ControlSizing.Height(
            style.Height, theme.Controls.ColorWellSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        float scale = metrics.Scale;
        var hit = Interactive.Reserve(id, metrics.Size, disabled);
        var wellMin = hit.ScreenMin;
        var wellMax = wellMin + new Vector2(side * scale);

        PaintColorWellBox(hit, color, disabled);

        if (hit.Clicked && !disabled)
        {
            onBegin?.Invoke();
            OpenPopover(ColorWellPopupId(id));
        }

        bool changed = DrawColorWellPopup(
            id, wellMin, wellMax, color, rgbOnly, onChange, hdr, disabled, onBegin, onCommit);
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, disabled, wellMin, wellMax))
            HoverHelp.Explain(id, wellMin, wellMax, help!);
        return changed;
    }

    /// <summary>Paints the leading square of the reserved control.</summary>
    private static void PaintColorWellBox(
        in InteractionResult hit, Vector4 color, bool disabled)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var wellMin = hit.ScreenMin;
        var wellMax = wellMin + new Vector2(hit.ScreenMax.Y - hit.ScreenMin.Y);

        var dl = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control;
        float borderPx = 1f * scale;
        var fill = disabled
            ? theme.Chrome.UnavailableFill
            : color with { W = 1f };
        var border = theme.Chrome.ColorWellBorder;

        if (disabled)
        {
            ControlPaint.DisabledGroup(
                dl, wellMin, wellMax,
                radius * scale, borderPx, fill, border,
                theme.Chrome.ControlDisabledOpacity);
            return;
        }

        BoxRenderer.Draw(dl, wellMin, wellMax, new BoxStyle
        {
            BackgroundColor = fill,
            BorderWidth = 1f,
            BorderRadius = radius,
            BorderTopColor = border,
            BorderRightColor = border,
            BorderBottomColor = border,
            BorderLeftColor = border,
        });
    }

    /// <summary>Builds the picker id from its control id.</summary>
    private static string ColorWellPopupId(string id) => id + "_picker";

    /// <summary>Draws the anchored picker and reports edited colors.</summary>
    private static bool DrawColorWellPopup(
        string id,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector4 color,
        bool rgbOnly,
        Action<Vector4> onChange,
        bool hdr = false, bool disabled = false,
        Action? onBegin = null, Action? onCommit = null)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        bool changed = false;
        bool ended = false;
        var popupColor = color;
        FloatingSurface.Popup(
            ColorWellPopupId(id),
            new FloatingSurfaceProps
            {
                Width = theme.Floating.ColorPickerWidth,
                Height = theme.Floating.ColorPickerHeight,
                Padding = theme.Floating.ColorPickerPadding,
                AnchorMin = anchorMin,
                AnchorMax = anchorMax,
            },
            () =>
            {
                ImGui.SetNextItemWidth(
                    (theme.Floating.ColorPickerWidth
                        - theme.Floating.ColorPickerPadding * 2f)
                    * scale);
                var flags = ImGuiColorEditFlags.NoSidePreview
                    | ImGuiColorEditFlags.NoSmallPreview;
                if (rgbOnly)
                    flags |= ImGuiColorEditFlags.NoAlpha;
                // HDR mode preserves components above one.
                if (hdr)
                    flags |= ImGuiColorEditFlags.Hdr
                        | ImGuiColorEditFlags.Float;
                float keepAlpha = popupColor.W;
                ImGui.BeginDisabled(disabled);
                changed = ImGui.ColorPicker4(id + "_pk", ref popupColor, flags);
                if (ImGui.IsItemActivated()) onBegin?.Invoke();
                ended = ImGui.IsItemDeactivatedAfterEdit();
                ImGui.EndDisabled();
                if (rgbOnly)
                    popupColor.W = keepAlpha;
            });
        if (changed)
            onChange(popupColor);
        if (ended) onCommit?.Invoke();
        return changed;
    }

    /// <summary>Draws one solid swatch with shared hover and selection chrome.</summary>
    public static bool Swatch(
        string id,
        Vector4 color,
        bool active,
        ControlStyle style = default,
        string? help = null)
    {
        return SwatchCore(id, active, style, help,
            (draw, center, radius) => draw.AddCircleFilled(
                center,
                radius,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = 1f })),
                SwatchSegments));
    }

    /// <summary>Draws the split theme swatch with the shared interaction chrome.</summary>
    internal static bool ThemeModeSwatch(
        string id,
        bool active,
        ControlStyle style = default,
        string? help = null) =>
        SwatchCore(id, active, style, help,
            (draw, center, radius) =>
                ThemeModeGlyph.Draw(draw, ThemeModeGlyph.Plan(center, radius)));

    private static bool SwatchCore(
        string id,
        bool active,
        ControlStyle style,
        string? help,
        Action<ImDrawListPtr, Vector2, float> paintContent)
    {
        float side = ControlSizing.Height(style.Height, SwatchLayout(1).HitSide);
        var metrics = ControlSizing.Resolve(style, side, side);
        var hit = Interactive.Reserve(id, metrics.Size, disabled: false);

        PaintSwatchDot(
            ImGui.GetWindowDrawList(), hit.ScreenMin, side, active,
            hit.Hovered, paintContent);

        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>Paints shared chrome around caller-provided swatch content.</summary>
    private static void PaintSwatchDot(
        ImDrawListPtr dl,
        Vector2 boxMin,
        float side,
        bool active,
        bool hovered,
        Action<ImDrawListPtr, Vector2, float> paintContent)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var center = boxMin + new Vector2(side * 0.5f * scale);
        float wrapRadius = side * 0.5f * scale;
        float dotRadius = MathF.Min(
            wrapRadius,
            SwatchLayout(1).DotRadius * scale);

        if (hovered)
            SwatchRing(
                dl, center, wrapRadius, SwatchHoverRing * scale,
                theme.TextMuted);
        if (active)
        {
            SwatchRing(
                dl, center, dotRadius, SwatchActiveGap * scale,
                theme.Chrome.PickerWell);
            SwatchRing(
                dl, center, dotRadius + SwatchActiveGap * scale,
                SwatchActiveRing * scale,
                theme.Chrome.Primary);
        }
        paintContent(dl, center, dotRadius);
        SwatchRing(
            dl, center, dotRadius - SwatchInsetRing * scale,
            SwatchInsetRing * scale,
            theme.Chrome.ControlHover);
    }

    /// <summary>Draws the shared pill and evenly spaced swatch slots.</summary>
    /// <param name="count">Number of swatch slots to lay out.</param>
    /// <param name="swatch">Draws the slot at the current cursor.</param>
    /// <param name="style">Overrides the natural size.</param>
    public static void ColorPalette(
        int count,
        Action<int> swatch,
        ControlStyle style = default)
    {
        SwatchLayoutPlan layout = SwatchLayout(count);
        var metrics = ControlSizing.Resolve(
            style, layout.PaletteWidth, PaletteMinHeight);
        float scale = metrics.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var paletteMax = origin + metrics.Size;

        var dl = ImGui.GetWindowDrawList();
        var border = ActiveTheme.Border;
        BoxRenderer.Draw(dl, origin, paletteMax, new BoxStyle
        {
            BackgroundColor = PaletteFill,
            BorderWidth = PaletteBorder,
            BorderRadius = PaletteRadius,
            BorderTopColor = border,
            BorderRightColor = border,
            BorderBottomColor = border,
            BorderLeftColor = border,
        });

        float contentHeight = metrics.LogicalHeight - PaletteBorder * 2f;
        var first = origin + new Vector2(
            (PaletteBorder + PalettePaddingX) * scale,
            (PaletteBorder + (contentHeight - SwatchWrapSize) * 0.5f)
                * scale);
        // Fixed-width callers clip slots at the palette edge.
        dl.PushClipRect(origin, paletteMax, true);
        try
        {
            for (int i = 0; i < count; i++)
            {
                ImGui.SetCursorScreenPos(first + new Vector2(
                    i * layout.CenterPitch * scale, 0f));
                swatch(i);
            }
        }
        finally
        {
            dl.PopClipRect();
            // Absolute-positioned children do not reserve the palette box.
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(metrics.Size);
        }
    }

    /// <summary>Draws a solid-color palette and reports every picked index.</summary>
    public static bool SwatchPalette(
        string id,
        IReadOnlyList<Vector4> colors,
        int selected,
        Action<int> onChange,
        IReadOnlyList<string>? names = null,
        ControlStyle style = default)
    {
        bool picked = false;
        ColorPalette(
            colors.Count,
            index =>
            {
                if (!Swatch(
                        $"{id}##{index}",
                        colors[index],
                        index == selected,
                        help: names is not null && index < names.Count
                            ? names[index]
                            : null))
                    return;
                picked = true;
                onChange(index);
            },
            style);
        return picked;
    }

    /// <summary>Draws an annulus without tinting its interior.</summary>
    private static void SwatchRing(
        ImDrawListPtr drawList,
        Vector2 center,
        float innerRadius,
        float width,
        Vector4 color)
    {
        if (width <= 0f || innerRadius < 0f)
            return;
        drawList.AddCircle(
            center,
            innerRadius + width * 0.5f + CircleStrokeBias,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
            SwatchSegments,
            width);
    }
}
