using System;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The branch a row's OWN depth column draws. Derived, never stated: it is a
/// function of the depth, of whether the row discloses children, and of whether
/// it is its parent's last — which is exactly the trio the sidebar's view model
/// has always carried.
/// </summary>
internal enum TreeBranch : byte
{
    /// <summary>A root row: no column, no branch.</summary>
    None,

    /// <summary>A leaf with siblings below it: the trunk runs the full band and
    /// the arm leaves it at the midline.</summary>
    Tee,

    /// <summary>The last leaf: the trunk stops at the arm, edge-joined.</summary>
    Elbow,

    /// <summary>A disclosing row with siblings below it: the trunk is cut around
    /// the chevron and resumes underneath.</summary>
    Fork,

    /// <summary>The last disclosing row: the trunk stops above the chevron and
    /// never resumes.</summary>
    ForkLast,
}

/// <summary>
/// Everything the guide painter needs about one row's column, as typed data.
///
/// <para>THE ENCODING: <see cref="Trunks"/> is a bitmask over ANCESTOR depths —
/// bit <c>a</c> set means a sibling line continues at depth <c>a</c>, which is
/// the view model's <c>TreeLines[a]</c> exactly — and the row's own column is
/// the derived <see cref="Branch"/>. A mask rather than an array because the
/// record is a value in a pooled arena: an array would put a per-row reference
/// (and, for a caller that rebuilds it, a per-frame allocation) on the warm
/// path. Bit 0 is unused, exactly as depth 0 has no trunk, and 32 levels is
/// more than any skeleton the sidebar can express.</para>
/// </summary>
internal readonly record struct TreeGuideSpec(
    uint Trunks, byte Depth, TreeBranch Branch);

/// <summary>
/// The sidebar's tree row: the 26px band the shell hand-drew — pill, guides,
/// disclosure, icon, label, badge and a right-anchored action strip — as ONE
/// composed element.
///
/// <para>The row and its disclosure are TWO REAL RESERVES, the house
/// overlapping-target pattern: the row is submitted first and yields
/// arbitration, so a press landing on the chevron takes the active id away from
/// it and the two outcomes — expand, select — are mutually exclusive by
/// construction rather than by a mouse-x comparison.</para>
///
/// <para>Everything the guides need is <see cref="TreeGuideSpec"/>; everything
/// the pill needs is the depth inside it. Both ride the element as typed data,
/// so the painters carry no per-row state and every instance is a
/// singleton.</para>
/// </summary>
public readonly record struct TreeRow
{
    /// <summary>The indent one level costs.</summary>
    internal const float Indent = 20f;

    /// <summary>Where depth 1's trunk stands: the 16px expander slot plus half
    /// the root row's 16px icon. Every deeper trunk is this plus whole indents,
    /// which is what keeps a terminal branch on the same grid as the root
    /// icon above it.</summary>
    internal const float RootTrunk = 24f;

    /// <summary>A nested label's distance from its own trunk.</summary>
    internal const float LabelOffset = 14f;

    /// <summary>The root row's disclosure slot, left of the icon.</summary>
    internal const float RootSlot = 16f;

    /// <summary>The disclosure's box — the drawn triangle sits 8px inside its
    /// left edge, so the hit rect and the mark are one rectangle.</summary>
    internal const float ChevronBox = 18f;

    internal const float ChevronCenter = 8f;

    /// <summary>The pill clears the branch arm by this much, so connector ink
    /// never runs under a selection.</summary>
    internal const float PillClearance = 10f;

    /// <summary>A root pill's own inset — SidebarRow's <c>--row-inset</c>.
    /// </summary>
    internal const float RootPillInset = 1f;

    /// <summary>The mark is a 16px square with the row's own trailing gap, so a
    /// root label lands 38px in whether or not the row discloses.</summary>
    internal const float IconSide = 16f;

    internal const float IconGap = 6f;

    /// <summary>SidebarRow's <c>.icon</c> opacity. CONSTANT here: lifting it on
    /// hover would need the slot's own hover state, and a stateful slot
    /// reserves — which is the one thing a row's content may not do.</summary>
    internal const float IconOpacity = 0.85f;

    /// <summary>The strip's pitch: the switch-sized square the shell's row
    /// actions use, plus the gap that made their 22px advance.</summary>
    internal const float ActionGap = 2f;

    /// <summary>
    /// USER CORRECTION (2026-08-03, seventh sighting of the same defect): the
    /// row label reads one pixel LOW at the sidebar's optical rise, so the tree
    /// row lifts it one further. The rise is the deviation from the legacy
    /// DrawRow placement and is stated HERE rather than on the navigation
    /// sheet: the settings rail is a different band and was not the complaint.
    ///
    /// <para>The lift is safe because the run is cut by
    /// <see cref="TextOverflow.Truncate"/> — the overflow-only constraint — and
    /// never by the always-shave Clip path, so no ascender or descender is
    /// touched by the cut.</para>
    /// </summary>
    internal const float LabelLift = 1f;

    public required string Label { get; init; }

    /// <summary>The row's mark. A texture WINS over both glyph forms — a
    /// resolved portrait is the concrete thing the row is about — and the
    /// glyph beside it is the fallback for the rows whose image never
    /// resolved.</summary>
    public TablerIcon? Icon { get; init; }

    /// <summary>The registry NAME form, for the glyphs the enum does not carry.
    /// </summary>
    public string? IconName { get; init; }

    /// <inheritdoc cref="Element.Texture"/>
    public nint Texture { get; init; }

    /// <summary>Right-aligned mono readout (counts, "you", "spawned").</summary>
    public string? Badge { get; init; }

    /// <summary>0 is a root row; each level costs one indent.</summary>
    public int Depth { get; init; }

    /// <inheritdoc cref="TreeGuideSpec.Trunks"/>
    public uint Trunks { get; init; }

    /// <summary>Last child of its parent — the branch is an L, not a T.
    /// </summary>
    public bool IsLastChild { get; init; }

    /// <summary>The disclosure. <see cref="SidebarExpander.None"/> reserves no
    /// chevron at all, so the row's whole width selects.</summary>
    public SidebarExpander Expander { get; init; }

    /// <summary>The affordance is shown but faded and inert — the row's
    /// children are temporarily unavailable. It is never ERASED once a row can
    /// disclose, so the column does not reflow when a skeleton resolves.
    /// </summary>
    public bool ExpanderDisabled { get; init; }

    public bool Selected { get; init; }

    public UiHandler OnSelect { get; init; }

    public UiHandler OnToggleExpand { get; init; }

    /// <inheritdoc cref="Listeners.OnContext"/>
    public UiHandler OnContext { get; init; }

    /// <summary>The right-anchored strip, which is the caller's own icon
    /// actions. The label's Fill is the spring that pushes it there.</summary>
    public UiChildren Actions { get; init; }

    /// <summary>REQUIRED: a tree reorders under disclosure and filtering, so a
    /// row keyed by position would hand its neighbour's interaction identity —
    /// and its hover — to whatever slid into its place.</summary>
    public required UiKey Key { get; init; }

    /// <summary>Suppress the mark WITHOUT moving the label: a nested row draws
    /// no icon, and its guide column already spans the same distance.</summary>
    public bool HideIcon { get; init; }

    public string? Help { get; init; }

    /// <summary>A single child needs no collection: user-defined conversions do
    /// not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(TreeRow row) => (UiNode)row;

    public static implicit operator UiNode(TreeRow row) => row.Emit();

    /// <summary>Where one depth's trunk stands, measured from the row's left
    /// edge. The ONE definition: the layout offsets its slots by it and the
    /// painter draws its ink on it.</summary>
    internal static float TrunkX(int depth) =>
        RootTrunk + (depth - 1) * Indent;

    private UiNode Emit()
    {
        int depth = Math.Max(0, Depth);
        bool discloses = Expander != SidebarExpander.None;
        TreeGuideSpec guides = new(
            Trunks,
            (byte)Math.Min(depth, byte.MaxValue),
            depth == 0
                ? TreeBranch.None
                : discloses
                    ? IsLastChild ? TreeBranch.ForkLast : TreeBranch.Fork
                    : IsLastChild ? TreeBranch.Elbow : TreeBranch.Tee);

        // The zone spans everything left of the label: the root's expander slot,
        // or a nested row's trunk plus its label offset. It is the row's FIRST
        // child at its left edge, which is what lets the painter measure every
        // guide column from its own box.
        float zoneWidth = depth == 0
            ? RootSlot
            : TrunkX(depth) + LabelOffset;
        float chevronLeft = depth == 0
            ? 0f
            : TrunkX(depth) - ChevronCenter;

        UiNode chevron = !discloses ? UiNode.None : new Element
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(ChevronBox),
                    Height = UiDim.Fill,
                },
            },
            // Selected IS the open state, exactly as it is for the section
            // header's disclosure.
            Selected = Expander == SidebarExpander.Open,
            Disabled = ExpanderDisabled,
            On = new Listeners
            {
                OnClick = ExpanderDisabled ? default : OnToggleExpand,
            },
            // An inert affordance reserves NOTHING: it must not take the hover
            // its row would otherwise keep.
            Painter = ExpanderDisabled
                ? DisclosureChevronPainter.Inert
                : DisclosureChevronPainter.Live,
            Key = "expander",
        };

        UiNode zone = new Element
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(zoneWidth),
                    Height = UiDim.Fill,
                    Padding = new EdgeInsets(chevronLeft, 0f, 0f, 0f),
                },
            },
            Guides = guides,
            Painter = TreeGuidePainter.Instance,
            Children = chevron,
            Key = "guides",
        };

        bool hasMark = Texture != 0 || Icon is not null || IconName is not null;
        UiNode icon = HideIcon || !hasMark ? UiNode.None : new Element
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(IconSide),
                    Height = UiDim.Fixed(IconSide),
                    Margin = new EdgeInsets(0f, 0f, IconGap, 0f),
                },
                Colors = new() { Opacity = IconOpacity },
            },
            Texture = Texture,
            TextureSize = IconSide,
            Glyph = Texture == 0 ? Icon : null,
            GlyphName = Texture == 0 ? IconName : null,
            GlyphSize = IconSide,
        };

        UiNode badge = string.IsNullOrEmpty(Badge) ? UiNode.None : new Label
        {
            Text = Badge!,
            Sheet = SheetFamily.Readout,
            Style = new()
            {
                Layout = new()
                {
                    Margin = new EdgeInsets(
                        0f, 0f, Crystarium.ActiveTheme.Spacing.Two, 0f),
                },
                // The badge shares the LABEL's optical line (user 2026-08-03:
                // the count sat a pixel low too) — the same corrected rise,
                // stated once here beside the label's.
                Type = new()
                {
                    InkRise = Crystarium.ActiveTheme.Optical.SidebarText - 1f,
                },
            },
        };

        UiNode actions = Actions.Count == 0 ? UiNode.None : new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Gap = ActionGap,
                    Margin = new EdgeInsets(0f, 0f, ActionGap, 0f),
                },
            },
            Children = Actions,
            Key = "actions",
        };

        return new Element
        {
            // SidebarRow's states as data — the pill's fills, its radius and
            // the label's optical rise are the navigation family's already.
            Sheet = SheetFamily.NavRow,
            Selected = Selected,
            On = new Listeners { OnClick = OnSelect, OnContext = OnContext },
            Painter = TreeRowPillPainter.Instance,
            Guides = guides,
            // Yielded for EVERY reserving child, not just the disclosure: the
            // action strip is the row's second overlapping target, and a strip
            // on a leaf row (the overlay eye) would otherwise be unreachable —
            // ImGui refuses hover to a later item while an earlier one owns it.
            AllowChildHits = (discloses && !ExpanderDisabled) || Actions.Count > 0,
            Help = Help,
            Key = Key,
            Children =
            [
                zone,
                icon,
                new Label
                {
                    Text = Label,
                    Sheet = SheetFamily.NavLabel,
                    Style = new()
                    {
                        Type = new()
                        {
                            InkRise = Crystarium.ActiveTheme.Optical.SidebarText
                                - LabelLift,
                            // Restated, not inherited: the lift is only safe
                            // over the overflow-only cut, so the two travel
                            // together.
                            Overflow = TextOverflow.Truncate,
                        },
                    },
                },
                badge,
                actions,
            ],
        };
    }
}
