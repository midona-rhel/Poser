using System;
using System.Numerics;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The two-pass box solver over one frame arena. Everything here is in
/// LOGICAL (CSS-pixel) units: the paint pass owns the single conversion to
/// physical pixels, so a rounded edge is derived from an ABSOLUTE logical
/// coordinate and adjacent siblings can never drift apart by a rounding
/// step. Measure is post-order intrinsic; Arrange is pre-order and writes
/// the absolute, root-relative position of every element.
/// </summary>
internal static class LayoutSolver
{
    /// <summary>Post-order intrinsic measure. Fill resolves against the
    /// available span, Fixed wins outright, Content asks the element.</summary>
    internal static void Measure(FrameArena arena, int node, float availWidth, float availHeight)
    {
        if (node == 0)
            return;

        ElementKind kind = arena[node].Kind;
        UiStyle style = arena[node].Style;
        Vector2 content = kind switch
        {
            ElementKind.Text => Poser.UI.LegacyCrystarium.MeasureText(
                arena[node].Text ?? string.Empty, TextStyleOf(in arena[node]))
                / ImGuiHelpers.GlobalScale,
            ElementKind.Svg => new Vector2(arena[node].TextSize),
            ElementKind.Interactive => MeasureInteractive(
                arena, node, in style, availWidth, availHeight),
            _ => MeasureBox(arena, node, in style, availWidth, availHeight),
        };

        arena[node].LogicalSize = new Vector2(
            Resolve(style.Width, content.X, availWidth),
            Resolve(style.Height, content.Y, availHeight));
    }

    /// <summary>Pre-order placement. <paramref name="logicalOrigin"/> is
    /// absolute and root-relative; <paramref name="logicalSize"/> is the
    /// span the parent granted.</summary>
    internal static void Arrange(FrameArena arena, int node, Vector2 logicalOrigin, Vector2 logicalSize)
    {
        if (node == 0)
            return;

        arena[node].LogicalPos = logicalOrigin;
        arena[node].LogicalSize = logicalSize;
        // Leaves declare no children, so ONE test covers every kind: an
        // interactive element arranges its subtree exactly like a box, which
        // is what lets a control's content be real composed elements.
        if (arena[node].ChildCount == 0)
            return;

        UiStyle style = arena[node].Style;
        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        Vector2 contentOrigin = logicalOrigin + new Vector2(style.Padding.Left, style.Padding.Top);
        Vector2 contentSize = new(
            MathF.Max(0f, logicalSize.X - style.Padding.Horizontal),
            MathF.Max(0f, logicalSize.Y - style.Padding.Vertical));

        if (style.Flow == UiFlow.Stack)
        {
            ArrangeStack(arena, in style, start, count, contentOrigin, contentSize);
            return;
        }

        bool row = style.Flow == UiFlow.Row;
        float mainAvail = row ? contentSize.X : contentSize.Y;
        float crossAvail = row ? contentSize.Y : contentSize.X;
        float used = 0f;
        int fills = 0;
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            UiStyle cs = arena[child].Style;
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
            UiStyle cs = arena[child].Style;
            EdgeInsets margin = cs.Margin;
            Vector2 measured = arena[child].LogicalSize;
            float mainSize = IsFillMain(in cs, row)
                ? share
                : (row ? measured.X : measured.Y);
            float crossSpan = MathF.Max(
                0f, crossAvail - (row ? margin.Vertical : margin.Horizontal));
            float crossSize = style.Align == UiAlign.Stretch
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

    /// <summary>The text style a Text record declared; unset members fall
    /// back to the active theme inside the renderer.</summary>
    internal static Poser.UI.TextStyle TextStyleOf(in ElementRecord record) =>
        TextStyleOf(in record, null);

    /// <summary>As above, with the walk's inherited foreground standing in
    /// for an unstated color — currentColor, resolved by the nearest painter
    /// above the run.</summary>
    internal static Poser.UI.TextStyle TextStyleOf(
        in ElementRecord record, Vector4? inheritedForeground) => new()
    {
        Size = record.TextSize > 0f ? record.TextSize : (float?)null,
        Color = record.HasTextColor ? record.TextColor : inheritedForeground,
    };

    /// <summary>The declaration already filled the control's intrinsic box,
    /// so only a Fill dimension is still open — but its children measure
    /// normally, because the subtree is laid out INSIDE that box. A control
    /// that declared NO box at all measures like an ordinary box, so a hit
    /// area can be sized by whatever content was composed into it.</summary>
    private static Vector2 MeasureInteractive(
        FrameArena arena, int node, in UiStyle style, float availWidth, float availHeight)
    {
        Vector2 intrinsic = arena[node].LogicalSize;
        if (intrinsic == Vector2.Zero)
            return MeasureBox(arena, node, in style, availWidth, availHeight);

        float innerWidth = MathF.Max(
            0f, Resolve(style.Width, intrinsic.X, availWidth) - style.Padding.Horizontal);
        float innerHeight = MathF.Max(
            0f, Resolve(style.Height, intrinsic.Y, availHeight) - style.Padding.Vertical);
        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        for (int i = 0; i < count; i++)
            Measure(arena, arena.ChildAt(start + i).Index, innerWidth, innerHeight);
        return intrinsic;
    }

    private static Vector2 MeasureBox(
        FrameArena arena, int node, in UiStyle style, float availWidth, float availHeight)
    {
        // A Content box offers its children everything it was offered.
        float innerWidth = MathF.Max(
            0f, Resolve(style.Width, availWidth, availWidth) - style.Padding.Horizontal);
        float innerHeight = MathF.Max(
            0f, Resolve(style.Height, availHeight, availHeight) - style.Padding.Vertical);

        int start = arena[node].ChildStart;
        int count = arena[node].ChildCount;
        bool row = style.Flow == UiFlow.Row;
        bool stack = style.Flow == UiFlow.Stack;
        float main = 0f;
        float cross = 0f;
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            Measure(arena, child, innerWidth, innerHeight);
            Vector2 size = arena[child].LogicalSize;
            EdgeInsets margin = arena[child].Style.Margin;
            float childMain = (row || stack ? size.X : size.Y)
                + (row || stack ? margin.Horizontal : margin.Vertical);
            float childCross = (row || stack ? size.Y : size.X)
                + (row || stack ? margin.Vertical : margin.Horizontal);
            // A Fill child has no intrinsic main contribution: it consumes
            // what its Content and Fixed siblings leave, so counting its
            // resolved span here would inflate the parent by the whole row.
            if (!stack && IsFillMain(in arena[child].Style, row))
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
        FrameArena arena, in UiStyle style, int start, int count,
        Vector2 contentOrigin, Vector2 contentSize)
    {
        for (int i = 0; i < count; i++)
        {
            int child = arena.ChildAt(start + i).Index;
            EdgeInsets margin = arena[child].Style.Margin;
            Vector2 measured = arena[child].LogicalSize;
            float spanX = MathF.Max(0f, contentSize.X - margin.Horizontal);
            float spanY = MathF.Max(0f, contentSize.Y - margin.Vertical);
            float width = style.Justify == UiAlign.Stretch ? spanX : measured.X;
            float height = style.Align == UiAlign.Stretch ? spanY : measured.Y;
            Arrange(
                arena,
                child,
                new Vector2(
                    contentOrigin.X + margin.Left + Offset(style.Justify, spanX, width),
                    contentOrigin.Y + margin.Top + Offset(style.Align, spanY, height)),
                new Vector2(width, height));
        }
    }

    private static bool IsFillMain(in UiStyle style, bool row) =>
        (row ? style.Width.Kind : style.Height.Kind) == UiDimKind.Fill;

    private static float Resolve(in UiDim dim, float content, float available) => dim.Kind switch
    {
        UiDimKind.Fixed => dim.Value,
        UiDimKind.Fill => available,
        _ => content,
    };

    // Stretch has no meaning along the main axis, so it reads as Start.
    private static float Offset(UiAlign align, float available, float used) => align switch
    {
        UiAlign.Center => (available - used) * 0.5f,
        UiAlign.End => available - used,
        _ => 0f,
    };
}
