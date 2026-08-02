using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// Everything one painter is handed. A struct rather than a parameter list
/// because one of these is NOT the element's obvious geometry: the reserved
/// hit rect can be narrower than the arranged box, so a painter that wants the
/// box it was ARRANGED must be told it.
/// </summary>
internal readonly struct PaintInput
{
    internal PaintInput(
        in Poser.UI.InteractionResult hit,
        uint identity,
        byte arg,
        bool disabled,
        Vector2 boxSize,
        ImDrawListPtr drawList)
    {
        Hit = hit;
        Identity = identity;
        Arg = arg;
        Disabled = disabled;
        BoxSize = boxSize;
        DrawList = drawList;
    }

    internal readonly Poser.UI.InteractionResult Hit;

    /// <summary>The ImGui id of the reservation, for animation state keyed to
    /// the item rather than to the call site.</summary>
    internal readonly uint Identity;

    /// <summary>The single byte of parameter the declaration stored — a
    /// variant, a tone, a selected flag. Only the painter interprets it.</summary>
    internal readonly byte Arg;

    internal readonly bool Disabled;

    /// <summary>The ARRANGED box. Equal to the hit rect's size unless a
    /// hit-width cap narrowed the reservation, which is how a scrolling menu
    /// row keeps a full-width fill under a gutter-free hit.</summary>
    internal readonly Vector2 BoxSize;

    /// <summary>The list the element's own box belongs on: the CURRENT
    /// window's, resolved by the walk where it paints. A popup is a window and
    /// a scroll region is a child window, each with its own list, so a menu
    /// row's fill lands on the same list as the label inside it.</summary>
    internal readonly ImDrawListPtr DrawList;
}

/// <summary>
/// What a painter resolved for its subtree: currentColor, and the glyph
/// opacity every Svg below the element folds into its own.
/// </summary>
internal readonly struct PaintOutput
{
    internal PaintOutput(Vector4? foreground, float? svgOpacity)
    {
        Foreground = foreground;
        SvgOpacity = svgOpacity;
    }

    /// <summary>null leaves the subtree on whatever it inherited. A menu row
    /// states no color at all, so its label stays on the theme default rather
    /// than on a value the row would have to restate.</summary>
    internal readonly Vector4? Foreground;

    /// <summary>null leaves the inherited glyph opacity untouched; a stated
    /// value COMPOSES onto it (inherited *= stated), so a box that dims its
    /// glyphs dims whatever its ancestors already dimmed rather than
    /// overwriting it. A painter that has no opinion states nothing — 1f is
    /// an opinion that happens to be identity, and only reads as one because
    /// the composition is multiplicative.</summary>
    internal readonly float? SvgOpacity;
}

/// <summary>
/// The paint half of an interactive element, kept OUT of the walk: the root
/// resolves identity, reserves the rect and hands both to a retained painter
/// singleton, so no element kind is special-cased in the runtime. The return
/// value is what the subtree INHERITS — the painter decides what its content
/// should be tinted with, and every Text and Svg below the element takes that
/// unless it names its own.
/// </summary>
internal interface IInteractivePainter
{
    PaintOutput Paint(in PaintInput input);
}

/// <summary>
/// The box a PORTAL wears. Painted over the popup window's own rect before the
/// detached subtree is walked, so a floating surface has the same one-painter
/// split as an interactive element: the runtime owns placement, the painter
/// owns pixels.
/// </summary>
internal interface IPortalSurfacePainter
{
    void Paint(ImDrawListPtr drawList, Vector2 min, Vector2 max);
}
