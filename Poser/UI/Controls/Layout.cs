using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

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

/// <summary>
/// Flexbox-like row layout: fixed label column + N equal field columns.
/// </summary>
public readonly struct FlexRow : IDisposable
{
    public readonly float LabelWidth;
    public readonly float FieldWidth;
    public readonly float Spacing;
    public readonly int FieldCount;

    public FlexRow(float labelWidth, int fieldCount)
    {
        LabelWidth = labelWidth * ImGuiHelpers.GlobalScale;
        FieldCount = fieldCount;
        Spacing = ImGui.GetStyle().ItemSpacing.X;

        float available = Layout.RemainingWidth;
        float totalSpacing = Spacing * fieldCount; // after label + between fields
        FieldWidth = (available - LabelWidth - totalSpacing) / fieldCount;
    }

    /// <summary>
    /// Draws centered icon in label column, advances to first field.
    /// </summary>
    public void DrawIconLabel(FontAwesomeIcon icon, Vector4? color = null)
    {
        float frameHeight = ImGui.GetFrameHeight();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconStr = icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconStr);

            // Center in label column
            float offsetX = (LabelWidth - iconSize.X) / 2;
            float offsetY = (frameHeight - iconSize.Y) / 2;

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            if (color.HasValue)
                ImGui.TextColored(color.Value, iconStr);
            else
                ImGui.TextDisabled(iconStr);

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - offsetY);
        }
        ImGui.SameLine(LabelWidth + Spacing);
    }

    /// <summary>
    /// Sets up next field. Call before each input widget.
    /// </summary>
    public void NextField(int index)
    {
        if (index > 0) ImGui.SameLine();
        ImGui.SetNextItemWidth(FieldWidth);
    }

    public void Dispose() { }
}

/// <summary>
/// Vertical flex column layout.
/// </summary>
public readonly struct FlexColumn : IDisposable
{
    public readonly float RowHeight;
    public readonly float Spacing;

    public FlexColumn(float rowHeight = -1)
    {
        RowHeight = rowHeight < 0 ? ImGui.GetFrameHeight() : rowHeight * ImGuiHelpers.GlobalScale;
        Spacing = ImGui.GetStyle().ItemSpacing.Y;
    }

    /// <summary>
    /// Reserves space for a row and returns if content should be drawn.
    /// </summary>
    public bool NextRow()
    {
        // Just advances cursor, could add clipping logic later
        return true;
    }

    public void Dispose() { }
}
