using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public enum SidebarExpander { None, Collapsed, Open }

/// <summary>The result of one tree-row gesture.</summary>
public enum TreeRowAction { None, Selected, Expander, Context }

/// <summary>Visual and interaction state for one tree row.</summary>
public record struct TreeRowProps
{
    /// <summary>The fallback icon when no texture is available.</summary>
    public TablerIcon? Icon;

    /// <summary>A registered icon name.</summary>
    public string? IconName;

    /// <summary>A caller-owned texture resolved for this frame.</summary>
    public IDalamudTextureWrap? IconTexture;

    /// <summary>Removes the icon and its spacing.</summary>
    public bool HideIcon;

    /// <summary>An optional label size override.</summary>
    public float? LabelSize;

    /// <summary>Centers the label inside its available span.</summary>
    public bool CenterLabel;

    /// <summary>Right-aligned mono readout (counts, "you", "spawned").</summary>
    public string? Badge;

    /// <summary>0 is a root row; each level costs one indent.</summary>
    public int Depth;

    /// <summary>Ancestor depths whose sibling lines continue.</summary>
    public uint Trunks;

    /// <summary>Last child of its parent — the branch is an L, not a T.
    /// </summary>
    public bool IsLastChild;

    /// <summary>Hides guides without changing row geometry.</summary>
    public bool HideGuides;

    /// <summary>The disclosure state.</summary>
    public SidebarExpander Expander;

    /// <summary>Shows an inert disclosure without changing layout.</summary>
    public bool ExpanderDisabled;

    public bool Selected;

    /// <summary>Drag-hover: the accent fill over its own hairline.</summary>
    public bool DropTarget;

    /// <summary>Right padding reserved for the scroll gutter.</summary>
    public float TrailingInset;

    /// <summary>Action slots reserved after the label.</summary>
    public int ActionSlots;

    /// <summary>The action square's logical side; 0 takes the shell's switch
    /// size.</summary>
    public float ActionSide;
}

public static partial class Crystarium
{
    /// <summary>The indent one level costs.</summary>
    private const float TreeIndent = 20f;

    /// <summary>Depth one's trunk position.</summary>
    private const float TreeRootTrunk = 24f;

    /// <summary>A nested label's distance from its own trunk.</summary>
    private const float TreeLabelOffset = 14f;

    /// <summary>The root row's disclosure slot, left of the icon.</summary>
    private const float TreeRootSlot = 16f;

    /// <summary>Label inset for rows without a disclosure or icon.</summary>
    private const float TreeBareLabelPad = 6f;

    /// <summary>The disclosure's visible and interactive box.</summary>
    private const float TreeChevronBox = 18f;

    private const float TreeChevronCenter = 8f;

    /// <summary>The pill clears the branch arm by this much, so connector ink
    /// never runs under a selection.</summary>
    private const float TreePillClearance = 10f;

    /// <summary>A root pill's horizontal inset.</summary>
    private const float TreeRootPillInset = 1f;

    private const float TreeIconSide = 16f;

    private const float TreeIconGap = 6f;

    /// <summary>Resting icon opacity.</summary>
    private const float TreeIconOpacity = 0.85f;

    /// <summary>The strip's gap, and the gap that ends it at the content edge.
    /// </summary>
    private const float TreeActionGap = 2f;

    /// <summary>Extends free trunk ends to the neighboring row.</summary>
    private const float TreeGuideDrop = 2f;

    private const float TreePillRadius = 5f;

    /// <summary>The one animated channel of the pill's opacity.</summary>
    private const int TreeRowHighlightChannel = 0;

    /// <summary>The connector shape at the row's depth.</summary>
    private enum TreeBranch { None, Tee, Elbow, Fork, ForkLast }

    /// <summary>Returns a trunk's horizontal position.</summary>
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
    /// Draws a tree row and reports the first reserved action position.
    /// The disclosure and action controls own presses inside their boxes.
    /// </summary>
    public static TreeRowAction TreeRow(
        string id,
        string label,
        in TreeRowProps props,
        out Vector2 actionsOrigin,
        ControlStyle style = default)
    {
        var theme = ActiveTheme;
        // A fixed width is the only explicit width override.
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

        // Rows stack without ambient vertical spacing.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        uint identity = ImGui.GetID(id);
        var hit = Interactive.Reserve(
            id, new Vector2(metrics.Width, height), disabled: false);
        ImGui.PopStyleVar();
        // Later disclosure and action controls own their overlapping boxes.
        bool chevronReserves = discloses && !props.ExpanderDisabled;
        if (chevronReserves || props.ActionSlots > 0)
            ImGui.SetItemAllowOverlap();

        // A row scrolled outside the host window cannot be hovered, clicked,
        // or seen — its reserve advanced the layout, and that is all a long
        // list needs from it. Everything below is paint and secondary
        // reserves for controls nobody can reach.
        float rowClipTop = ImGui.GetWindowPos().Y;
        if (hit.ScreenMax.Y < rowClipTop
            || hit.ScreenMin.Y > rowClipTop + ImGui.GetWindowSize().Y)
        {
            actionsOrigin = default;
            return TreeRowAction.None;
        }
        var dl = ImGui.GetWindowDrawList();

        // Selection completes on release.
        var action = hit.Activated
            ? TreeRowAction.Selected
            : TreeRowAction.None;
        // Context menus open on a hovered right-button press.
        if (hit.Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            action = TreeRowAction.Context;

        float contentRight = hit.ScreenMax.X - props.TrailingInset * scale;

        // Transparent means no highlight rule matched.
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
            // Nested pills clear their connector and stop at the content edge.
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

        // Root disclosures use their slot; nested ones center on the trunk.
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
                // The later reserve gives the chevron ownership of its box.
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

        // The label begins after its disclosure, connector, and optional mark.
        float zoneWidth = depth == 0
            ? props.Expander == SidebarExpander.None && props.HideIcon
                ? TreeBareLabelPad
                : TreeRootSlot
            : TreeTrunkX(depth) + TreeLabelOffset;
        float x = hit.ScreenMin.X + zoneWidth * scale;
        bool hasMark = props.IconTexture is not null
            || props.Icon is not null
            || props.IconName is not null;
        if (!props.HideIcon && hasMark)
        {
            float side = TreeIconSide * scale;
            // Nested marks center on their children's trunk.
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
            var labelStyle = SidebarTreeLabelStyle(theme, props.LabelSize);
            float span = labelRight - x;
            var labelMin = new Vector2(x, hit.ScreenMin.Y);
            var labelBand = new Vector2(span, height);
            // Only overflowing labels use a clip rectangle.
            if (MeasureText(label, labelStyle).X <= span)
                TextInBand(
                    labelMin, labelBand, label, labelStyle,
                    props.CenterLabel ? TextAlign.Center : TextAlign.Start,
                    besideIcon: true);
            else
                // Truncated labels stay start-aligned.
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

    /// <summary>Draws connector segments without overlapping endpoints.</summary>
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
        // Arms and disclosure gaps share the row's midline.
        float mid = (min.Y + max.Y) * 0.5f;
        float gap = 4f * scale;
        float top = min.Y + TreeGuideDrop;
        float bottom = max.Y + TreeGuideDrop;

        // Draw each continuing ancestor trunk.
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
                // The vertical leg owns the elbow corner.
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

    /// <summary>Draws the disclosure triangle.</summary>
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
