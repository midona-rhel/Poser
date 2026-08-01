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
}
