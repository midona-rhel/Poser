using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Thread-static layout state shared by Row, Column, Grid, Absolute, and Inline
/// renderers. All fields and helpers here are <c>internal</c> partials of
/// <see cref="Element"/> so the layout files can read/write them directly.
/// </summary>
internal static partial class Element
{
    [ThreadStatic]
    private static Stack<RowContext>? _rowStack;

    [ThreadStatic]
    private static Stack<GridContext>? _gridStack;

    [ThreadStatic]
    private static Stack<PositionContext>? _positionStack;

    /// <summary>Ambient inner width of the current parent (for AvailableWidth queries).</summary>
    [ThreadStatic]
    internal static float _ambientWidth;
    /// <summary>Ambient inner height of the current parent.</summary>
    [ThreadStatic]
    internal static float _ambientHeight;

    private struct RowContext
    {
        public List<RowItem> Items;
    }

    private struct RowItem
    {
        public Sizing Width;
        public Sizing? Height;
        public AlignSelf? AlignSelf;
        public Action<float, float> Render;
    }

    private struct GridContext
    {
        public List<GridItem> Items;
    }

    private struct GridItem
    {
        public int? GridColumn;
        public int? GridRow;
        public int  ColumnSpan;
        public int  RowSpan;
        public Sizing? Height;
        public Action<float, float> Render;
    }

    private struct PositionContext
    {
        public Vector2 ScreenMin;
        public Vector2 ScreenMax;
    }

    private static void PushPositionContext(Vector2 min, Vector2 max)
    {
        _positionStack ??= new Stack<PositionContext>();
        _positionStack.Push(new PositionContext { ScreenMin = min, ScreenMax = max });
    }

    private static void PopPositionContext()
    {
        if (_positionStack is { Count: > 0 }) _positionStack.Pop();
    }

    private static bool TryPeekPositionContext(out Vector2 min, out Vector2 max)
    {
        if (_positionStack is { Count: > 0 })
        {
            var ctx = _positionStack.Peek();
            min = ctx.ScreenMin;
            max = ctx.ScreenMax;
            return true;
        }
        min = max = Vector2.Zero;
        return false;
    }

    // ---- Public layout-context queries (used by widgets that aren't full Element renders) ----

    /// <summary>True when the current draw call is inside a flex row's children lambda.</summary>
    public static bool IsInRow => _rowStack is { Count: > 0 };

    /// <summary>
    /// Register a deferred row child from a widget that normally manages its own ImGui
    /// cursor. Used by <see cref="Crystarium.Text"/> and other tags so they participate
    /// in flex layout when called inside a row body instead of falling through ImGui's
    /// vertical auto-flow.
    /// </summary>
    public static void RegisterRowItem(Sizing width, Sizing? height, AlignSelf? align, Action<float, float> render)
    {
        if (_rowStack is { Count: > 0 } stack)
            stack.Peek().Items.Add(new RowItem { Width = width, Height = height, AlignSelf = align, Render = render });
    }
}
