using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Poser.UI.Controls;

/// <summary>
/// Static helper for drawing consistent table rows with standardized height and styling.
/// </summary>
public static class TableRow
{
    // Track hover state per row (set during End())
    private static bool _currentRowHovered;
    private static int _currentRowIndex;
    private static bool _currentRowSelected;
    private static Vector4 _hoverColor;
    private static Vector4 _selectedColor;

    /// <summary>
    /// Begins a table row with selection and hover highlighting.
    /// </summary>
    public static void Begin(int index, bool isSelected, Vector4 selectedColor, Vector4 hoverColor)
    {
        _currentRowIndex = index;
        _currentRowSelected = isSelected;
        _currentRowHovered = false;
        _selectedColor = selectedColor;
        _hoverColor = hoverColor;

        ImGui.TableNextRow();
        ImGui.PushID(index);

        // Apply selection highlight immediately
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(selectedColor));
        }
    }

    /// <summary>
    /// Draws an icon column with optional color.
    /// Returns true if the icon cell was clicked.
    /// </summary>
    public static bool IconColumn(FontAwesomeIcon icon, Vector4? color = null, string? tooltip = null)
    {
        ImGui.TableSetColumnIndex(0);
        var cellStart = ImGui.GetCursorScreenPos();

        ImPoser.CenterIconInCell(icon, color, tooltip);

        // Invisible button for click detection
        ImGui.SetCursorScreenPos(cellStart);
        bool clicked = ImGui.InvisibleButton($"##icon_{_currentRowIndex}", new Vector2(UIConstants.ScaledRowHeight, UIConstants.ScaledRowHeight));

        if (ImGui.IsItemHovered())
            _currentRowHovered = true;

        return clicked;
    }

    /// <summary>
    /// Draws a text/name column with selectable behavior.
    /// Returns true if the cell was clicked.
    /// </summary>
    public static bool TextColumn(string text, int columnIndex = 1)
    {
        ImGui.TableSetColumnIndex(columnIndex);
        ImPoser.VerticalCenterText();

        var style = ImGui.GetStyle();
        bool clicked = ImGui.Selectable(
            $"{text}##row_{_currentRowIndex}",
            _currentRowSelected,
            ImGuiSelectableFlags.None,
            new Vector2(ImGui.GetContentRegionAvail().X, UIConstants.ScaledRowHeight - style.CellPadding.Y * 2));

        if (ImGui.IsItemHovered())
            _currentRowHovered = true;

        return clicked;
    }

    /// <summary>
    /// Draws a centered checkbox column.
    /// </summary>
    public static bool CheckboxColumn(string id, ref bool value, int columnIndex)
    {
        ImGui.TableSetColumnIndex(columnIndex);
        bool changed = ImPoser.DrawCenteredCheckbox($"##{id}_{_currentRowIndex}", ref value);

        if (ImGui.IsItemHovered())
            _currentRowHovered = true;

        return changed;
    }

    /// <summary>
    /// Ends the table row, applying hover highlight if needed.
    /// Returns true if the row was hovered.
    /// </summary>
    public static bool End()
    {
        // Apply hover highlight at the end (only if not selected)
        if (_currentRowHovered && !_currentRowSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(_hoverColor));
        }

        ImGui.PopID();
        return _currentRowHovered;
    }
}
