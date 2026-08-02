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
        UiNode header = new Element
        {
            Sheet = SheetFamily.SectionHeader,
            Text = section.Title,
            Selected = section.Expanded,
            On = new Listeners { OnToggle = section.OnExpandedChange },
            Painter = SectionHeaderPainter.Instance,
        };

        return new Column
        {
            Style = new() { Layout = new() { Width = UiDim.Fill } },
            Key = section.Key,
            Children =
            [
                Crystarium.Spacer(page.SectionMarginTop),
                section.NoDivider
                    ? UiNode.None
                    : new Element
                    {
                        Sheet = SheetFamily.SectionRule,
                        Painter = SectionRulePainter.Instance,
                    },
                Crystarium.Spacer(page.SectionPaddingTop),
                header,
                section.Expanded
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
