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
/// <para>Two variants, exactly as the imperative control has two: LABELS in a
/// stated width, or ICONS on their own square tabs, where the natural width IS
/// the control's width. Nothing else differs — the pill, the tab sheet, the
/// selection paint and the reporting are shared.</para>
///
/// <para>Reselect reports nothing, exactly as the imperative control only
/// fires on a change: the selected tab is the one tab with no handler.</para>
/// </summary>
public readonly record struct Segmented
{
    /// <summary>The label variant's captions. Null when <see cref="Icons"/>
    /// names the tabs instead.</summary>
    public string[]? Items { get; init; }

    /// <summary>The icon variant's glyphs: each tab is a square of the
    /// comfortable height and the control measures itself.</summary>
    public TablerIcon[]? Icons { get; init; }

    public int Selected { get; init; }

    public UiHandler<int> OnChange { get; init; }

    /// <summary>Per-tab refusal, evaluated at BUILD: a declared tree states
    /// what it knows now, so the caller hoists whatever the predicate reads.
    /// </summary>
    public Func<int, bool>? ItemDisabled { get; init; }

    /// <inheritdoc cref="ItemDisabled"/>
    public Func<int, string?>? ItemHelp { get; init; }

    /// <summary>The LOGICAL width the pill fills. The imperative control
    /// resolved Fill from the ambient region; a declared tree states the cell
    /// width it was granted, because tab widths must exist before the solver
    /// runs. REQUIRED by the label variant; the icon variant measures itself
    /// and ignores a zero.</summary>
    public float Width { get; init; }

    /// <summary>Lands the FIRST TAB's left edge on the caller's origin rather
    /// than the pill's: the trough's chrome padding becomes a negative left
    /// margin, which the solver honours as a shift because a margin is added
    /// to the cursor and never clamped.</summary>
    public bool AlignFirstTabToCursor { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Segmented segmented) =>
        (UiNode)segmented;

    public static implicit operator UiNode(Segmented segmented) =>
        segmented.Emit();

    private UiNode Emit()
    {
        string[]? items = Items;
        TablerIcon[]? icons = Icons;
        int count = icons?.Length ?? items?.Length ?? 0;
        if (count == 0)
            return UiNode.None;

        float scale = ImGuiHelpers.GlobalScale;
        ControlStyle style = Width > 0f
            ? new ControlStyle { Width = UiWidth.Fixed(Width) }
            : default;
        LegacyCrystarium.SegmentLayout layout = icons is not null
            ? LegacyCrystarium.IconSegmentLayout(count, style)
            : LegacyCrystarium.LabelSegmentLayout(items!, style);
        float glyphSize = Crystarium.ActiveTheme.Controls.SmallIconSize;

        Span<UiNode> tabs = FrameArena.Require().ScratchNodes(count);
        for (int i = 0; i < count; i++)
        {
            bool isSelected = i == Selected;
            bool disabled = ItemDisabled?.Invoke(i) == true;
            // A tab's mark is a REAL child on the icon variant and a run on the
            // label one; the empty range is stated, because a conditional whose
            // other arm is a Glyph would smuggle an empty glyph child in.
            UiChildren mark = default;
            if (icons is not null)
                mark = new Glyph { Icon = icons[i], Size = glyphSize };
            tabs[i] = new Element
            {
                Sheet = SheetFamily.SegmentTab,
                Style = Element.Sized(
                    UiDim.Fixed(layout.Widths[i] / scale), null),
                Text = icons is null ? items![i] : null,
                Children = mark,
                Selected = isSelected,
                Disabled = disabled,
                Help = ItemHelp?.Invoke(i),
                Index = i,
                Painter = SegmentTabPainter.Instance,
                On = new Listeners
                {
                    OnPick = isSelected || disabled ? default : OnChange,
                },
                Key = i,
            };
        }

        return new Row
        {
            Sheet = SheetFamily.SegmentPill,
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(
                        Width > 0f ? Width : layout.TotalWidth / scale),
                    Margin = AlignFirstTabToCursor
                        ? new EdgeInsets(-layout.Padding / scale, 0f, 0f, 0f)
                        : null,
                },
            },
            Key = Key,
            Children = UiChildren.Create(tabs),
        };
    }
}
