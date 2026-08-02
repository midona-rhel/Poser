using System;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The segmented control. Geometry comes from the ONE layout resolution the
/// imperative control uses — per-tab natural widths stretched into the stated
/// width — and the selected fill pair from the same paint seam, so the two
/// paths are one control by construction. Tones are the tab sheet's states.
///
/// <para>Reselect reports nothing, exactly as the imperative control only
/// fires on a change: the selected tab is the one tab with no handler.</para>
/// </summary>
public readonly record struct Segmented
{
    public required string[] Items { get; init; }

    public int Selected { get; init; }

    public UiHandler<int> OnChange { get; init; }

    /// <summary>The LOGICAL width the pill fills. The imperative control
    /// resolved Fill from the ambient region; a declared tree states the cell
    /// width it was granted, because tab widths must exist before the solver
    /// runs.</summary>
    public required float Width { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Segmented segmented) =>
        (UiNode)segmented;

    public static implicit operator UiNode(Segmented segmented) =>
        segmented.Emit();

    private UiNode Emit()
    {
        string[] items = Items;
        if (items.Length == 0)
            return UiNode.None;

        float scale = ImGuiHelpers.GlobalScale;
        LegacyCrystarium.SegmentLayout layout =
            LegacyCrystarium.LabelSegmentLayout(
                items, new ControlStyle { Width = UiWidth.Fixed(Width) });

        Span<UiNode> tabs = FrameArena.Require().ScratchNodes(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            bool isSelected = i == Selected;
            tabs[i] = new Element
            {
                Sheet = SheetFamily.SegmentTab,
                Style = Element.Sized(
                    UiDim.Fixed(layout.Widths[i] / scale), null),
                Text = items[i],
                Selected = isSelected,
                Index = i,
                Painter = SegmentTabPainter.Instance,
                On = new Listeners
                {
                    OnPick = isSelected ? default : OnChange,
                },
                Key = i,
            };
        }

        return new Row
        {
            Sheet = SheetFamily.SegmentPill,
            Style = Element.Sized(UiDim.Fixed(Width), null),
            Key = Key,
            Children = UiChildren.Create(tabs),
        };
    }
}
