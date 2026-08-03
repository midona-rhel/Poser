using System;
using System.Collections.Generic;
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
    internal const float SwatchWrapSize = 16f;

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

    /// <summary>ImGui's <c>AddCircle</c> does not stroke on the radius it
    /// is handed: BOTH its branches build the path at
    /// <c>radius - 0.5f</c> before <c>PathStroke</c> centres the band on
    /// it. <c>AddCircleFilled</c> uses the radius as given, so a ring and
    /// the disc it hugs are half a pixel out of register unless the bias
    /// is added back. It is deliberately NOT multiplied by the UI scale:
    /// it is a rasterizer offset in FRAMEBUFFER pixels, the same half
    /// pixel at 1×, 1.25×, and 1.5×.</summary>
    private const float CircleStrokeBias = 0.5f;

    /// <summary><c>.palette { min-height: 26px }</c> — the pill's
    /// border-box height. Its 24px CONTENT box is what the 16px wraps
    /// centre in, so this is not interchangeable with
    /// <c>Controls.WorkspaceHeight</c>, which happens to share the
    /// number but means a control's height.</summary>
    internal const float PaletteMinHeight = 26f;

    /// <summary><c>.palette { padding: 0 6px }</c>.</summary>
    internal const float PalettePaddingX = 6f;

    /// <summary><c>.palette { gap: 2px }</c> — flex gap, so n wraps
    /// contribute n−1 gaps and a single wrap contributes none.</summary>
    internal const float PaletteGap = 2f;

    /// <summary><c>.palette { border: 1px solid
    /// var(--color-border-secondary) }</c> — the width; the colour is the
    /// one <c>var()</c> in the module and comes from the theme.</summary>
    internal const float PaletteBorder = 1f;

    /// <summary><c>.palette { border-radius: 40px }</c>. NOT
    /// <c>Radii.Pill</c>: Picto writes <c>999px</c> where it means
    /// "always a pill" (AuthWorkspace) and <c>40px</c> here, which only
    /// reads as a pill while the box stays under 80px tall.</summary>
    internal const float PaletteRadius = 40f;

    /// <summary><c>.palette { background: rgba(0, 0, 0, 0.15) }</c> — a
    /// raw rgba in the module, NOT a <c>var()</c>, so it is identical in
    /// every theme and belongs here rather than in ChromeTokens (no
    /// tokens.css entry carries black at .15 either).</summary>
    internal static readonly Vector4 PaletteFill = new(0f, 0f, 0f, 0.15f);

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

        PaintColorWellBox(hit, color, disabled);

        if (hit.Clicked && !disabled)
            OpenPopover(ColorWellPopupId(id));

        bool changed = DrawColorWellPopup(
            id, wellMin, wellMax, color, rgbOnly, onChange);
        if (!string.IsNullOrEmpty(help) && HoverHelp.Gate(
                hit, disabled, wellMin, wellMax))
            HoverHelp.Explain(id, wellMin, wellMax, help!);
        return changed;
    }

    /// <summary>
    /// The well's BOX alone — fill, border, and the disabled group — so
    /// the retained twin drives the SAME pixels. The well is the LEADING
    /// SQUARE of the reserved rect: a caller may widen the control, and
    /// the swatch stays as wide as it is tall.
    /// </summary>
    internal static void PaintColorWellBox(
        in InteractionResult hit, Vector4 color, bool disabled)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var wellMin = hit.ScreenMin;
        var wellMax = wellMin + new Vector2(hit.ScreenMax.Y - hit.ScreenMin.Y);

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
            // opacity — the same recipe Button and Dropdown use, from the
            // one implementation. The well is chrome ONLY: its content is
            // the fill itself, so the returned content transform has
            // nothing to apply to and is deliberately dropped.
            ControlPaint.DisabledGroup(
                dl, wellMin, wellMax,
                radius * scale, borderPx, fill, border,
                theme.Chrome.ControlDisabledOpacity);
            return;
        }

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

    /// <summary>The popover handle the well opens, derived from the
    /// control id in ONE place so the opener and the surface cannot drift
    /// apart.</summary>
    internal static string ColorWellPopupId(string id) => id + "_picker";

    /// <summary>
    /// The picker popup's mechanics — the anchored glass surface and the
    /// raw <c>ColorPicker4</c> inside it. The picker interior is the named
    /// NATIVE boundary and is deliberately not transcribed; this exists so
    /// a twin can stage the identical popup instead of copying it. Returns
    /// true on the frames the picker edits, having already reported the new
    /// colour to <paramref name="onChange"/>.
    /// </summary>
    internal static bool DrawColorWellPopup(
        string id,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector4 color,
        bool rgbOnly,
        Action<Vector4> onChange)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        bool changed = false;
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
                float keepAlpha = popupColor.W;
                changed = ImGui.ColorPicker4(id + "_pk", ref popupColor, flags);
                if (rgbOnly)
                    popupColor.W = keepAlpha;
            });
        if (changed)
            onChange(popupColor);
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
    /// <para>A swatch draws itself and nothing else: the pill
    /// <c>.palette</c> container the same module defines is
    /// <see cref="ColorPalette"/>, which owns the row's chrome and
    /// spacing.</para>
    /// </summary>
    public static bool Swatch(
        string id,
        Vector4 color,
        bool active,
        ControlStyle style = default,
        string? help = null)
    {
        // Same square contract as ColorWell above.
        float side = ControlSizing.Height(style.Height, SwatchWrapSize);
        var metrics = ControlSizing.Resolve(style, side, side);
        var hit = Interactive.Reserve(id, metrics.Size, disabled: false);

        PaintSwatchDot(
            ImGui.GetWindowDrawList(), hit.ScreenMin, side, color, active,
            hit.Hovered);

        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);
        return hit.Clicked;
    }

    /// <summary>
    /// The swatch's PAINT alone — the hover ring, the active ring pair, the
    /// dot and its inset ring — so the retained twin drives the same pixels.
    /// <paramref name="side"/> is the wrap's LOGICAL side; the dot follows it
    /// with the ring gap held constant, exactly as the control scales.
    /// </summary>
    internal static void PaintSwatchDot(
        ImDrawListPtr dl,
        Vector2 boxMin,
        float side,
        Vector4 color,
        bool active,
        bool hovered)
    {
        var theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var center = boxMin + new Vector2(side * 0.5f * scale);
        float wrapRadius = side * 0.5f * scale;
        float dotRadius = MathF.Max(
            0f, wrapRadius - SwatchRingGap * scale);

        // .swatchWrap:hover — spread-only shadow on the WRAP, so the band
        // is the 1px annulus just outside it and the 1px of wrap showing
        // around the dot stays untouched.
        if (hovered)
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
    }

    /// <summary>
    /// Color palette — Picto's <c>shared/ui/ColorPalette</c>
    /// <c>.palette</c>: the pill CONTAINER its <see cref="Swatch"/>es sit
    /// in. A 1px <c>--color-border-secondary</c> border over
    /// <c>rgba(0,0,0,.15)</c> at <c>border-radius: 40px</c>, with
    /// <c>padding: 0 6px</c> and <c>min-height: 26px</c> around a
    /// <c>flex-wrap: nowrap</c> row of 16px wraps separated by
    /// <c>gap: 2px</c> and centred in the content box
    /// (<c>align-items: center</c>).
    /// <para>Every metric above is a raw px in the module; the single
    /// <c>var()</c> it uses is the border colour, which is why that alone
    /// reads from the theme and the rest are CSS literals, exactly like
    /// the swatch metrics this file already carries.</para>
    /// <para>The container takes no id and reserves no hit target — it
    /// reserves LAYOUT, not input: the module declares no hover, focus, or
    /// click on <c>.palette</c>, so the only interactive things here are
    /// the children, and they own their own ids.
    /// <paramref name="count"/> is a parameter rather than something the
    /// body reports because the pill is painted BEFORE its children and
    /// therefore has to know its width first.</para>
    /// <para>Three declarations are deliberately NOT implemented, all for
    /// the same reason — they are the PARENT's half of the box model, and
    /// modelling them here would be the CSS engine this deliberately is
    /// not: <c>margin: 0 0 8px</c> (external spacing the placing caller
    /// owns; Crystarium models no margins anywhere),
    /// <c>align-self: center</c> (the parent's cross axis), and
    /// <c>max-width: 100%</c> (the parent's content box — a caller that
    /// needs the clamp asks for <see cref="UiWidth.Fill"/>, and the
    /// natural width never reaches it on its own).</para>
    /// </summary>
    /// <param name="count">Number of swatch slots to lay out.</param>
    /// <param name="swatch">Draws slot <c>i</c>; the cursor is already at
    /// that slot's 16px top-left.</param>
    /// <param name="style">Overrides the natural <c>width: fit-content</c>
    /// and the 26px minimum height.</param>
    public static void ColorPalette(
        int count,
        Action<int> swatch,
        ControlStyle style = default)
    {
        // width: fit-content — border + padding on both sides, the wraps,
        // and one gap between each adjacent pair.
        float naturalWidth =
            PaletteBorder * 2f
            + PalettePaddingX * 2f
            + count * SwatchWrapSize
            + MathF.Max(0f, count - 1f) * PaletteGap;
        var metrics = ControlSizing.Resolve(
            style, naturalWidth, PaletteMinHeight);
        float scale = metrics.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var paletteMax = origin + metrics.Size;

        var dl = ImGui.GetWindowDrawList();
        var border = ActiveTheme.Border;  // --color-border-secondary
        // Shared paint: BoxRenderer scales the radius, clamps it to the
        // pill, and insets the 1px border into the border box.
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

        // align-items: center inside the content box — the border box
        // less its two borders — and the row starts after the padding.
        float contentHeight = metrics.LogicalHeight - PaletteBorder * 2f;
        var first = origin + new Vector2(
            (PaletteBorder + PalettePaddingX) * scale,
            (PaletteBorder + (contentHeight - SwatchWrapSize) * 0.5f)
                * scale);
        // overflow: hidden — a caller-fixed width narrower than the row
        // clips it rather than letting wraps spill past the pill.
        dl.PushClipRect(origin, paletteMax, true);
        try
        {
            for (int i = 0; i < count; i++)
            {
                ImGui.SetCursorScreenPos(first + new Vector2(
                    i * (SwatchWrapSize + PaletteGap) * scale, 0f));
                swatch(i);
            }
        }
        finally
        {
            // Both of these are GLOBAL for the rest of the frame — the
            // window's draw list clip stack and the window's cursor — so a
            // swatch callback that throws must not be able to strand
            // either one. The unwind still leaves the palette's box
            // correctly reserved, because the reservation IS the restore.
            dl.PopClipRect();
            // Flow reservation. The children drew at ABSOLUTE positions
            // and reserved only their own 16px wraps, none of which is the
            // container's box; the pill itself was pure draw-list paint.
            // So the palette claims its FULL resolved rect here, as the
            // LAST item it emits — which is what a same-line or measuring
            // consumer reads (ImGui's SameLine and content-extent both
            // follow the most recent item, not the cursor). Reserving from
            // `origin` rather than from wherever the last child left the
            // cursor is also what makes an EMPTY palette (count 0) reserve
            // its min-height by min-padding-width pill instead of nothing,
            // and it leaves the cursor exactly where the reservation's own
            // flow puts it: the next line under the pill.
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(metrics.Size);
        }
    }

    /// <summary>
    /// The palette ROW in one call: the <see cref="ColorPalette"/> pill filled
    /// with one <see cref="Swatch"/> per colour, the selection carried by index
    /// and a name list riding as per-dot help. The two-part form stays public
    /// for callers whose slots are not a colour list; this is what an accent
    /// row actually asks for.
    /// <para>Every click reports, including one on the already-selected dot: a
    /// palette is a set of copy targets, so "picked" is the event, not
    /// "changed". Returns true on the frames a dot was clicked.</para>
    /// </summary>
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

    /// <summary>
    /// One <c>box-shadow: 0 0 0 Npx</c> band on a circle, stroked at the
    /// band's mid-radius. A spread shadow is an ANNULUS: it paints from
    /// the element's edge outward and leaves every pixel inside it alone.
    /// The superseded emulation stacked filled discs back-to-front, which
    /// only survives while every ring colour is opaque — the palette's
    /// hover ring is <c>--color-text-tertiary</c> at 50% white and a disc
    /// would tint the swatch's own gap through it.
    /// <para>The band must land on exactly
    /// <c>[innerRadius, innerRadius + width]</c>, which is the radial span
    /// CSS gives a spread/inset shadow. The mid-radius alone does not get
    /// there because of <see cref="CircleStrokeBias"/>: the radius handed
    /// to <c>AddCircle</c> is the one ImGui shrinks, so it is the one that
    /// carries the correction.</para>
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
            innerRadius + width * 0.5f + CircleStrokeBias,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
            SwatchSegments,
            width);
    }
}
