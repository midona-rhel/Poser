namespace Poser.UI;

/// <summary>
/// The window action bar: a bar-height band padded to the header inset, its
/// items centred on the content row and its 1px rule flowed on whichever edge
/// the separator names. Left and right are ordinary children — a title is a
/// <see cref="Title"/> label, a close affordance an <see cref="IconAction"/>,
/// a footer action a <see cref="Button"/>.
/// </summary>
public readonly record struct ActionBar
{
    public UiChildren Left { get; init; }

    public UiChildren Right { get; init; }

    public ActionBarSeparator Separator { get; init; }

    /// <summary>The modal footer's band: the ModalFooter fill rounded to the
    /// window's bottom corners under the bar. Chrome is the BAR's role, so a
    /// footer states it here rather than importing a painter.</summary>
    public bool FooterChrome { get; init; }

    public UiKey Key { get; init; }

    /// <summary>The bar's own text role: the label tone at the bar's optical
    /// rise.</summary>
    public static UiNode Title(string text) =>
        new Label { Text = text, Sheet = SheetFamily.ActionBarTitle };

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(ActionBar bar) => (UiNode)bar;

    public static implicit operator UiNode(ActionBar bar)
    {
        UiNode content = new Row
        {
            Sheet = SheetFamily.ActionBarRow,
            Children =
            [
                new Row { Sheet = SheetFamily.ActionGroup, Children = bar.Left },
                // The spring between the clusters, so right-aligned actions
                // derive from the same flow that placed the left ones.
                new Element { Style = Element.Sized(UiDim.Fill, null) },
                new Row { Sheet = SheetFamily.ActionGroup, Children = bar.Right },
            ],
        };
        // The rule is the BOX's painter, not a flowed child: it spans the
        // bar's full border box — the window edges, past the header inset —
        // and steals no height, so items centre on the whole bar (USER
        // 2026-08-02: rules reach the edges; footer buttons sat 1px low).
        UiNode box = new Element
        {
            Sheet = SheetFamily.ActionBarBox,
            Painter = bar.Separator switch
            {
                ActionBarSeparator.Top => Reactive.BarSeparatorPainter.Top,
                ActionBarSeparator.Bottom => Reactive.BarSeparatorPainter.Bottom,
                _ => null,
            },
            Key = bar.FooterChrome ? default : bar.Key,
            Children = content,
        };
        return bar.FooterChrome
            ? new Element
            {
                // The wrapper STATES the bar height: a sibling Fill resolves
                // against declared extents, and an unsized wrapper would let
                // it swallow the footer's 44 and push the bar off the window
                // (user-caught: the rail rule ran through the footer band).
                Style = Element.Sized(
                    UiDim.Fill,
                    UiDim.Fixed(Crystarium.ActiveTheme.Floating.ModalBarHeight)),
                Painter = Reactive.ModalFooterPainter.Instance,
                Key = bar.Key,
                Children = box,
            }
            : box;
    }
}
