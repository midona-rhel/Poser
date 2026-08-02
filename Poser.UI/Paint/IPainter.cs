using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// Everything a painter hook is handed. It receives the element's own
/// declaration AND its resolved sheet, so a hook that needs a colour reads it
/// from the resolution the base already performed rather than decoding an
/// untyped argument the declaration smuggled past the runtime.
/// </summary>
internal readonly ref struct PaintContext
{
    internal PaintContext(
        in ElementRecord record,
        in Poser.UI.ResolvedPaint style,
        in Poser.UI.InteractionResult hit,
        uint identity,
        string id,
        Vector2 min,
        Vector2 max,
        ImDrawListPtr drawList)
    {
        Record = ref record;
        Style = ref style;
        Hit = hit;
        Identity = identity;
        Id = id;
        Min = min;
        Max = max;
        DrawList = drawList;
    }

    /// <summary>The element's own declaration — the title a header draws, the
    /// value a bar shows, the flags a state keys off. Every member is typed.
    /// </summary>
    internal readonly ref readonly ElementRecord Record;

    /// <summary>The flattened sheet chain: the colours, radius and border the
    /// hook must draw with when its seam accepts them.</summary>
    internal readonly ref readonly Poser.UI.ResolvedPaint Style;

    internal readonly Poser.UI.InteractionResult Hit;

    /// <summary>The ImGui id of the reservation, for animation state keyed to
    /// the item rather than to the call site.</summary>
    internal readonly uint Identity;

    /// <summary>The element's retained interaction-id string. A hook that
    /// registers hover help or opens a popover needs the SAME name the runtime
    /// would have used.</summary>
    internal readonly string Id;

    /// <summary>The ARRANGED box. Equal to the hit rect unless a hit-width cap
    /// narrowed the reservation, which is how a scrolling menu row keeps a
    /// full-width fill under a gutter-free hit.</summary>
    internal readonly Vector2 Min;

    /// <inheritdoc cref="Min"/>
    internal readonly Vector2 Max;

    /// <summary>The list the element's own box belongs on: the CURRENT
    /// window's, resolved by the walk where it paints.</summary>
    internal readonly ImDrawListPtr DrawList;

    internal Vector2 Size => Max - Min;
}

/// <summary>
/// What a hook resolved for the subtree beneath it. Both are overrides on top
/// of what the sheet already resolved; a hook with no opinion states neither.
/// </summary>
internal readonly struct PaintResult
{
    internal PaintResult(Vector4? foreground, float? opacity)
    {
        Foreground = foreground;
        Opacity = opacity;
    }

    internal readonly Vector4? Foreground;

    /// <summary>COMPOSES onto the inherited glyph opacity (inherited *=
    /// stated), so a box that dims its glyphs dims whatever its ancestors
    /// already dimmed.</summary>
    internal readonly float? Opacity;
}

/// <summary>
/// The escape hatch, and ONLY for geometry a sheet cannot express: a slider's
/// track and thumb, a switch's knob, a bar's fill, the colour popover, a
/// disclosure chevron, an inset hairline. A hook never decides state, layout,
/// identity, help or dispatch — those are the base's, once — and it may not
/// paint outside the box it was handed.
///
/// <para>An element that names a hook gives up its BASE box paint: the hook
/// owns the box, which is why a hook and a sheet fill are never both drawn.
/// </para>
/// </summary>
internal interface IPainter
{
    /// <summary>Whether the hook reads pointer state. A decorative seam — a
    /// bar, a rule, a hairline — states false and its element reserves
    /// nothing, which is what keeps the controls under painted chrome
    /// reachable.</summary>
    bool NeedsHit => true;

    /// <summary>Whether the seam draws the element's own run. A hook that
    /// cannot be split from its text — the section header's title, chevron and
    /// hover swap are one legacy seam shared with the imperative page — states
    /// true, and the base leaves the run to it rather than drawing it twice.
    /// </summary>
    bool OwnsText => false;

    PaintResult Paint(in PaintContext context);
}

/// <summary>
/// The box a PORTAL wears. Painted over the popup window's own rect before the
/// detached subtree is walked, so a floating surface has the same split as an
/// element: the runtime owns placement, the painter owns pixels.
/// </summary>
internal interface IPortalSurfacePainter
{
    void Paint(ImDrawListPtr drawList, Vector2 min, Vector2 max);
}
