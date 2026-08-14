using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum SidebarExpander { None, Collapsed, Open }

/// <summary>
/// What one gesture on a <see cref="Crystarium.TreeRow"/> resolved to. A
/// single value, not a flag per target: the row and its disclosure are two
/// overlapping reserves and ImGui lets only the item that OWNS the press
/// complete a release, so a double activation is unrepresentable.
/// </summary>
public enum TreeRowAction { None, Selected, Expander, Context }

/// <summary>
/// One sidebar/tree row's inputs. Everything the guides need is
/// <see cref="Depth"/>, <see cref="Trunks"/> and <see cref="IsLastChild"/>;
/// the branch shape is DERIVED from those plus the disclosure, never stated.
/// </summary>
public record struct TreeRowProps
{
    /// <summary>The row's mark. A texture WINS over both glyph forms — a
    /// resolved portrait is the concrete thing the row is about — and the glyph
    /// beside it is the fallback for rows whose image never resolved.</summary>
    public TablerIcon? Icon;

    /// <summary>The registry NAME form, for glyphs the enum does not carry.
    /// </summary>
    public string? IconName;

    /// <summary>Already-resolved game texture. The caller owns lifetime:
    /// Dalamud's shared textures must be re-resolved every frame.</summary>
    public IDalamudTextureWrap? IconTexture;

    /// <summary>Suppress the mark AND its advance: a nested row draws no icon,
    /// and its guide column already spans the same distance.</summary>
    public bool HideIcon;

    /// <summary>The label's type size, or null for the standing body size the
    /// scene tree reads at. Stated only by rows that are NOT the tree — a
    /// dense transient list at the pointer wants the caption size and a row
    /// box to match, and shrinking the shared token would take the whole
    /// sidebar with it.</summary>
    public float? LabelSize;

    /// <summary>Centre the label in its zone instead of seating it at the
    /// start. Stated only by rows that are NOT the tree — a transient pick
    /// list at the pointer centres its names (user 2026-08-15); a tree row
    /// centring under its guides would break the indent grid.</summary>
    public bool CenterLabel;

    /// <summary>Right-aligned mono readout (counts, "you", "spawned").</summary>
    public string? Badge;

    /// <summary>0 is a root row; each level costs one indent.</summary>
    public int Depth;

    /// <summary>Bitmask over ANCESTOR depths: bit <c>a</c> set means a sibling
    /// line continues at depth <c>a</c>. A mask rather than an array keeps the
    /// warm path free of a per-row allocation; bit 0 is unused because depth 0
    /// has no trunk.</summary>
    public uint Trunks;

    /// <summary>Last child of its parent — the branch is an L, not a T.
    /// </summary>
    public bool IsLastChild;

    /// <summary>Suppress the connector INK only. The trunk column, the pill's
    /// inset and the label zone are unchanged, so a row keeps its exact
    /// geometry with the guides off and nothing reflows. Stated as HIDE so the
    /// record's default draws them.</summary>
    public bool HideGuides;

    /// <summary><see cref="SidebarExpander.None"/> reserves no chevron at all,
    /// so the row's whole width selects.</summary>
    public SidebarExpander Expander;

    /// <summary>The affordance is shown but faded and inert. It is never ERASED
    /// once a row can disclose, so the column does not reflow when a skeleton
    /// resolves.</summary>
    public bool ExpanderDisabled;

    public bool Selected;

    /// <summary>Drag-hover: the accent fill over its own hairline.</summary>
    public bool DropTarget;

    /// <summary>Right padding for the row's CONTENT under a scroll gutter. The
    /// pill's right edge, the badge and the action strip all stop here.
    /// </summary>
    public float TrailingInset;

    /// <summary>How many square icon actions the CALLER will draw after the row
    /// returns. The row reserves their span so the label truncates against it
    /// and reports where the strip starts.</summary>
    public int ActionSlots;

    /// <summary>The action square's logical side; 0 takes the shell's switch
    /// size.</summary>
    public float ActionSide;
}

public static partial class Crystarium
{
    /// <summary>The indent one level costs.</summary>
    private const float TreeIndent = 20f;

    /// <summary>Where depth 1's trunk stands: the 16px expander slot plus half
    /// the root row's 16px icon. Every deeper trunk is this plus whole indents,
    /// which keeps a terminal branch on the same grid as the root icon above
    /// it.</summary>
    private const float TreeRootTrunk = 24f;

    /// <summary>A nested label's distance from its own trunk.</summary>
    private const float TreeLabelOffset = 14f;

    /// <summary>The root row's disclosure slot, left of the icon.</summary>
    private const float TreeRootSlot = 16f;

    /// <summary>The disclosure's box: the hit rect and the drawn mark are one
    /// rectangle, so the chevron can never be clickable where it is not
    /// visible.</summary>
    private const float TreeChevronBox = 18f;

    private const float TreeChevronCenter = 8f;

    /// <summary>The pill clears the branch arm by this much, so connector ink
    /// never runs under a selection.</summary>
    private const float TreePillClearance = 10f;

    /// <summary>A root pill's own inset (CSS <c>--row-inset</c>).</summary>
    private const float TreeRootPillInset = 1f;

    private const float TreeIconSide = 16f;

    private const float TreeIconGap = 6f;

    /// <summary>CONSTANT: lifting it on hover would need the slot's own hover
    /// state, and a stateful slot reserves — the one thing row content may not
    /// do.</summary>
    private const float TreeIconOpacity = 0.85f;

    /// <summary>The strip's gap, and the gap that ends it at the content edge.
    /// </summary>
    private const float TreeActionGap = 2f;

    /// <summary>A trunk's FREE ends — the ones meeting the NEIGHBOURING row
    /// rather than this row's own arm — drop two PHYSICAL px.
    /// Both ends move together, so consecutive rows still edge-join exactly
    /// while an end that TERMINATES at the arm stays put.</summary>
    private const float TreeGuideDrop = 2f;

    private const float TreePillRadius = 5f;

    /// <summary>The one animated channel of the pill's opacity.</summary>
    private const int TreeRowHighlightChannel = 0;

    /// <summary>The branch a row's OWN depth column draws — a function of the
    /// depth, of whether the row discloses, and of whether it is its parent's
    /// last.</summary>
    private enum TreeBranch { None, Tee, Elbow, Fork, ForkLast }

    /// <summary>Where one depth's trunk stands, measured from the row's left
    /// edge: the ONE definition the pill's inset and the guide ink share.
    /// </summary>
    private static float TreeTrunkX(int depth) =>
        TreeRootTrunk + (depth - 1) * TreeIndent;

    /// <inheritdoc cref="TreeRow(string, string, in TreeRowProps, out Vector2, ControlStyle)"/>
    public static TreeRowAction TreeRow(
        string id,
        string label,
        in TreeRowProps props,
        ControlStyle style = default) =>
        TreeRow(id, label, in props, out _, style);

    /// <summary>
    /// The one sidebar/tree row: a 26px band carrying the highlight pill, the
    /// connector guides, the disclosure, the mark, the label and a right-aligned
    /// badge.
    ///
    /// <para>ACTIONS ARE THE CALLER'S. State <see cref="TreeRowProps.ActionSlots"/>
    /// and the row reserves the strip's span — the label truncates against it —
    /// then reports <paramref name="actionsOrigin"/>, the screen-space top-left
    /// of the first square. The caller seats its own controls there and restores
    /// the cursor; nothing about their appearance is this row's business.</para>
    ///
    /// <para>The row and its disclosure are TWO REAL RESERVES: the row is
    /// submitted first and yields arbitration through
    /// <c>SetItemAllowOverlap</c>, so a press landing on the chevron — or on a
    /// caller's action — takes ImGui's active id AWAY from it and the outcomes
    /// are mutually exclusive by construction rather than by a mouse-x
    /// comparison.</para>
    /// </summary>
    public static TreeRowAction TreeRow(
        string id,
        string label,
        in TreeRowProps props,
        out Vector2 actionsOrigin,
        ControlStyle style = default)
    {
        var theme = ActiveTheme;
        // Content and Fill both resolve to the available region here, so
        // UiWidth.Fixed is the only width path that changes the row.
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / ImGuiHelpers.GlobalScale,
            theme.Controls.ListRowHeight);
        float scale = metrics.Scale;
        float height = metrics.Height;

        int depth = Math.Max(0, props.Depth);
        bool discloses = props.Expander != SidebarExpander.None;
        var branch = depth == 0
            ? TreeBranch.None
            : discloses
                ? props.IsLastChild ? TreeBranch.ForkLast : TreeBranch.Fork
                : props.IsLastChild ? TreeBranch.Elbow : TreeBranch.Tee;

        // Rows stack seamlessly at exactly 26px — suppress ImGui's ambient
        // vertical ItemSpacing for the reserve.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        uint identity = ImGui.GetID(id);
        var hit = Interactive.Reserve(
            id, new Vector2(metrics.Width, height), disabled: false);
        ImGui.PopStyleVar();
        // Yielded for EVERY later reserving sibling, not just the disclosure:
        // the caller's action strip is the row's second overlapping target, and
        // ImGui refuses hover to a later item while an earlier one owns it.
        bool chevronReserves = discloses && !props.ExpanderDisabled;
        if (chevronReserves || props.ActionSlots > 0)
            ImGui.SetItemAllowOverlap();
        var dl = ImGui.GetWindowDrawList();

        // Never hit.Clicked: a press is not an activation, and it is precisely
        // the press frame on which both items are momentarily live.
        var action = hit.Activated
            ? TreeRowAction.Selected
            : TreeRowAction.None;
        // The context edge is read from ImGui directly: the reservation reports
        // the LEFT button's edges alone, and rows have always opened their menu
        // on "hovered and right-clicked" rather than on a release-inside.
        if (hit.Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            action = TreeRowAction.Context;

        float contentRight = hit.ScreenMax.X - props.TrailingInset * scale;

        // ---- the pill -----------------------------------------------------
        // Which rule paints it this frame. Transparent means no rule matched,
        // which is also why fading OUT is instant.
        var fill = props.DropTarget
            ? theme.Chrome.AccentFill
            : props.Selected
                ? theme.Chrome.SidebarSelected
                : hit.Hovered
                    ? theme.Chrome.SidebarHover
                    : Vector4.Zero;
        Span<MotionChannel> highlight =
        [
            MotionChannel.Number(
                TreeRowHighlightChannel, fill.W > 0f ? 1f : 0f),
        ];
        Motion.Toward(identity, Transition.PictoFast, highlight);
        float pillOpacity = highlight[0].Scalar;
        if (fill.W > 0f && pillOpacity > 0f)
        {
            // A nested pill starts clear of its own branch arm; a root pill
            // carries the 1px CSS inset. The 1px bottom shave is the same
            // accepted look. The right edge is the CONTENT edge, not the
            // window edge.
            float inset = (depth == 0
                ? TreeRootPillInset
                : TreeTrunkX(depth) + TreePillClearance) * scale;
            var border = props.DropTarget
                ? theme.Chrome.AccentFillBorder.Fade(pillOpacity)
                : (Vector4?)null;
            BoxRenderer.Draw(
                dl,
                new Vector2(hit.ScreenMin.X + inset, hit.ScreenMin.Y),
                new Vector2(contentRight, hit.ScreenMax.Y - scale),
                new BoxStyle
                {
                    BackgroundColor = fill.Fade(pillOpacity),
                    BorderRadius = TreePillRadius,
                    BorderWidth = border is null ? 0f : 1f,
                    BorderTopColor = border,
                    BorderRightColor = border,
                    BorderBottomColor = border,
                    BorderLeftColor = border,
                });
        }

        if (branch != TreeBranch.None && !props.HideGuides)
            DrawTreeGuides(
                dl, hit.ScreenMin, hit.ScreenMax, props.Trunks, depth, branch,
                scale, theme);

        // ---- the disclosure -----------------------------------------------
        // A root's chevron sits in its own 16px slot; a nested one is centred
        // on the trunk it cuts.
        if (discloses)
        {
            float chevronLeft = depth == 0
                ? 0f
                : TreeTrunkX(depth) - TreeChevronCenter;
            var chevronMin = new Vector2(
                hit.ScreenMin.X + chevronLeft * scale, hit.ScreenMin.Y);
            var chevronMax = new Vector2(
                chevronMin.X + TreeChevronBox * scale, hit.ScreenMax.Y);
            bool chevronHovered = false;
            if (chevronReserves)
            {
                // The chevron is a REAL reserved item over its own drawn box,
                // submitted AFTER the row so a press takes the active id from
                // it. The row's own hover was resolved above, while the chevron
                // did not yet exist — the CSS result exactly, since the arrow
                // is a child and pointing at it keeps .row:hover on.
                var cursorAfterRow = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(chevronMin);
                ImGui.PushID(id);
                var chevronHit = Interactive.Reserve(
                    "##expander", chevronMax - chevronMin, disabled: false);
                ImGui.PopID();
                ImGui.SetCursorScreenPos(cursorAfterRow);
                chevronHovered = chevronHit.Hovered;
                if (chevronHit.Activated)
                    action = TreeRowAction.Expander;
                if (chevronHovered)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            DrawDisclosureChevron(
                dl, chevronMin, chevronMax,
                props.Expander == SidebarExpander.Open,
                props.ExpanderDisabled, chevronHovered, scale, theme);
        }

        // ---- mark, label, badge -------------------------------------------
        // The zone spans everything left of the label: the root's expander
        // slot, or a nested row's trunk plus its label offset.
        float zoneWidth = depth == 0
            ? TreeRootSlot
            : TreeTrunkX(depth) + TreeLabelOffset;
        float x = hit.ScreenMin.X + zoneWidth * scale;
        bool hasMark = props.IconTexture is not null
            || props.Icon is not null
            || props.IconName is not null;
        if (!props.HideIcon && hasMark)
        {
            float side = TreeIconSide * scale;
            // A NESTED row's mark centres on its children's trunk — the
            // guide line those children will hang from — instead of sitting
            // a label-offset past its own (user 2026-08-11).
            if (depth > 0)
                x = hit.ScreenMin.X
                    + (TreeTrunkX(depth + 1) - TreeIconSide * 0.5f) * scale;
            var markMin = theme.Optical.Snap(new Vector2(
                x, hit.ScreenMin.Y + (height - side) * 0.5f));
            var markMax = markMin + new Vector2(side);
            if (props.IconTexture is { } texture)
                dl.AddImage(
                    texture.Handle,
                    markMin,
                    markMax,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                        new Vector4(1f, 1f, 1f, TreeIconOpacity))));
            else if (props.Icon is { } glyph)
                IconIn(
                    markMin, markMax, glyph, theme.Text,
                    opacity: TreeIconOpacity);
            else
                IconIn(
                    markMin, markMax, props.IconName!, theme.Text,
                    opacity: TreeIconOpacity);
            x += (TreeIconSide + TreeIconGap) * scale;
        }

        float actionSide =
            (props.ActionSide > 0f ? props.ActionSide : theme.Controls.SwitchHeight)
            * scale;
        float actionsWidth = props.ActionSlots > 0
            ? props.ActionSlots * actionSide
                + (props.ActionSlots - 1) * TreeActionGap * scale
            : 0f;
        float actionsTrail = props.ActionSlots > 0 ? TreeActionGap * scale : 0f;
        actionsOrigin = new Vector2(
            contentRight - actionsTrail - actionsWidth,
            hit.ScreenMin.Y + (height - actionSide) * 0.5f);

        float badgeWidth = 0f;
        float badgeTrail = 0f;
        var badgeStyle = new TextStyle
        {
            // The badge: 11px mono on the label's own optical line.
            Size = theme.Typography.CaptionSize,
            Family = FontFamily.Mono,
            Color = theme.FormLabel,
        };
        if (!string.IsNullOrEmpty(props.Badge))
        {
            badgeWidth = MeasureText(props.Badge, badgeStyle).X;
            badgeTrail = theme.Spacing.Two * scale;
        }

        float labelRight =
            contentRight - actionsTrail - actionsWidth - badgeTrail - badgeWidth;
        if (labelRight > x)
        {
            var labelStyle = new TextStyle
            {
                Size = props.LabelSize ?? theme.Typography.BodySize,
                Color = theme.Text,
            };
            float span = labelRight - x;
            var labelMin = new Vector2(x, hit.ScreenMin.Y);
            var labelBand = new Vector2(span, height);
            // Truncate constrains ONLY on overflow: the clip's snapped edge
            // would otherwise shave a fitting run's descender. The label is
            // judged against the ICON's ink, not the band centre — the
            // accepted seat.
            if (MeasureText(label, labelStyle).X <= span)
                TextInBand(
                    labelMin, labelBand, label, labelStyle,
                    props.CenterLabel ? TextAlign.Center : TextAlign.Start,
                    besideIcon: true);
            else
                // An overflowing label truncates from the start either way:
                // centring a cut run hides both of its ends.
                TextInBand(
                    labelMin, labelBand, label, labelStyle,
                    TextConstraint.Truncate(span), TextAlign.Start,
                    besideIcon: true);
        }
        if (badgeWidth > 0f)
            TextInBand(
                new Vector2(labelRight, hit.ScreenMin.Y),
                new Vector2(badgeWidth, height),
                props.Badge!,
                badgeStyle,
                TextAlign.Start,
                besideIcon: true);

        return action;
    }

    /// <summary>
    /// The connector ink. Segments are FILLED rectangles, not stroked lines:
    /// anti-aliased caps stack alpha where two rows meet, and a shared endpoint
    /// would then read as a bright dot on every band boundary.
    /// </summary>
    private static void DrawTreeGuides(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        uint trunks,
        int depth,
        TreeBranch branch,
        float scale,
        Theme theme)
    {
        float half = MathF.Max(1f, scale) * 0.5f;
        uint color = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(theme.TextMuted));
        // The arm's line, and the cutout's gap, are both measured from the
        // band's own midline rather than from the row height as a literal.
        float mid = (min.Y + max.Y) * 0.5f;
        float gap = 4f * scale;
        float top = min.Y + TreeGuideDrop;
        float bottom = max.Y + TreeGuideDrop;

        // Ancestor trunks: one column per level whose sibling line continues.
        for (int level = 1; level < depth && level < 32; level++)
        {
            if ((trunks & (1u << level)) == 0)
                continue;
            GuideVertical(
                draw, min.X + TreeTrunkX(level) * scale, top, bottom, half,
                color);
        }

        float x = min.X + TreeTrunkX(depth) * scale;
        switch (branch)
        {
            case TreeBranch.Fork:
                GuideVertical(draw, x, top, mid - gap, half, color);
                GuideVertical(draw, x, mid + gap, bottom, half, color);
                GuideHorizontal(
                    draw, x + 4.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
            case TreeBranch.ForkLast:
                GuideVertical(draw, x, top, mid - gap, half, color);
                GuideHorizontal(
                    draw, x + 4.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
            case TreeBranch.Elbow:
                // A crisp hard L: the vertical leg owns the square corner and
                // the horizontal leg begins at its right edge, so translucent
                // geometry touches without overlapping.
                draw.AddRectFilled(
                    new Vector2(x - half, top),
                    new Vector2(x + half, mid + half),
                    color);
                draw.AddRectFilled(
                    new Vector2(x + half, mid - half),
                    new Vector2(x + 8.5f * scale, mid + half),
                    color);
                break;
            default:
                GuideVertical(draw, x, top, bottom, half, color);
                GuideHorizontal(
                    draw, x + 0.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
        }
    }

    private static void GuideVertical(
        ImDrawListPtr draw, float x, float y0, float y1, float half, uint color) =>
        draw.AddRectFilled(
            new Vector2(x - half, y0), new Vector2(x + half, y1), color);

    private static void GuideHorizontal(
        ImDrawListPtr draw, float x0, float x1, float y, float half, uint color) =>
        draw.AddRectFilled(
            new Vector2(x0, y - half), new Vector2(x1, y + half), color);

    /// <summary>
    /// The one disclosure affordance: the compact filled triangle, visible in
    /// both states, hover-emphasized over its OWN box, faded while the row's
    /// children are temporarily unavailable.
    /// </summary>
    private static void DrawDisclosureChevron(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        bool open,
        bool disabled,
        bool hovered,
        float scale,
        Theme theme)
    {
        float alpha = disabled ? 0.25f : hovered ? 1f : 0.7f;
        uint color = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(theme.Chrome.Text with { W = alpha }));
        var center = new Vector2(
            min.X + TreeChevronCenter * scale, (min.Y + max.Y) * 0.5f);
        if (open)
            draw.AddTriangleFilled(
                center + new Vector2(-3.5f, -2.5f) * scale,
                center + new Vector2(3.5f, -2.5f) * scale,
                center + new Vector2(0f, 2.5f) * scale,
                color);
        else
            draw.AddTriangleFilled(
                center + new Vector2(-2.5f, -3.5f) * scale,
                center + new Vector2(2.5f, 0f) * scale,
                center + new Vector2(-2.5f, 3.5f) * scale,
                color);
    }

}
