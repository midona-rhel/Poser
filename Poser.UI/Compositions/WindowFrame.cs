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
/// Rectangles produced by <see cref="WindowFrameWidgets.WindowFrame"/>. The
/// frame paints its bars; the caller fills the rail, body, and optional bands.
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
/// Frame content and optional regions. A zero rail width removes the rail and
/// its rule; omitting footer actions removes the footer.
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

    /// <summary>Custom title-bar content in place of <see cref="Title"/>.
    /// Receives the full title-bar rectangle and must leave room for the
    /// right-side icon cluster.
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

    /// <summary>Logical height of a full-width band above the footer; zero
    /// removes it. The rail and body stop above the band.</summary>
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
