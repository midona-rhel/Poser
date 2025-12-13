using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Controls;

/// <summary>
/// Flexbox-inspired layout system for ImGui.
/// All user-facing values are in unscaled pixels; scaling is handled internally.
/// </summary>
public static class Flex
{
    // ========== STANDARDIZED SPACING ==========
    // These constants define ALL spacing in the UI. Change them here to affect everything.

    /// <summary>
    /// Standard row height for controls (buttons, dropdowns, scrubbers).
    /// </summary>
    public const float RowHeight = 24f;

    /// <summary>
    /// Vertical spacing between rows.
    /// </summary>
    public const float RowSpacing = 14f;

    /// <summary>
    /// Standard label width for labeled rows.
    /// </summary>
    public const float LabelWidth = 70f;

    /// <summary>
    /// Horizontal gap between items in a row.
    /// </summary>
    public const float ItemGap = 12f;

    /// <summary>
    /// Small gap for tightly grouped items.
    /// </summary>
    public const float SmallGap = 6f;

    /// <summary>
    /// Standard button width (Reset, etc.).
    /// </summary>
    public const float ButtonWidth = 70f;

    /// <summary>
    /// Large icon size (for IconToggle).
    /// </summary>
    public const float LargeIconSize = 24f;

    /// <summary>
    /// Standard control size (checkbox, toggle).
    /// </summary>
    public const float ControlSize = 18f;

    /// <summary>
    /// Standard text padding inside controls.
    /// </summary>
    public const float TextPadding = 8f;

    /// <summary>
    /// Content padding from container edges.
    /// </summary>
    public const float ContentPadding = 8f;

    /// <summary>
    /// Creates a horizontal flex row.
    /// </summary>
    /// <param name="height">Row height in unscaled pixels (default: Flex.RowHeight).</param>
    /// <param name="gap">Gap between items in unscaled pixels (default: 0).</param>
    /// <param name="width">Optional fixed width (scaled). If null, uses available width.</param>
    public static FlexRow Row(float height = RowHeight, float gap = 0, float? width = null)
    {
        return new FlexRow(height, gap, width);
    }
}

/// <summary>
/// A horizontal flex container that distributes space among its children.
/// Items are added via Fixed/Fill/Flex methods, then drawn on Dispose.
/// </summary>
public sealed class FlexRow : IDisposable
{
    private readonly List<FlexItem> _items = new();
    private readonly float _height;
    private readonly float _width;
    private readonly float _gap;
    private readonly Vector2 _startPos;

    private const float DefaultContentPadding = 8f; // Must match Flex.ContentPadding

    internal FlexRow(float height, float gap, float? width)
    {
        float scale = PoserUI.Scale;
        _height = height * scale;
        _gap = gap * scale;

        var cursorPos = ImGui.GetCursorPos();

        if (width.HasValue)
        {
            // Explicit width provided (nested row) - use cursor position directly, no content padding
            _startPos = cursorPos;
            _width = width.Value;
        }
        else
        {
            // Auto width (top-level row) - apply content padding relative to content region
            float contentPadding = DefaultContentPadding * scale;
            var contentRegionMin = ImGui.GetWindowContentRegionMin();
            float contentRegionWidth = ImGui.GetWindowContentRegionMax().X - contentRegionMin.X;
            _startPos = new Vector2(contentRegionMin.X + contentPadding, cursorPos.Y);
            _width = contentRegionWidth - contentPadding * 2;
        }
    }

    private const float DefaultLabelWidth = 70f; // Must match Flex.LabelWidth
    private const float DefaultRowSpacing = 14f; // Must match Flex.RowSpacing

    /// <summary>
    /// Adds a right-aligned label with standard width and auto vertical centering.
    /// </summary>
    /// <param name="text">Label text.</param>
    /// <param name="width">Width in unscaled pixels (default: 60).</param>
    public FlexRow Label(string text, float width = DefaultLabelWidth)
    {
        return Fixed(width, (w, h) =>
        {
            // Vertical centering
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            // Right-align within the label width
            float textWidth = ImGui.CalcTextSize(text).X;
            float offsetX = w - textWidth;
            if (offsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

            ImGui.Text(text);
        });
    }

    /// <summary>
    /// Adds a fixed-width item.
    /// </summary>
    /// <param name="width">Width in unscaled pixels.</param>
    /// <param name="draw">Draw callback.</param>
    public FlexRow Fixed(float width, Action draw)
    {
        return Fixed(width, (_, _) => draw());
    }

    /// <summary>
    /// Adds a fixed-width item.
    /// </summary>
    /// <param name="width">Width in unscaled pixels.</param>
    /// <param name="draw">Draw callback that receives (width, height) for centering.</param>
    public FlexRow Fixed(float width, Action<float, float> draw)
    {
        _items.Add(new FlexItem
        {
            IsFixed = true,
            Size = width * PoserUI.Scale,
            Weight = 0,
            Draw = draw
        });
        return this;
    }

    /// <summary>
    /// Adds a fill item that takes equal share of remaining space.
    /// </summary>
    /// <param name="draw">Draw callback.</param>
    public FlexRow Fill(Action draw)
    {
        return Flex(1, (_, _) => draw());
    }

    /// <summary>
    /// Adds a fill item that takes equal share of remaining space.
    /// </summary>
    /// <param name="draw">Draw callback that receives the computed width (scaled).</param>
    public FlexRow Fill(Action<float> draw)
    {
        return Flex(1, (w, _) => draw(w));
    }

    /// <summary>
    /// Adds a fill item that takes equal share of remaining space.
    /// </summary>
    /// <param name="draw">Draw callback that receives (width, height) for centering.</param>
    public FlexRow Fill(Action<float, float> draw)
    {
        return Flex(1, draw);
    }

    /// <summary>
    /// Adds a weighted flex item.
    /// </summary>
    /// <param name="weight">Flex weight (like CSS flex: n).</param>
    /// <param name="draw">Draw callback.</param>
    public FlexRow Flex(float weight, Action draw)
    {
        return Flex(weight, (_, _) => draw());
    }

    /// <summary>
    /// Adds a weighted flex item.
    /// </summary>
    /// <param name="weight">Flex weight (like CSS flex: n).</param>
    /// <param name="draw">Draw callback that receives the computed width (scaled).</param>
    public FlexRow Flex(float weight, Action<float> draw)
    {
        return Flex(weight, (w, _) => draw(w));
    }

    /// <summary>
    /// Adds a weighted flex item.
    /// </summary>
    /// <param name="weight">Flex weight (like CSS flex: n).</param>
    /// <param name="draw">Draw callback that receives (width, height) for centering.</param>
    public FlexRow Flex(float weight, Action<float, float> draw)
    {
        _items.Add(new FlexItem
        {
            IsFixed = false,
            Size = 0,
            Weight = weight,
            Draw = draw
        });
        return this;
    }

    /// <summary>
    /// Adds a vertically centered text element.
    /// </summary>
    /// <param name="text">Text to display.</param>
    /// <param name="width">Optional fixed width in unscaled pixels. If null, uses text width.</param>
    public FlexRow Text(string text, float? width = null)
    {
        float textWidth = width ?? (ImGui.CalcTextSize(text).X / PoserUI.Scale);
        return Fixed(textWidth, (w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
            ImGui.Text(text);
        });
    }

    /// <summary>
    /// Adds a vertically centered checkbox.
    /// </summary>
    /// <param name="id">Unique ID for the checkbox.</param>
    /// <param name="value">Current checked state (ref).</param>
    /// <param name="onChanged">Optional callback when value changes.</param>
    public FlexRow Checkbox(string id, ref bool value, Action? onChanged = null)
    {
        bool localValue = value;
        bool valueRef = value;
        return Fixed(PoserCheckbox.Size / PoserUI.Scale, (w, h) =>
        {
            float offsetY = (h - PoserCheckbox.Size) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
            if (PoserCheckbox.Draw(id, ref localValue))
            {
                onChanged?.Invoke();
            }
        });
    }

    /// <summary>
    /// Adds a vertically centered icon toggle.
    /// </summary>
    /// <param name="id">Unique ID for the toggle.</param>
    /// <param name="value">Current toggle state (ref).</param>
    /// <param name="icon">FontAwesome icon to display.</param>
    /// <param name="tooltip">Optional tooltip text.</param>
    /// <param name="onChanged">Optional callback when value changes.</param>
    public FlexRow IconToggle(string id, ref bool value, Dalamud.Interface.FontAwesomeIcon icon, string? tooltip = null, Action? onChanged = null)
    {
        bool localValue = value;
        return Fixed(Controls.IconToggle.Size / PoserUI.Scale, (w, h) =>
        {
            float offsetY = (h - Controls.IconToggle.Size) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
            if (Controls.IconToggle.Draw(id, ref localValue, icon, tooltip))
            {
                onChanged?.Invoke();
            }
        });
    }

    /// <summary>
    /// Adds an empty spacer that pushes subsequent items to the right.
    /// </summary>
    public FlexRow Spacer()
    {
        return Fill(() => { });
    }

    public void Dispose()
    {
        if (_items.Count == 0)
        {
            AdvanceCursor();
            return;
        }

        // Phase 1: Calculate sizes
        float totalFixed = 0;
        float totalWeight = 0;

        foreach (var item in _items)
        {
            if (item.IsFixed)
                totalFixed += item.Size;
            else
                totalWeight += item.Weight;
        }

        float totalGaps = _gap * (_items.Count - 1);
        float remaining = _width - totalFixed - totalGaps;
        float perWeight = totalWeight > 0 ? remaining / totalWeight : 0;

        // Phase 2: Draw items
        float x = _startPos.X;
        float y = _startPos.Y;

        foreach (var item in _items)
        {
            float w = item.IsFixed ? item.Size : item.Weight * perWeight;

            ImGui.SetCursorPos(new Vector2(x, y));
            item.Draw(w, _height);

            x += w + _gap;
        }

        AdvanceCursor();
    }

    private void AdvanceCursor()
    {
        float scale = PoserUI.Scale;
        float nextY = _startPos.Y + _height + (DefaultRowSpacing * scale);
        ImGui.SetCursorPos(new Vector2(_startPos.X, nextY));
    }

    private struct FlexItem
    {
        public bool IsFixed;
        public float Size;
        public float Weight;
        public Action<float, float> Draw;
    }
}

/// <summary>
/// Layout helpers for consistent alignment in ImGui.
/// </summary>
public static class Layout
{
    /// <summary>
    /// Gets remaining width in current region.
    /// </summary>
    public static float RemainingWidth => ImGui.GetContentRegionAvail().X;

    /// <summary>
    /// Gets remaining height in current region.
    /// </summary>
    public static float RemainingHeight => ImGui.GetContentRegionAvail().Y;

    /// <summary>
    /// Calculates width for N equal columns with spacing.
    /// </summary>
    public static float ColumnWidth(int columnCount, float totalWidth = -1)
    {
        if (totalWidth < 0) totalWidth = RemainingWidth;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        return (totalWidth - spacing * (columnCount - 1)) / columnCount;
    }

    /// <summary>
    /// Sets cursor to center an item of given width.
    /// </summary>
    public static void CenterHorizontally(float itemWidth)
    {
        float offset = (RemainingWidth - itemWidth) / 2;
        if (offset > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
    }

    /// <summary>
    /// Sets cursor to center an item vertically within given height.
    /// </summary>
    public static void CenterVertically(float itemHeight, float containerHeight)
    {
        float offset = (containerHeight - itemHeight) / 2;
        if (offset > 0)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
    }

    /// <summary>
    /// Right-aligns cursor for an item of given width.
    /// </summary>
    public static void AlignRight(float itemWidth)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RemainingWidth - itemWidth);
    }
}
