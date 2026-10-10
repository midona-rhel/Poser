using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.ActionBarWidgets;

namespace Poser.UI;

/// <summary>A frame slot in screen space.</summary>
public readonly record struct WindowFrameRect(Vector2 Min, Vector2 Max)
{
    public Vector2 Size => Max - Min;
}

/// <summary>
/// What <see cref="WindowFrameWidgets.WindowFrame"/> drew and what it left to the
/// caller: the two bars are already painted, the rail band and the body are
/// empty rectangles their owner fills.
/// </summary>
public readonly record struct WindowFrameRects
{
    /// <summary>The title band, rule included.</summary>
    public WindowFrameRect TitleBar { get; init; }

    /// <summary>The band under the title bar, its bottom rule INCLUDED. Empty
    /// when there is no band.</summary>
    public WindowFrameRect Band { get; init; }

    /// <summary>The rail's raised band, its 1px rule EXCLUDED — the rule is
    /// the body's left edge, not the rail's content. Empty when there is no
    /// rail.</summary>
    public WindowFrameRect Rail { get; init; }

    /// <summary>Everything between the two bars and right of the rail rule.
    /// </summary>
    public WindowFrameRect Body { get; init; }

    /// <summary>The full-width band between the columns region and the
    /// footer, its top rule INCLUDED — the mirror of <see cref="Band"/>.
    /// Empty when there is no bottom band.</summary>
    public WindowFrameRect BottomBand { get; init; }

    /// <summary>The footer band, rule included. Empty when there is no footer.
    /// </summary>
    public WindowFrameRect Footer { get; init; }
}

/// <summary>
/// The frame's slots. A rail is its WIDTH (rule included; 0 is no rail and no
/// rule) and a footer is its ACTIONS: both slots vanish when unstated, and the
/// body takes the space back.
/// </summary>
public readonly record struct WindowFrameProps
{
    public string Title { get; init; }

    /// <summary>Unstated draws no close affordance.</summary>
    public Action? OnClose { get; init; }

    public string? CloseHelp { get; init; }

    /// <summary>Extra title-bar icons, seated in the right cluster BEFORE
    /// the close affordance (a pin, a mode toggle). Unstated adds nothing.
    /// </summary>
    public Action<ActionBarScope>? HeaderRight { get; init; }

    /// <summary>Custom title-bar content instead of the <see cref="Title"/>
    /// label — a search field, a breadcrumb. Told the WHOLE title-bar rect;
    /// the right icon cluster still draws, so the content must size itself
    /// to leave that cluster room. The label is skipped while stated.
    /// </summary>
    public Action<WindowFrameRect>? TitleContent { get; init; }

    /// <summary>Logical rail width, the 1px rule INCLUDED; 0 is no rail.
    /// </summary>
    public float RailWidth { get; init; }

    /// <summary>Logical height of a band between the title bar and the body —
    /// the file surface's navigation row; 0 is no band. The frame reserves it
    /// and rules its bottom edge full width; the caller fills the rect.
    /// </summary>
    public float BandHeight { get; init; }

    /// <summary>Logical height of a band between the columns region and the
    /// footer — the file surface's option band; 0 is no band. The mirror of
    /// <see cref="BandHeight"/>: the frame reserves it FULL WIDTH — the rail
    /// and the body both stop above it — and rules its top edge; the caller
    /// fills the rect.</summary>
    public float BottomBandHeight { get; init; }

    /// <summary>Logical width excluded from the bottom-band separator.</summary>
    public float BottomBandRightInset { get; init; }

    /// <summary>The host window already painted the glass — which
    /// <see cref="FloatingSurface.Window"/> does for every
    /// window it hosts — so the frame must not paint a second shadow over the
    /// first. A surface on a bare host leaves this unstated.</summary>
    public bool HostPaintsChrome { get; init; }

    /// <summary>The footer's left cluster. Stating either cluster is what
    /// makes the footer band exist.</summary>
    public Action<ActionBarScope>? FooterLeft { get; init; }

    /// <summary>The footer's right cluster.</summary>
    public Action<ActionBarScope>? FooterRight { get; init; }
}
