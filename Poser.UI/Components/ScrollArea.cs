using System;

namespace Poser.UI;

/// <summary>
/// An IN-WINDOW scrolling viewport: a vertical flow whose own box is the
/// viewport and whose children run past it. Everything else about the subtree
/// is ordinary — one solve, absolute logical boxes, the same identity chain —
/// so a row inside a scroll area is declared exactly as it would be outside
/// one, and its identity does not move when the region scrolls.
///
/// <para>THE GUTTER IS THE PADDING: the reserved bar space is a right INSET of
/// the viewport, not a column taken out of the layout, so children are arranged
/// at the full content width and the bar overlays them.
/// <see cref="CapChildHitWidth"/> is the opt-in that keeps the first
/// interactive layer's HIT rect clear of the bar while its fill still paints to
/// the edge.</para>
///
/// <para>Nesting is unsupported and throws: two viewports would each re-anchor
/// the same walk.</para>
/// </summary>
public readonly record struct ScrollArea
{
    /// <summary>The marker a Fill viewport carries until the solver has a
    /// share to grant it. Negative so it can never be mistaken for a resolved
    /// height; see <see cref="Reactive.ElementRecord.ScrollViewport"/>.
    /// </summary>
    private const float PendingViewport = -1f;

    public UiChildren Children { get; init; }

    /// <summary>
    /// The VIEWPORT, and the element's own height. Fixed states it outright;
    /// Fill takes whatever the parent's distribution grants, which is the only
    /// way a list can end exactly where a footer band begins. Content is
    /// meaningless here — a viewport sized by its content never scrolls — and
    /// is rejected.
    /// </summary>
    public required UiDim Height { get; init; }

    /// <inheritdoc cref="LegacyCrystarium.ScrollRegion"/>
    public float? GutterWidth { get; init; }

    /// <summary>Reserve the first interactive layer clear of the scrollbar
    /// gutter while their boxes keep the full width.</summary>
    public bool CapChildHitWidth { get; init; }

    public ElementSheet? Style { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(ScrollArea area) => (UiNode)area;

    public static implicit operator UiNode(ScrollArea area) => Emit(area);

    private static UiNode Emit(in ScrollArea area)
    {
        UiDim height = area.Height;
        float viewport = height.Kind switch
        {
            UiDimKind.Fixed when height.Value > 0f => height.Value,
            UiDimKind.Fill => PendingViewport,
            _ => throw new InvalidOperationException(
                "ScrollArea.Height must be UiDim.Fixed(> 0) or UiDim.Fill: a "
                + "viewport sized by its own content can never overflow, so "
                + "nothing would scroll."),
        };

        return new Element
        {
            Sheet = SheetFamily.Column,
            // The viewport is the element's OWN height, so a Fill viewport
            // resolves through the ordinary Fill distribution rather than
            // through a second sizing rule nobody else obeys.
            Style = (area.Style ?? default) with
            {
                Layout = (area.Style?.Layout ?? default) with { Height = height },
            },
            Children = area.Children,
            Key = area.Key,
            ScrollViewport = viewport,
            ScrollGutter = area.GutterWidth ?? 0f,
            ScrollCapsHitWidth = area.CapChildHitWidth,
        };
    }
}
