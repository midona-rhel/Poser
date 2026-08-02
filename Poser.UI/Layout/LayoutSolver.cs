using System;
using System.Numerics;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The two-pass box solver over one frame arena. Everything here is in
/// LOGICAL (CSS-pixel) units: the paint pass owns the single conversion to
/// physical pixels, so a rounded edge is derived from an ABSOLUTE logical
/// coordinate and adjacent siblings can never drift apart by a rounding step.
/// Measure is post-order intrinsic; Arrange is pre-order and writes the
/// absolute, root-relative position of every element.
///
/// <para>Measure is also where each element's sheet chain is FLATTENED into
/// its resolved layout and typography, so the solver reads plain values and
/// never touches a stylesheet.</para>
/// </summary>
internal static class LayoutSolver
{
    /// <summary>Post-order intrinsic measure. Fill resolves against the
    /// available span, Fixed wins outright, Content asks the element.</summary>
    internal static void Measure(
        FrameArena arena, int node, float availWidth, float availHeight,
        in Poser.UI.InheritedType inherited)
    {
        if (node == 0)
            return;

        ref ElementRecord record = ref arena[node];
        Poser.UI.ElementSheet? patch = arena.HasPatch(record.PatchSlot)
            ? arena.Patch(record.PatchSlot)
            : null;
        record.Layout = Poser.UI.StyleResolver.Layout(record.Sheet, in patch);
        record.Type = Poser.UI.StyleResolver.Type(record.Sheet, in patch, in inherited);

        if (record.PortalSlot != 0)
        {
            MeasureDetached(arena, node);
            return;
        }

        Poser.UI.ResolvedLayout style = record.Layout;
        Vector2 content = MeasureBox(arena, node, in style, availWidth, availHeight);
        // The element's own leaf content is a FACET, not a species: a box that
        // also carries a run is as wide as the wider of the two.
        if (record.Text is { Length: > 0 } text)
        {
            record.TextSize =
                Poser.UI.LegacyCrystarium.MeasureText(text, record.Type.Text(null));
            content = Vector2.Max(
                content,
                (record.TextSize / ImGuiHelpers.GlobalScale)
                + new Vector2(style.Padding.Horizontal, style.Padding.Vertical));
        }
        if (record.Glyph is not null)
            content = Vector2.Max(content, new Vector2(record.GlyphSize));

        arena[node].LogicalSize = new Vector2(
            ResolveWidth(in style, content.X, availWidth),
            Resolve(style.Height, content.Y, availHeight));
    }

    /// <summary>Pre-order placement. <paramref name="logicalOrigin"/> is
    /// absolute and root-relative; <paramref name="logicalSize"/> is the
    /// span the parent granted.</summary>
    internal static void Arrange(FrameArena arena, int node, Vector2 logicalOrigin, Vector2 logicalSize)
    {
        if (node == 0)
            return;

        if (arena[node].PortalSlot != 0)
        {
            ArrangeDetached(arena, node, logicalOrigin);
            return;
        }

        arena[node].LogicalPos = logicalOrigin;
        arena[node].LogicalSize = logicalSize;
        if (arena[node].ChildCount == 0)
            return;

        Poser.UI.ResolvedLayout style = arena[node].Layout;
        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        Vector2 contentOrigin = logicalOrigin + new Vector2(style.Padding.Left, style.Padding.Top);
        Vector2 contentSize = new(
            MathF.Max(0f, logicalSize.X - style.Padding.Horizontal),
            MathF.Max(0f, logicalSize.Y - style.Padding.Vertical));

        if (style.Flow == Poser.UI.UiFlow.Stack)
        {
            ArrangeStack(arena, in style, start, count, contentOrigin, contentSize);
            return;
        }

        bool row = style.Flow == Poser.UI.UiFlow.Row;
        float mainAvail = row ? contentSize.X : contentSize.Y;
        float crossAvail = row ? contentSize.Y : contentSize.X;
        float used = 0f;
        int fills = 0;
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            ref Poser.UI.ResolvedLayout cs = ref arena[child].Layout;
            used += row ? cs.Margin.Horizontal : cs.Margin.Vertical;
            if (IsFillMain(in cs, row))
                fills++;
            else
                used += row ? arena[child].LogicalSize.X : arena[child].LogicalSize.Y;
        }

        float gaps = style.Gap * (count - 1);
        // Fill children split what Content and Fixed siblings left behind.
        float share = fills > 0 ? MathF.Max(0f, mainAvail - used - gaps) / fills : 0f;
        float total = used + gaps + (share * fills);
        float cursor = (row ? contentOrigin.X : contentOrigin.Y)
            + Offset(style.Justify, mainAvail, total);

        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            Poser.UI.ResolvedLayout cs = arena[child].Layout;
            EdgeInsets margin = cs.Margin;
            Vector2 measured = arena[child].LogicalSize;
            float mainSize = IsFillMain(in cs, row)
                ? share
                : (row ? measured.X : measured.Y);
            float crossSpan = MathF.Max(
                0f, crossAvail - (row ? margin.Vertical : margin.Horizontal));
            // A cross-axis Fill is the container's cross span BY CONSTRUCTION,
            // not its own measure: the measure pass resolved it against the
            // parent's raw avail, which can exceed this container's actual
            // grant (user-caught: the nav rule ran past its row through the
            // footer band).
            float crossSize = style.Align == Poser.UI.UiAlign.Stretch
                    || IsFillCross(in cs, row)
                ? crossSpan
                : (row ? measured.Y : measured.X);
            float crossPos = (row ? contentOrigin.Y + margin.Top : contentOrigin.X + margin.Left)
                + Offset(style.Align, crossSpan, crossSize);

            cursor += row ? margin.Left : margin.Top;
            Arrange(
                arena,
                child,
                row ? new Vector2(cursor, crossPos) : new Vector2(crossPos, cursor),
                row ? new Vector2(mainSize, crossSize) : new Vector2(crossSize, mainSize));
            cursor += mainSize + (row ? margin.Right : margin.Bottom);
            if (i < count - 1)
                cursor += style.Gap;
        }
    }

    /// <summary>
    /// The span a portal's floating surface occupies, logical. A zero authored
    /// width means "as wide as the ANCHOR": a Fill-sized trigger has no span
    /// until the solver grants it, which is long after its menu was declared.
    /// </summary>
    internal static Vector2 PortalSurface(FrameArena arena, int node)
    {
        ref PortalRecord portal = ref arena.Portal(arena[node].PortalSlot);
        Vector2 authored = portal.ContentSize;
        int anchor = portal.AnchorNode;
        return authored.X > 0f || anchor == 0
            ? authored
            : new Vector2(arena[anchor].LogicalSize.X, authored.Y);
    }

    /// <summary>
    /// The box a portal's DETACHED subtree is laid out in: the surface less
    /// its padding, with the scroll viewport's authored height standing in
    /// when the children scroll past it.
    /// </summary>
    internal static Vector2 PortalContent(FrameArena arena, int node)
    {
        ref PortalRecord portal = ref arena.Portal(arena[node].PortalSlot);
        Vector2 surface = PortalSurface(arena, node);
        float padding = portal.Padding * 2f;
        float height = portal.ScrollRegionHeight > 0f
            ? portal.ScrollRegionHeight
            : MathF.Max(0f, surface.Y - padding);
        return new Vector2(MathF.Max(0f, surface.X - padding), height);
    }

    /// <summary>
    /// The logical Y at which a portal's SCROLLED region starts: the stacked
    /// height of the fixed head above it. Read by the walk, which has to place
    /// the scroll child window itself, and by <see cref="ArrangeDetached"/>,
    /// which places the head in the first place — one definition, so the
    /// viewport can never land somewhere the head did not end.
    /// </summary>
    internal static float PortalScrollTop(FrameArena arena, int node)
    {
        int start = arena[node].ChildStart;
        int head = Math.Min(
            arena.Portal(arena[node].PortalSlot).ScrollFromChild,
            arena[node].ChildCount);
        float y = 0f;
        for (int i = 0; i < head; i++)
            y += arena[arena.ChildAt(start + i).Index].LogicalSize.Y;
        return y;
    }

    /// <summary>
    /// A portal is OUT OF FLOW: it contributes nothing to its parent's box, so
    /// its subtree is measured against the SURFACE's constraints rather than
    /// whatever the parent had left over.
    /// </summary>
    private static void MeasureDetached(FrameArena arena, int node)
    {
        Vector2 content = PortalContent(arena, node);
        Vector2 surface = PortalSurface(arena, node);
        // The head is measured against what the SURFACE has left, not against
        // the viewport: a caption band above a 190px list is not constrained by
        // the list's height.
        float headHeight = MathF.Max(
            0f, surface.Y - arena.Portal(arena[node].PortalSlot).Padding * 2f);
        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        int head = Math.Min(
            arena.Portal(arena[node].PortalSlot).ScrollFromChild, count);
        Poser.UI.InheritedType inherited = Inherit(in arena[node].Type);
        for (int i = 0; i < count; i++)
            Measure(
                arena,
                arena.ChildAt(start + i).Index,
                content.X,
                i < head ? headHeight : content.Y,
                in inherited);
        arena[node].LogicalSize = Vector2.Zero;
    }

    /// <summary>
    /// The subtree is placed from the surface's own origin, not the parent's:
    /// the walk re-anchors it at the popup's cursor, so a portal child's
    /// arranged position is already surface-relative.
    ///
    /// <para>A surface with a fixed HEAD places it as a plain vertical stack
    /// from the content origin, then starts the scrolled children over again at
    /// zero — because the walk re-anchors those a second time, at the scroll
    /// child window's own cursor.</para>
    /// </summary>
    private static void ArrangeDetached(FrameArena arena, int node, Vector2 logicalOrigin)
    {
        arena[node].LogicalPos = logicalOrigin;
        arena[node].LogicalSize = Vector2.Zero;
        Vector2 content = PortalContent(arena, node);
        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        int head = Math.Min(
            arena.Portal(arena[node].PortalSlot).ScrollFromChild, count);
        float y = 0f;
        for (int i = 0; i < head; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            float band = arena[child].LogicalSize.Y;
            Arrange(arena, child, new Vector2(0f, y), new Vector2(content.X, band));
            y += band;
        }

        for (int i = head; i < count; i++)
            Arrange(arena, arena.ChildAt(start + i).Index, Vector2.Zero, content);
    }

    /// <summary>The logical width a text run is CUT to, or null for a run that
    /// renders at its intrinsic width.</summary>
    internal static Poser.UI.InheritedType Inherit(in Poser.UI.ResolvedType type) =>
        new(type.Size, type.Font, type.Weight);

    private static Vector2 MeasureBox(
        FrameArena arena, int node, in Poser.UI.ResolvedLayout style,
        float availWidth, float availHeight)
    {
        // A Content box offers its children everything it was offered.
        float innerWidth = MathF.Max(
            0f, ResolveWidth(in style, availWidth, availWidth) - style.Padding.Horizontal);
        float innerHeight = MathF.Max(
            0f, Resolve(style.Height, availHeight, availHeight) - style.Padding.Vertical);

        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        bool row = style.Flow == Poser.UI.UiFlow.Row;
        bool stack = style.Flow == Poser.UI.UiFlow.Stack;
        float main = 0f;
        float cross = 0f;
        Poser.UI.InheritedType inherited = Inherit(in arena[node].Type);
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            Measure(arena, child, innerWidth, innerHeight, in inherited);
            Vector2 size = arena[child].LogicalSize;
            EdgeInsets margin = arena[child].Layout.Margin;
            float childMain = (row || stack ? size.X : size.Y)
                + (row || stack ? margin.Horizontal : margin.Vertical);
            float childCross = (row || stack ? size.Y : size.X)
                + (row || stack ? margin.Vertical : margin.Horizontal);
            // A Fill child has no intrinsic main contribution: it consumes what
            // its Content and Fixed siblings leave, so counting its resolved
            // span here would inflate the parent by the whole row.
            if (!stack && IsFillMain(in arena[child].Layout, row))
                childMain = row ? margin.Horizontal : margin.Vertical;
            if (stack)
            {
                main = MathF.Max(main, childMain);
                cross = MathF.Max(cross, childCross);
            }
            else
            {
                main += childMain;
                cross = MathF.Max(cross, childCross);
            }
        }

        if (!stack && count > 1)
            main += style.Gap * (count - 1);

        Vector2 content = row || stack ? new Vector2(main, cross) : new Vector2(cross, main);
        return content + new Vector2(style.Padding.Horizontal, style.Padding.Vertical);
    }

    private static void ArrangeStack(
        FrameArena arena, in Poser.UI.ResolvedLayout style, int start, int count,
        Vector2 contentOrigin, Vector2 contentSize)
    {
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            EdgeInsets margin = arena[child].Layout.Margin;
            Vector2 measured = arena[child].LogicalSize;
            float spanX = MathF.Max(0f, contentSize.X - margin.Horizontal);
            float spanY = MathF.Max(0f, contentSize.Y - margin.Vertical);
            float width = style.Justify == Poser.UI.UiAlign.Stretch ? spanX : measured.X;
            float height = style.Align == Poser.UI.UiAlign.Stretch ? spanY : measured.Y;
            Arrange(
                arena,
                child,
                new Vector2(
                    contentOrigin.X + margin.Left + Offset(style.Justify, spanX, width),
                    contentOrigin.Y + margin.Top + Offset(style.Align, spanY, height)),
                new Vector2(width, height));
        }
    }

    private static bool IsFillMain(in Poser.UI.ResolvedLayout style, bool row) =>
        (row ? style.Width.Kind : style.Height.Kind) == Poser.UI.UiDimKind.Fill;

    private static bool IsFillCross(in Poser.UI.ResolvedLayout style, bool row) =>
        (row ? style.Height.Kind : style.Width.Kind) == Poser.UI.UiDimKind.Fill;

    /// <summary>
    /// The width axis, clamped by MaxWidth. The clamp is applied at EVERY width
    /// resolution — the element's own box and the span it offers its children —
    /// so a capped Fill column measures, arranges and cuts its content at the
    /// cap rather than only drawing narrow.
    /// </summary>
    private static float ResolveWidth(
        in Poser.UI.ResolvedLayout style, float content, float available)
    {
        float width = Resolve(style.Width, content, available);
        return style.MaxWidth > 0f && width > style.MaxWidth ? style.MaxWidth : width;
    }

    private static float Resolve(in Poser.UI.UiDim dim, float content, float available) =>
        dim.Kind switch
        {
            Poser.UI.UiDimKind.Fixed => dim.Value,
            Poser.UI.UiDimKind.Fill => available,
            _ => content,
        };

    // Stretch has no meaning along the main axis, so it reads as Start.
    private static float Offset(Poser.UI.UiAlign align, float available, float used) =>
        align switch
        {
            Poser.UI.UiAlign.Center => (available - used) * 0.5f,
            Poser.UI.UiAlign.End => available - used,
            _ => 0f,
        };
}
