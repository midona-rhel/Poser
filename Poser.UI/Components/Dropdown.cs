using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// Picto's <c>CmSelect</c>: an interactive trigger carrying the closed box's
/// seam, with the label, the chevron and the whole open menu as ordinary
/// composed elements. The pixels come from the same measurement and paint
/// seams the imperative control uses, so the two paths are one dropdown by
/// construction.
///
/// <para>Reselect semantics follow the imperative control: clicking the row
/// that is already selected closes the menu and reports nothing, which is why
/// that row is the one row with no handler wired.</para>
/// </summary>
public readonly record struct Dropdown
{
    public required string[] Items { get; init; }

    public int Selected { get; init; }

    public UiHandler<int> OnChange { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public ElementSheet? StyleSheet { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Dropdown dropdown) => (UiNode)dropdown;

    public static implicit operator UiNode(Dropdown dropdown) => dropdown.Emit();

    private UiNode Emit()
    {
        ArgumentNullException.ThrowIfNull(Items);
        if (Items.Length == 0)
            return UiNode.None;

        float scale = ImGuiHelpers.GlobalScale;
        Theme theme = Crystarium.ActiveTheme;
        // Content sizing is INTRINSIC here — the widest option, never the
        // surrounding region.
        LegacyCrystarium.DropdownMetrics metrics =
            LegacyCrystarium.MeasureDropdown(Items, null, default);
        LegacyCrystarium.DropdownPopupMetrics popup =
            LegacyCrystarium.MeasureDropdownPopup(Items.Length, metrics.LogicalHeight);

        UiDim authored = StyleSheet?.Layout?.Width ?? default;
        float triggerWidth = authored.Kind switch
        {
            UiDimKind.Fixed => authored.Value,
            // Fill is the solver's business, and so is everything measured off
            // it: the menu takes its own width from the anchor after the fact.
            UiDimKind.Fill => 0f,
            _ => metrics.Width / scale,
        };
        float labelSize = theme.Typography.LabelSize;
        // A label fills its row in both places it appears and is explicitly cut
        // to it, offering the full text on hover — CmSelect's own
        // `text-overflow: ellipsis`, stated rather than inferred from the Fill.
        ElementSheet labelBox = new()
        {
            Layout = new() { Width = UiDim.Fill },
            Type = new() { FontSize = labelSize, Overflow = TextOverflow.Clip },
        };

        // ---- .drop ---------------------------------------------------------
        ElementSheet rowBox = new()
        {
            Layout = new() { Height = UiDim.Fixed(popup.RowHeight / scale) },
            Type = new() { FontSize = labelSize, Overflow = TextOverflow.Clip },
        };
        // Frame-scoped scratch, at EVERY item count: the arena's buffer is
        // already there, and a stackalloc/heap split would allocate the moment
        // a menu got long.
        Span<UiNode> rows = FrameArena.Require().ScratchNodes(Items.Length);
        for (int i = 0; i < Items.Length; i++)
        {
            bool isSelected = i == Selected;
            rows[i] = new Element
            {
                Sheet = SheetFamily.DropdownRow,
                Style = rowBox,
                Text = Items[i],
                Preview = true,
                Selected = isSelected,
                Index = i,
                // The selected row reports nothing and still closes: the close
                // is the ELEMENT's, so the missing handler costs it nothing.
                On = new Listeners { OnPick = isSelected ? default : OnChange },
                // A menu row answers the press, exactly as the imperative menu does.
                ActivateOn = Activation.Press,
                ClosesPortal = true,
                Key = i,
            };
        }

        UiNode portal = Crystarium.Portal(
            new Column
            {
                Style = new() { Layout = new() { Gap = popup.RowGap / scale } },
                Children = UiChildren.Create(rows),
            },
            contentSize: new Vector2(triggerWidth, popup.PopupHeight / scale),
            padding: popup.DropInset / scale,
            anchorCompensation: popup.AnchorGapCompensation / scale,
            scrollRegionHeight: popup.ItemListHeight / scale,
            capChildHitWidth: Items.Length > popup.VisibleItems,
            surface: DropdownSurfacePainter.Instance,
            // The panel is the painter's, and every row scrolls: a menu has no
            // chrome above its list. Both stated rather than defaulted, so the
            // picker's opposite choices read as choices.
            treatment: FloatingSurfaceTreatment.Unframed,
            scrollFromChild: 0);

        // ---- .btn ----------------------------------------------------------
        string current = Selected >= 0 && Selected < Items.Length
            ? Items[Selected]
            : string.Empty;
        // CSS content box: the 1px border sits INSIDE the border box, so
        // padding measures from the border's inner edge.
        UiNode chrome = new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Gap = metrics.Gap / scale,
                    Padding = new EdgeInsets(
                        (metrics.BorderPx + metrics.PadLeft) / scale, 0f,
                        (metrics.BorderPx + metrics.PadRight) / scale, 0f),
                    Align = UiAlign.Center,
                },
            },
            Children =
            [
                new Label { Text = current, Style = labelBox, Preview = true },
                // .btnChevron: the 14px glyph centred in its fixed 20px slot.
                // The 0.5 opacity is the BOX's, so it arrives as the subtree's
                // inherited glyph opacity rather than as a number stated twice.
                new Stack
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Justify = UiAlign.Center,
                            Align = UiAlign.Center,
                            Width = UiDim.Fixed(metrics.ChevronSlot / scale),
                        },
                    },
                    Children = new Glyph
                    {
                        Name = LegacyCrystarium.ChevronIcon,
                        Size = theme.Controls.SmallIconSize,
                        NoInherit = true,
                    },
                },
            ],
        };

        // The trigger truncates its label through the text constraint and draws
        // nothing outside its own box, so it needs no clip rect.
        UiNode trigger = new Element
        {
            Sheet = SheetFamily.DropdownTrigger,
            // Fill stays the solver's; Content becomes the widest option, which
            // no sheet can know.
            Style = (StyleSheet ?? default) with
            {
                Layout = (StyleSheet?.Layout ?? default) with
                {
                    Width = authored.Kind == UiDimKind.Fill
                        ? UiDim.Fill
                        : UiDim.Fixed(triggerWidth),
                    Height = UiDim.Fixed(metrics.LogicalHeight),
                },
            },
            Painter = DropdownTriggerPainter.Instance,
            Disabled = Disabled,
            Help = Help,
            Key = Key,
            Children = [chrome, portal],
            ActivateOn = Activation.Press,
            OpensPortalNode = portal.Index,
        };
        Crystarium.AnchorPortal(portal, trigger);
        return trigger;
    }
}
