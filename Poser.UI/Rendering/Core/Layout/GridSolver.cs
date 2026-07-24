using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>One grid child: optional explicit 1-based cell + spans + fixed height.</summary>
public struct GridCell
{
    public int? Column;
    public int? Row;
    public int ColumnSpan;
    public int RowSpan;
    /// <summary>px — row min-height contribution; null falls back to the default row height.</summary>
    public float? FixedHeight;
}

/// <summary>
/// Pure CSS-grid solver — the column resolution + occupancy auto-flow extracted
/// verbatim from the v1 <c>Element.RenderGrid</c> (Norvrandt/Layout/Grid.cs).
/// Pixels in, pixels out; no ImGui dependency.
/// </summary>
public static class GridSolver
{
    /// <summary>
    /// Resolves column widths and x offsets. Missing template entries (and an
    /// empty template) behave as Fill. Fixed entries are template units ×
    /// <paramref name="scale"/>; everything else splits the remainder by weight.
    /// </summary>
    public static void SolveColumns(ReadOnlySpan<Sizing> template, float innerWidth, float colGap, float scale,
        Span<float> colWidths, Span<float> colX)
    {
        int cols = colWidths.Length;
        float fixedSum = 0f, weightSum = 0f;
        for (int i = 0; i < cols; i++)
        {
            var s = i < template.Length ? template[i] : Sizing.Fill;
            switch (s.Mode)
            {
                case SizingMode.Fixed: colWidths[i] = s.Value * scale; fixedSum += colWidths[i]; break;
                case SizingMode.Flex:  weightSum += s.Value; break;
                case SizingMode.Fill:  weightSum += 1f; break;
                case SizingMode.Auto:  weightSum += 1f; break;
            }
        }
        float gapsTotal = colGap * (cols - 1);
        float remaining = innerWidth - fixedSum - gapsTotal;
        float perWeight = weightSum > 0f ? remaining / weightSum : 0f;
        for (int i = 0; i < cols; i++)
        {
            var s = i < template.Length ? template[i] : Sizing.Fill;
            if (s.Mode == SizingMode.Flex) colWidths[i] = s.Value * perWeight;
            else if (s.Mode is SizingMode.Fill or SizingMode.Auto) colWidths[i] = perWeight;
        }

        float x = 0f;
        for (int i = 0; i < cols; i++)
        {
            colX[i] = x;
            x += colWidths[i] + colGap;
        }
    }

    /// <summary>
    /// Places cells (explicit cells first, then row-major auto-flow around the
    /// occupied set) and writes rects relative to the content origin. Returns
    /// the total content height (rows + row gaps, no padding).
    /// </summary>
    public static float Solve(ReadOnlySpan<GridCell> cells, ReadOnlySpan<float> colWidths, ReadOnlySpan<float> colX,
        float colGap, float rowGap, float defaultRowHeight, Span<RectF> rects)
    {
        if (rects.Length < cells.Length) throw new ArgumentException("rects span too small", nameof(rects));
        int cols = colWidths.Length;

        var rowHeights = new List<float>();
        var occupied = new HashSet<(int c, int r)>();
        var placed = new (int Col, int Row, int ColSpan, int RowSpan)[cells.Length];

        // explicit cells claim their spots first
        for (int i = 0; i < cells.Length; i++)
        {
            ref readonly var cell = ref cells[i];
            if (!cell.Column.HasValue || !cell.Row.HasValue) continue;
            int c = cell.Column.Value - 1;
            int r = cell.Row.Value - 1;
            int cs = Math.Max(1, cell.ColumnSpan);
            int rs = Math.Max(1, cell.RowSpan);
            MarkOccupied(occupied, c, r, cs, rs);
            EnsureRow(rowHeights, r, cell.FixedHeight ?? defaultRowHeight);
            placed[i] = (c, r, cs, rs);
        }

        // row-major auto-flow with explicit-cell avoidance
        int autoCol = 0, autoRow = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            ref readonly var cell = ref cells[i];
            if (cell.Column.HasValue && cell.Row.HasValue) continue;
            int cs = Math.Max(1, cell.ColumnSpan);
            int rs = Math.Max(1, cell.RowSpan);
            while (true)
            {
                if (autoCol + cs > cols) { autoCol = 0; autoRow++; }
                if (!IsOccupied(occupied, autoCol, autoRow, cs, rs)) break;
                autoCol++;
            }
            MarkOccupied(occupied, autoCol, autoRow, cs, rs);
            EnsureRow(rowHeights, autoRow, cell.FixedHeight ?? defaultRowHeight);
            placed[i] = (autoCol, autoRow, cs, rs);
            autoCol += cs;
        }

        // row y offsets
        var rowY = new float[rowHeights.Count];
        float yAcc = 0f;
        for (int r = 0; r < rowHeights.Count; r++)
        {
            rowY[r] = yAcc;
            yAcc += rowHeights[r] + rowGap;
        }
        float totalHeight = yAcc - (rowHeights.Count > 0 ? rowGap : 0f);

        for (int i = 0; i < cells.Length; i++)
        {
            var p = placed[i];
            float spanWidth = 0f;
            for (int c = p.Col; c < p.Col + p.ColSpan && c < cols; c++) spanWidth += colWidths[c];
            spanWidth += colGap * (p.ColSpan - 1);

            float spanHeight = 0f;
            for (int r = p.Row; r < p.Row + p.RowSpan && r < rowHeights.Count; r++) spanHeight += rowHeights[r];
            spanHeight += rowGap * (p.RowSpan - 1);

            rects[i] = new RectF(p.Col < cols ? colX[p.Col] : 0f, p.Row < rowY.Length ? rowY[p.Row] : 0f, spanWidth, spanHeight);
        }

        return totalHeight;
    }

    private static void MarkOccupied(HashSet<(int c, int r)> set, int c, int r, int cs, int rs)
    {
        for (int i = 0; i < cs; i++)
            for (int j = 0; j < rs; j++)
                set.Add((c + i, r + j));
    }

    private static bool IsOccupied(HashSet<(int c, int r)> set, int c, int r, int cs, int rs)
    {
        for (int i = 0; i < cs; i++)
            for (int j = 0; j < rs; j++)
                if (set.Contains((c + i, r + j))) return true;
        return false;
    }

    private static void EnsureRow(List<float> rowHeights, int row, float minHeight)
    {
        while (rowHeights.Count <= row) rowHeights.Add(0f);
        if (rowHeights[row] < minHeight) rowHeights[row] = minHeight;
    }
}
