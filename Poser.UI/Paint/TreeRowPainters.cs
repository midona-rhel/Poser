using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The tree row's three seams, and only geometry no stylesheet can express: a
/// pill whose left edge is a function of DEPTH, the connector ink between rows,
/// and the disclosure triangle. Each is a singleton — the depth, the trunk mask
/// and the branch are typed fields of the element, so a hook carries no per-row
/// state.
/// </summary>
internal sealed class TreeRowPillPainter : IPainter
{
    internal static readonly TreeRowPillPainter Instance = new();

    private TreeRowPillPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        // No state, no pill: the sheet states a fill for hover and for
        // selection and for nothing else, exactly as the CSS pseudo-element
        // stops applying the moment the rule stops matching.
        if (context.Style.Fill is not { } fill)
            return default;

        float scale = ImGuiHelpers.GlobalScale;
        int depth = context.Record.Guides.Depth;
        // A nested pill starts clear of its own branch arm so connector ink
        // never runs under a selection; a root pill carries SidebarRow's own
        // 1px inset. The 1px bottom shave is the same accepted look.
        float inset = depth == 0
            ? Poser.UI.TreeRow.RootPillInset
            : Poser.UI.TreeRow.TrunkX(depth) + Poser.UI.TreeRow.PillClearance;
        context.DrawList.AddRectFilled(
            new Vector2(context.Min.X + inset * scale, context.Min.Y),
            new Vector2(context.Max.X, context.Max.Y - scale),
            ImGui.ColorConvertFloat4ToU32(Poser.UI.ColorEx.ApplyAlpha(fill)),
            context.Style.Radius * scale);
        return default;
    }
}

/// <summary>
/// The connector ink. Segments are FILLED rectangles, not stroked lines:
/// anti-aliased caps stack alpha where two rows meet, and a shared endpoint
/// would then read as a bright dot on every band boundary.
/// </summary>
internal sealed class TreeGuidePainter : IPainter
{
    internal static readonly TreeGuidePainter Instance = new();

    private TreeGuidePainter()
    {
    }

    /// <summary>One PHYSICAL pixel — the user's correction is stated in device
    /// pixels, so it does not grow with the global scale.</summary>
    private const float Drop = 1f;

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.TreeGuideSpec guides = context.Record.Guides;
        int depth = guides.Depth;
        if (depth == 0 || guides.Branch == Poser.UI.TreeBranch.None)
            return default;

        float scale = ImGuiHelpers.GlobalScale;
        float half = MathF.Max(1f, scale) * 0.5f;
        uint color = ImGui.ColorConvertFloat4ToU32(
            Poser.UI.ColorEx.ApplyAlpha(
                Poser.UI.LegacyCrystarium.ActiveTheme.TextMuted));
        ImDrawListPtr draw = context.DrawList;
        // The arm's line, and the cutout's gap, are both measured from the
        // band's own midline rather than from the row height as a literal.
        float mid = (context.Min.Y + context.Max.Y) * 0.5f;
        float gap = 4f * scale;
        // USER CORRECTION (2026-08-03): the vertical stroke read one pixel HIGH
        // against its band, so a trunk's FREE ends — the ones that meet the
        // NEIGHBOURING row rather than this row's own arm — drop one PHYSICAL
        // pixel. Both ends move by the same amount, so consecutive rows still
        // edge-join exactly (this row's shifted bottom IS the next row's
        // shifted top), while an end that TERMINATES at the arm stays put and
        // every junction keeps its corner.
        float top = context.Min.Y + Drop;
        float bottom = context.Max.Y + Drop;

        // Ancestor trunks: one column per level whose sibling line continues.
        for (int level = 1; level < depth && level < 32; level++)
        {
            if ((guides.Trunks & (1u << level)) == 0)
                continue;
            Vertical(draw, Column(in context, level, scale), top, bottom, half, color);
        }

        float x = Column(in context, depth, scale);
        switch (guides.Branch)
        {
            case Poser.UI.TreeBranch.Fork:
                Vertical(draw, x, top, mid - gap, half, color);
                Vertical(draw, x, mid + gap, bottom, half, color);
                Horizontal(draw, x + 4.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
            case Poser.UI.TreeBranch.ForkLast:
                Vertical(draw, x, top, mid - gap, half, color);
                Horizontal(draw, x + 4.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
            case Poser.UI.TreeBranch.Elbow:
                // A crisp hard L: the vertical leg owns the square corner and
                // the horizontal leg begins at its right edge, so translucent
                // geometry touches without overlapping.
                draw.AddRectFilled(
                    new Vector2(x - half, top), new Vector2(x + half, mid + half), color);
                draw.AddRectFilled(
                    new Vector2(x + half, mid - half),
                    new Vector2(x + 8.5f * scale, mid + half),
                    color);
                break;
            default:
                Vertical(draw, x, top, bottom, half, color);
                Horizontal(draw, x + 0.5f * scale, x + 8.5f * scale, mid, half, color);
                break;
        }

        return default;
    }

    private static float Column(in PaintContext context, int depth, float scale) =>
        context.Min.X + Poser.UI.TreeRow.TrunkX(depth) * scale;

    private static void Vertical(
        ImDrawListPtr draw, float x, float y0, float y1, float half, uint color) =>
        draw.AddRectFilled(new Vector2(x - half, y0), new Vector2(x + half, y1), color);

    private static void Horizontal(
        ImDrawListPtr draw, float x0, float x1, float y, float half, uint color) =>
        draw.AddRectFilled(new Vector2(x0, y - half), new Vector2(x1, y + half), color);
}

/// <summary>
/// The one disclosure affordance for actor and category rows: the compact filled
/// triangle, visible in both states, hover-emphasized over its OWN box, faded
/// and inert while the row's children are temporarily unavailable.
///
/// <para>NOTE: PBI-002 runtime round 1 specified Tabler chevrons here; the user
/// explicitly requested the original triangle affordance back during the
/// 2026-07-24 in-game session — this supersedes that clarification line.</para>
/// </summary>
internal sealed class DisclosureChevronPainter : IPainter
{
    /// <summary>The interactive affordance: it reserves, because the lift is
    /// scoped to the triangle's own box and because the press must be takeable
    /// from the row underneath.</summary>
    internal static readonly DisclosureChevronPainter Live = new(inert: false);

    /// <summary>The refused affordance: drawn, faded, and reserving NOTHING —
    /// an inert mark that took a hit rect would steal its row's hover.
    /// </summary>
    internal static readonly DisclosureChevronPainter Inert = new(inert: true);

    private readonly bool _inert;

    private DisclosureChevronPainter(bool inert) => _inert = inert;

    public bool NeedsHit => !_inert;

    public PaintResult Paint(in PaintContext context)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float alpha = context.Record.Disabled
            ? 0.25f
            : context.Hit.Hovered ? 1f : 0.7f;
        uint color = ImGui.ColorConvertFloat4ToU32(
            Poser.UI.ColorEx.ApplyAlpha(
                Poser.UI.LegacyCrystarium.ActiveTheme.Chrome.Text with { W = alpha }));
        Vector2 center = new(
            context.Min.X + Poser.UI.TreeRow.ChevronCenter * scale,
            (context.Min.Y + context.Max.Y) * 0.5f);
        if (context.Record.Selected)
            context.DrawList.AddTriangleFilled(
                center + new Vector2(-3.5f, -2.5f) * scale,
                center + new Vector2(3.5f, -2.5f) * scale,
                center + new Vector2(0f, 2.5f) * scale,
                color);
        else
            context.DrawList.AddTriangleFilled(
                center + new Vector2(-2.5f, -3.5f) * scale,
                center + new Vector2(2.5f, 0f) * scale,
                center + new Vector2(-2.5f, 3.5f) * scale,
                color);
        return default;
    }
}
