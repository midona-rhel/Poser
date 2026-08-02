using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// A CONTROLLED disclosure section. The margin, the rule and the padding are
/// three boxes in the column rather than three cursor advances, and the header
/// is one element whose <c>Selected</c> IS its expanded flag — the same flag
/// the base negates when it reports the toggle.
/// </summary>
public readonly record struct Section
{
    public required string Title { get; init; }

    public bool Expanded { get; init; }

    public UiHandler<bool> OnExpandedChange { get; init; }

    public UiChildren Children { get; init; }

    /// <summary>A section is a stateful, reorderable sibling: its identity may
    /// not come from its position.</summary>
    public required UiKey Key { get; init; }

    /// <summary>USER FEEDBACK 2026-08-02: the rule is a divider BETWEEN
    /// sections — the page's first section sets this and draws none.</summary>
    public bool NoDivider { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Section section) => (UiNode)section;

    public static implicit operator UiNode(Section section)
    {
        Theme.PageTokens page = Crystarium.ActiveTheme.Page;
        // No toggle handler means no disclosure AT ALL: the header paints
        // through the same seam with the default hit the imperative
        // non-collapsible section hands it — open forever, never hovering.
        bool collapsible = !section.OnExpandedChange.IsNone;
        bool expanded = !collapsible || section.Expanded;
        UiNode header = collapsible
            ? new Element
            {
                Sheet = SheetFamily.SectionHeader,
                Text = section.Title,
                Selected = section.Expanded,
                On = new Listeners { OnToggle = section.OnExpandedChange },
                Painter = SectionHeaderPainter.Instance,
            }
            : new Element
            {
                Sheet = SheetFamily.SectionHeader,
                Text = section.Title,
                Selected = true,
                Painter = StaticSectionHeaderPainter.Instance,
            };

        return new Column
        {
            Style = new() { Layout = new() { Width = UiDim.Fill } },
            Key = section.Key,
            Children =
            [
                // Without a divider there is nothing the margin separates
                // FROM: the first section keeps only the header's own offset,
                // so GENERAL sits as far under the page top as every other
                // header sits under its rule.
                section.NoDivider
                    ? UiNode.None
                    : Crystarium.Spacer(page.SectionMarginTop),
                section.NoDivider
                    ? UiNode.None
                    : new Element
                    {
                        Sheet = SheetFamily.SectionRule,
                        Painter = SectionRulePainter.Instance,
                    },
                Crystarium.Spacer(page.SectionPaddingTop),
                header,
                expanded
                    ? new Column
                    {
                        Style = new() { Layout = new() { Width = UiDim.Fill } },
                        Children = section.Children,
                    }
                    : UiNode.None,
            ],
        };
    }
}
