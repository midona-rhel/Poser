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
        UiNode rule = bar.Separator == ActionBarSeparator.None
            ? UiNode.None
            : new Element { Sheet = SheetFamily.BarRule };
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
        UiNode box = new Column
        {
            Sheet = SheetFamily.ActionBarBox,
            Key = bar.FooterChrome ? default : bar.Key,
            Children = bar.Separator == ActionBarSeparator.Top
                ? [rule, content]
                : [content, rule],
        };
        return bar.FooterChrome
            ? new Element
            {
                Painter = Reactive.ModalFooterPainter.Instance,
                Key = bar.Key,
                Children = box,
            }
            : box;
    }
}
