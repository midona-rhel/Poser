using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Shared state-paint recipes: the small, repeated chrome fragments that
/// several controls draw identically. One home means a change to the
/// recipe cannot land on some controls and miss others.
/// </summary>
internal static class ControlPaint
{
    /// <summary>
    /// THE keyboard focus-visible ring (<c>:focus-visible</c>): a 2px
    /// primary-hover outline offset 1px outside the control's rect.
    ///
    /// <para>Pointer interaction never invents one — the caller keeps the
    /// gate (<c>hit.Focused &amp;&amp; Interactive.KeyboardNavActive</c>)
    /// because only the caller knows whether the control is focusable at
    /// all. This method only paints.</para>
    /// </summary>
    /// <param name="drawList">Draw list to render into.</param>
    /// <param name="min">Top-left of the control's visual rect.</param>
    /// <param name="max">Bottom-right of the control's visual rect.</param>
    /// <param name="radius">The control's ALREADY-SCALED corner radius.</param>
    /// <param name="scale">Global UI scale.</param>
    public static void FocusRing(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float radius,
        float scale)
    {
        float offset = 1f * scale;
        float thickness = 2f * scale;
        float expand = offset + thickness * 0.5f;
        drawList.AddRect(
            min - new Vector2(expand),
            max + new Vector2(expand),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.PrimaryHover)),
            radius + expand,
            ImDrawFlags.None,
            thickness);
    }

    /// <summary>
    /// The 1px hairline separator: a filled rect one physical pixel tall
    /// (<c>max(1, scale)</c>, so it never vanishes below 1x and never
    /// blurs into a half-pixel above it).
    ///
    /// <para>The color is packed RAW — <see cref="ColorEx.ApplyAlpha"/> is
    /// deliberately NOT applied, preserving the behavior of every call
    /// site this was extracted from. Whether hairlines should honor the
    /// ImGui style alpha like the rest of the chrome is a normalization
    /// decision, not part of this extraction.</para>
    /// </summary>
    /// <para>The right edge is taken as an ABSOLUTE x, not a width: every
    /// call site already knows where the line ends, and passing the end
    /// directly avoids the <c>left + (right − left)</c> round-trip, which
    /// IEEE-754 does not guarantee returns <c>right</c>.</para>
    /// </summary>
    /// <param name="drawList">Draw list to render into.</param>
    /// <param name="topLeft">Left end of the hairline, at its top edge.</param>
    /// <param name="right">Absolute x of the hairline's right end.</param>
    /// <param name="scale">Global UI scale.</param>
    /// <param name="color">Separator color, packed without style alpha.</param>
    public static void Separator(
        ImDrawListPtr drawList,
        Vector2 topLeft,
        float right,
        float scale,
        Vector4 color)
        => drawList.AddRectFilled(
            topLeft,
            new Vector2(right, topLeft.Y + MathF.Max(1f, scale)),
            ImGui.ColorConvertFloat4ToU32(color));

    /// <summary>
    /// THE bordered-control disabled group (<c>.btn:disabled { opacity:
    /// .35 }</c> and everything that borrows it): the chrome a
    /// <see cref="DisabledGroup"/> call paints, plus the transform the
    /// caller must put on whatever it draws INSIDE that chrome.
    ///
    /// <para>CSS group opacity flattens the element ONCE and then fades
    /// the result. A draw list has no group, so the chrome is drawn
    /// non-overlapping instead and the content is pre-corrected to land
    /// where the group would have. That correction is the whole reason
    /// this type exists: it is not optional decoration, it is the second
    /// half of the recipe, and it differs by content kind. A text run
    /// blends its glyphs over the faded fill, so it needs a COMPENSATED
    /// color (<see cref="Label"/>); a glyph rendered by the icon path
    /// already composites against the same backdrop the group does, so it
    /// only needs its own opacity scaled (<see cref="Glyph"/>).</para>
    ///
    /// <para>Both accessors read the caller's own content value, because
    /// the group does not decide what color the label is or how opaque
    /// the icon was — it only decides what the group does to them.</para>
    /// </summary>
    internal readonly struct DisabledContent
    {
        private readonly Vector4 fill;
        private readonly float groupOpacity;

        internal DisabledContent(Vector4 fill, float groupOpacity)
        {
            this.fill = fill;
            this.groupOpacity = groupOpacity;
        }

        /// <summary>The color a text run must draw with so that blending
        /// its glyphs over the faded fill lands on the group result. See
        /// <see cref="ColorEx.DisabledLabelCompensation"/> for the exact
        /// solution and the bound where the fill is opaque.</summary>
        public Vector4 Label(Vector4 color) =>
            ColorEx.DisabledLabelCompensation(
                color, fill, Crystarium.ActiveTheme.Surface, groupOpacity);

        /// <summary>The opacity an icon/glyph must render at: its own
        /// resting opacity scaled by the group's.</summary>
        public float Glyph(float opacity) => opacity * groupOpacity;
    }

    /// <summary>
    /// Paints the disabled chrome of a bordered control and returns the
    /// content transform for its label or glyph.
    ///
    /// <para>The fill is inset to the border's INNER edge and faded, and
    /// the ring carries the analytically flattened border-over-fill —
    /// exact for every backdrop — so the two never overlap and no pixel
    /// is faded twice. That is what makes this equivalent to a CSS group
    /// rather than an approximation of one.</para>
    ///
    /// <para>The caller keeps its own disabled-opacity constant: WHICH
    /// opacity a control fades to is a per-control decision, the same way
    /// <see cref="FocusRing"/> leaves the focus gate to the caller.</para>
    /// </summary>
    /// <param name="drawList">Draw list to render into.</param>
    /// <param name="min">Top-left of the control's border box.</param>
    /// <param name="max">Bottom-right of the control's border box.</param>
    /// <param name="radius">The control's ALREADY-SCALED corner radius.</param>
    /// <param name="borderPx">The ALREADY-SCALED border width.</param>
    /// <param name="fill">The control's enabled background color.</param>
    /// <param name="border">The control's enabled border color.</param>
    /// <param name="groupOpacity">The caller's disabled group opacity.</param>
    /// <returns>The transform to apply to the control's content.</returns>
    internal static DisabledContent DisabledGroup(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float radius,
        float borderPx,
        Vector4 fill,
        Vector4 border,
        float groupOpacity)
    {
        float inset = borderPx * 0.5f;
        drawList.AddRectFilled(
            min + new Vector2(borderPx),
            max - new Vector2(borderPx),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(fill.Fade(groupOpacity))),
            MathF.Max(0f, radius - borderPx));
        drawList.AddRect(
            min + new Vector2(inset),
            max - new Vector2(inset),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(
                    ColorEx.FlattenOver(border, fill).Fade(groupOpacity))),
            MathF.Max(0f, radius - inset),
            ImDrawFlags.None,
            borderPx);
        return new DisabledContent(fill, groupOpacity);
    }
}
