using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary><c>.swatchWrap { width: 16px; height: 16px }</c> — the
    /// circular hit/hover target the 14px dot centers in. A CSS literal,
    /// not a token: <c>Controls.ColorWellSize</c> is the WELL's side and
    /// the two are different components.</summary>
    private const float SwatchWrapSize = 16f;

    /// <summary>The wrap is 16px around a 14px <c>.swatch</c>, i.e. 1px of
    /// wrap shows on every side. DEVIATION: the CSS pins the dot at an
    /// absolute 14px, but a caller that sizes the control (PageForm's
    /// accent row asks for 26px) means the CONTROL, so the gap is held
    /// constant and the dot follows the resolved box instead.</summary>
    private const float SwatchRingGap = 1f;

    /// <summary><c>.swatch { box-shadow: inset 0 0 0 1px
    /// var(--color-subtle-overlay) }</c>.</summary>
    private const float SwatchInsetRing = 1f;

    /// <summary><c>.swatchWrap:hover { box-shadow: 0 0 0 1px
    /// var(--color-text-tertiary) }</c> — spread only, so the band sits
    /// entirely OUTSIDE the wrap's border box.</summary>
    private const float SwatchHoverRing = 1f;

    /// <summary>m5 <c>.swatch.-active</c>'s first shadow,
    /// <c>0 0 0 2px var(--color-bg-app)</c>. DEVIATION:
    /// ColorPalette.module.css has no selected state at all — its swatches
    /// are copy targets — so the frozen mockup remains the only source for
    /// the active ring pair.</summary>
    private const float SwatchActiveGap = 2f;

    /// <summary>m5 <c>.swatch.-active</c>'s second shadow,
    /// <c>0 0 0 4px var(--color-primary)</c>, measured from the outside of
    /// the gap ring.</summary>
    private const float SwatchActiveRing = 2f;

    /// <summary>Segment count for the swatch circles; a 16px circle needs
    /// far fewer, but the count is fixed so a scaled or caller-sized
    /// swatch keeps the same silhouette.</summary>
    private const int SwatchSegments = 64;

    /// <summary>
    /// Color well — picto m5 <c>.well</c>: 26×26 (<c>Controls.ColorWellSize</c>;
    /// the mockup draws 28), radius 6, 1px <c>--color-border-primary</c>
    /// border, filled with the current color. Clicking opens an ImGui
    /// color picker in a glass popup (documented deviation: the picker
    /// interior is ImGui's, only the chrome is picto). Returns true while
    /// the color is being edited.
    /// <para>The well has NO hover treatment: neither the m5 mockup nor
    /// any Picto CSS module declares one for it, and inventing chrome the
    /// reference does not have is worse than the omission.</para>
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
        var theme = ActiveTheme;
        // The well is square by default: its content width IS the resolved
        // side, so the side is settled first and fed back as the content.
        float side = ControlSizing.Height(
            style.Height, theme.Controls.ColorWellSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        float scale = metrics.Scale;
        var hit = Interactive.Reserve(id, metrics.Size, disabled);
        var wellMin = hit.ScreenMin;
        var wellMax = wellMin + new Vector2(side * scale);

        var dl = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control;
        float borderPx = 1f * scale;
        // DEVIATION (no CSS source): a well with no value shows the
        // neutral unavailable fill rather than a colour it does not have.
        var fill = disabled
            ? theme.Chrome.UnavailableFill
            : color with { W = 1f };
        var border = theme.Chrome.ColorWellBorder;  // --color-border-primary

        if (disabled)
        {
            // m5 declares no disabled well; this borrows the Picto action
            // button family's `.btn:disabled { opacity: .35 }` GROUP
            // opacity, reproduced the way Button and Dropdown do — the
            // chrome draws non-overlapping (fill inset to the border's
            // inner edge, the ring carrying the analytically flattened
            // border-over-fill). There is no text to compensate.
            float groupOpacity = theme.Chrome.ControlDisabledOpacity;
            dl.AddRectFilled(
                wellMin + new Vector2(borderPx),
                wellMax - new Vector2(borderPx),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(fill.Fade(groupOpacity))),
                MathF.Max(0f, radius * scale - borderPx));
            dl.AddRect(
                wellMin + new Vector2(borderPx * 0.5f),
                wellMax - new Vector2(borderPx * 0.5f),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(
                        ColorEx.FlattenOver(border, fill)
                            .Fade(groupOpacity))),
                MathF.Max(0f, radius * scale - borderPx * 0.5f),
                ImDrawFlags.None,
                borderPx);
        }
        else
        {
            // Shared paint: BoxRenderer scales the radius and insets the
            // 1px border to the CSS border box itself, so the old
            // hand-written — and unscaled — half-pixel inset is gone.
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

        string popupId = id + "_picker";
        if (hit.Clicked && !disabled)
            OpenPopover(popupId);

        bool changed = false;
        var popupColor = color;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = theme.Floating.ColorPickerWidth,
                Height = theme.Floating.ColorPickerHeight,
                Padding = theme.Floating.ColorPickerPadding,
                AnchorMin = wellMin,
                AnchorMax = wellMax,
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
                float keepAlpha = popupColor.W;
                changed = ImGui.ColorPicker4(id + "_pk", ref popupColor, flags);
                if (rgbOnly)
                    popupColor.W = keepAlpha;
            });
        if (changed)
            onChange(popupColor);
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, disabled, wellMin, wellMax))
            HoverHelp.Explain(id, wellMin, wellMax, help!);
        return changed;
    }

    /// <summary>
    /// Accent swatch — Picto's <c>shared/ui/ColorPalette</c>:
    /// a 16px circular <c>.swatchWrap</c> around a 14px <c>.swatch</c>
    /// carrying <c>box-shadow: inset 0 0 0 1px var(--color-subtle-overlay)</c>,
    /// with <c>.swatchWrap:hover</c> adding a 1px
    /// <c>--color-text-tertiary</c> ring outside the wrap. The module
    /// declares NO transition, so hover is instant and the swatch owns no
    /// motion channel. Selection is the m5 mockup's ring pair (2px
    /// <c>--color-bg-app</c> gap, then 2px <c>--color-primary</c>), the
    /// only source that describes one. Returns true when clicked.
    /// <para>The pill <c>.palette</c> container the CSS module also
    /// defines has no Crystarium counterpart — a swatch draws itself and
    /// nothing else.</para>
    /// </summary>
    public static bool Swatch(
        string id,
        Vector4 color,
        bool active,
        ControlStyle style = default,
        string? help = null)
    {
        var theme = ActiveTheme;
        // Same square contract as ColorWell above.
        float side = ControlSizing.Height(style.Height, SwatchWrapSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        float scale = metrics.Scale;
        var hit = Interactive.Reserve(id, metrics.Size, disabled: false);

        var dl = ImGui.GetWindowDrawList();
        var center = hit.ScreenMin + new Vector2(side * 0.5f * scale);
        float wrapRadius = side * 0.5f * scale;
        float dotRadius = MathF.Max(
            0f, wrapRadius - SwatchRingGap * scale);

        // .swatchWrap:hover — spread-only shadow on the WRAP, so the band
        // is the 1px annulus just outside it and the 1px of wrap showing
        // around the dot stays untouched.
        if (hit.Hovered)
            SwatchRing(
                dl, center, wrapRadius, SwatchHoverRing * scale,
                theme.TextMuted);            // --color-text-tertiary
        if (active)
        {
            SwatchRing(
                dl, center, dotRadius, SwatchActiveGap * scale,
                theme.Chrome.PickerWell);    // --color-bg-app
            SwatchRing(
                dl, center, dotRadius + SwatchActiveGap * scale,
                SwatchActiveRing * scale,
                theme.Chrome.Primary);       // --color-primary
        }
        dl.AddCircleFilled(
            center,
            dotRadius,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(color with { W = 1f })),
            SwatchSegments);
        SwatchRing(
            dl, center, dotRadius - SwatchInsetRing * scale,
            SwatchInsetRing * scale,
            theme.Chrome.ControlHover);      // --color-subtle-overlay

        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>
    /// One <c>box-shadow: 0 0 0 Npx</c> band on a circle, stroked at the
    /// band's mid-radius. A spread shadow is an ANNULUS: it paints from
    /// the element's edge outward and leaves every pixel inside it alone.
    /// The superseded emulation stacked filled discs back-to-front, which
    /// only survives while every ring colour is opaque — the palette's
    /// hover ring is <c>--color-text-tertiary</c> at 50% white and a disc
    /// would tint the swatch's own gap through it.
    /// </summary>
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
            innerRadius + width * 0.5f,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
            SwatchSegments,
            width);
    }
}
