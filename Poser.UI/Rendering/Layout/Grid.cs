using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

internal static partial class Element
{
    /// <summary>
    /// CSS Grid: column widths from <see cref="ElementStyle.GridTemplateColumns"/>,
    /// row-major auto-flow with explicit-cell preference, span via
    /// <c>GridColumnSpan</c> / <c>GridRowSpan</c>.
    /// </summary>
    private static void RenderGrid(ElementProps props, Action? children, in ElementStyle resolved)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var screenStart = ImGui.GetCursorScreenPos();
        var posStart = ImGui.GetCursorPos();
        var margin = resolved.Margin ?? new Spacing(0);
        var padding = resolved.Padding ?? new Spacing(0);

        screenStart += new Vector2(margin.Left * scale, margin.Top * scale);
        posStart += new Vector2(margin.Left * scale, margin.Top * scale);

        float availWidth = ImGui.GetContentRegionAvail().X;
        float outerWidth = (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            ? resolved.Width.Value.Value * scale
            : availWidth - margin.Horizontal * scale;
        outerWidth = ApplyMinMaxWidth(outerWidth, resolved, scale);

        float innerWidth = outerWidth - padding.Horizontal * scale;
        float colGap = (resolved.GridColumnGap ?? resolved.Gap ?? 0f) * scale;
        float rowGap = (resolved.GridRowGap ?? resolved.Gap ?? 0f) * scale;

        var template = resolved.GridTemplateColumns ?? Array.Empty<Sizing>();
        int cols = Math.Max(template.Length, 1);
        var colWidths = new float[cols];
        var colX = new float[cols];
        GridSolver.SolveColumns(template, innerWidth, colGap, scale, colWidths, colX);

        _gridStack ??= new Stack<GridContext>();
        var ctx = new GridContext { Items = new List<GridItem>() };
        _gridStack.Push(ctx);

        DrawChrome(screenStart, screenStart + new Vector2(outerWidth, 0), resolved);
        bool isPositioningContext = (resolved.Position ?? UI.Position.Static) != UI.Position.Static;
        if (isPositioningContext) PushPositionContext(screenStart, screenStart + new Vector2(outerWidth, 0));

        int pushes = PushCascade(resolved);
        try
        {
            if (children != null)
            {
                float prevW = _ambientWidth, prevH = _ambientHeight;
                _ambientWidth = innerWidth;
                _ambientHeight = 0;
                try { children(); }
                finally { _ambientWidth = prevW; _ambientHeight = prevH; }
            }
        }
        finally { _gridStack.Pop(); PopCascade(pushes); if (isPositioningContext) PopPositionContext(); }

        // placement + span math delegated to the pure GridSolver (Core);
        // behavior-identical — the math moved, not changed.
        var cells = new GridCell[ctx.Items.Count];
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var it = ctx.Items[i];
            cells[i] = new GridCell
            {
                Column = it.GridColumn,
                Row = it.GridRow,
                ColumnSpan = it.ColumnSpan,
                RowSpan = it.RowSpan,
                FixedHeight = it.Height is { Mode: SizingMode.Fixed } fixedH ? fixedH.Value * scale : null,
            };
        }

        var rects = new RectF[cells.Length];
        float totalHeight = GridSolver.Solve(cells, colWidths, colX, colGap, rowGap,
            Norvrandt.Sheet.CurrentTheme.RowHeight * scale, rects);

        for (int i = 0; i < rects.Length; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(
                screenStart.X + padding.Left * scale + rects[i].X,
                screenStart.Y + padding.Top * scale + rects[i].Y));
            ctx.Items[i].Render(rects[i].W, rects[i].H);
        }

        float resolvedHeight = totalHeight + padding.Vertical * scale;
        if (resolved.Height.HasValue && resolved.Height.Value.Mode == SizingMode.Fixed)
            resolvedHeight = resolved.Height.Value.Value * scale;
        resolvedHeight = ApplyMinMaxHeight(resolvedHeight, resolved, scale);

        DrawChrome(screenStart, screenStart + new Vector2(outerWidth, resolvedHeight), resolved);

        float bottomY = posStart.Y + resolvedHeight + margin.Bottom * scale;
        ImGui.SetCursorPos(new Vector2(posStart.X - margin.Left * scale, bottomY));
    }

}
