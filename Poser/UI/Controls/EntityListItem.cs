using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.UI;

namespace Poser.UI.Controls;

/// <summary>
/// Configuration for rendering an entity list item row.
/// Pass this to EntityListItem.Draw() to render a consistent row.
/// </summary>
public struct EntityListItemConfig
{
    /// <summary>Unique ID for ImGui elements.</summary>
    public required string Id { get; init; }

    /// <summary>Display name shown in the row.</summary>
    public required string Name { get; init; }

    /// <summary>Icon to display.</summary>
    public required FontAwesomeIcon Icon { get; init; }

    /// <summary>Color for the icon.</summary>
    public Vector4 IconColor { get; init; } = UIConstants.DefaultIconColor;

    /// <summary>Tree depth for indentation.</summary>
    public int Depth { get; init; } = 0;

    /// <summary>Whether this item is currently selected.</summary>
    public bool IsSelected { get; init; } = false;

    /// <summary>Whether this item can be collapsed (has children).</summary>
    public bool IsCollapsible { get; init; } = false;

    /// <summary>Whether this item is currently collapsed.</summary>
    public bool IsCollapsed { get; init; } = false;

    /// <summary>Whether to show the freeze checkbox.</summary>
    public bool ShowFreezeCheckbox { get; init; } = false;

    /// <summary>Current freeze state (only used if ShowFreezeCheckbox is true).</summary>
    public bool IsFrozen { get; init; } = false;

    /// <summary>Whether to show the visibility checkbox.</summary>
    public bool ShowVisibilityCheckbox { get; init; } = false;

    /// <summary>Current visibility state (only used if ShowVisibilityCheckbox is true).</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Optional tooltip for the name.</summary>
    public string? Tooltip { get; init; } = null;

    /// <summary>Optional text color override.</summary>
    public Vector4? TextColor { get; init; } = null;

    public EntityListItemConfig() { }
}

/// <summary>
/// Result of rendering an entity list item - contains user interactions.
/// </summary>
public struct EntityListItemResult
{
    /// <summary>Whether the name was clicked.</summary>
    public bool Clicked { get; init; }

    /// <summary>Whether the collapse button was clicked.</summary>
    public bool CollapseToggled { get; init; }

    /// <summary>Whether the freeze checkbox was toggled.</summary>
    public bool FreezeToggled { get; init; }

    /// <summary>New freeze value if toggled.</summary>
    public bool NewFreezeValue { get; init; }

    /// <summary>Whether the visibility checkbox was toggled.</summary>
    public bool VisibilityToggled { get; init; }

    /// <summary>New visibility value if toggled.</summary>
    public bool NewVisibilityValue { get; init; }

    /// <summary>Whether Ctrl was held during click.</summary>
    public bool CtrlHeld { get; init; }

    /// <summary>Whether Shift was held during click.</summary>
    public bool ShiftHeld { get; init; }
}

/// <summary>
/// Reusable UI component for rendering entity list rows.
/// Works with actors, bones, categories - anything that can be represented as a tree item.
/// </summary>
public static class EntityListItem
{
    /// <summary>
    /// Draws a single entity list item row. Call within a table context.
    /// Uses UIColors for consistent selection highlighting.
    /// </summary>
    public static EntityListItemResult Draw(EntityListItemConfig config)
    {
        var result = new EntityListItemResult();

        // Use UIColors for selection colors
        var tabHovered = UIColors.SelectionHovered;
        var tabActive = UIColors.SelectionActive;
        var selectedHoverColor = UIColors.SelectionActiveHovered;

        ImGui.TableNextRow();
        float rowHeight = ImGui.GetFrameHeight();

        // Column 1: Name (with collapse button, icon, indentation)
        ImGui.TableNextColumn();

        // Get row screen position for background drawing
        var rowMinY = ImGui.GetCursorScreenPos().Y;
        var windowPos = ImGui.GetWindowPos();
        var contentMin = ImGui.GetWindowContentRegionMin();
        var contentMax = ImGui.GetWindowContentRegionMax();
        var rowMin = new Vector2(windowPos.X + contentMin.X, rowMinY);
        var rowMax = new Vector2(windowPos.X + contentMax.X, rowMinY + rowHeight);

        // Check hover state FIRST for background color
        bool rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax);

        // Draw row background using TableSetBgColor (proper table row coloring)
        if (config.IsSelected && rowHovered)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(selectedHoverColor));
        }
        else if (config.IsSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
        }
        else if (rowHovered)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
        }

        float buttonSize = ImGui.GetFrameHeight();
        float edgeMargin = 8f * ImGuiHelpers.GlobalScale;

        // Add left margin before content
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + edgeMargin);
        ImPoser.ApplyTreeIndentation(config.Depth);

        // Collapse/expand button or dot
        bool collapseClicked = false;
        if (config.IsCollapsible)
        {
            var arrowIcon = config.IsCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
            if (ImPoser.IconButton($"collapse_{config.Id}", arrowIcon, new Vector2(buttonSize, buttonSize)))
            {
                collapseClicked = true;
            }
        }
        else
        {
            // Non-collapsible: show dot for consistent sizing
            ImPoser.TextOverIconButton($"dot_{config.Id}", FontAwesomeIcon.CaretDown, "·", new Vector2(buttonSize, buttonSize));
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();

        // Icon
        ImPoser.FontIcon(config.Icon, config.IconColor);

        ImGui.SameLine();

        // Name text
        if (config.TextColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, config.TextColor.Value);
        }

        ImGui.Text(config.Name);

        if (config.TextColor.HasValue)
        {
            ImGui.PopStyleColor();
        }

        // Column 2: Freeze checkbox (always show, disabled when not applicable)
        ImGui.TableNextColumn();
        bool freezeToggled = false;
        bool newFreezeValue = config.IsFrozen;
        {
            bool frozen = config.IsFrozen;
            bool enabled = config.ShowFreezeCheckbox;
            if (DrawCenteredCheckbox($"##freeze_{config.Id}", ref frozen, rowHeight, 0, 0, enabled))
            {
                freezeToggled = true;
                newFreezeValue = frozen;
            }
        }

        // Column 3: Visibility checkbox (always show, disabled when not applicable)
        ImGui.TableNextColumn();
        bool visibilityToggled = false;
        bool newVisibilityValue = config.IsVisible;
        {
            bool visible = config.IsVisible;
            bool enabled = config.ShowVisibilityCheckbox;
            if (DrawCenteredCheckbox($"##vis_{config.Id}", ref visible, rowHeight, 0, edgeMargin, enabled))
            {
                visibilityToggled = true;
                newVisibilityValue = visible;
            }
        }

        // Tooltip
        if (config.Tooltip != null && rowHovered)
        {
            ImGui.SetTooltip(config.Tooltip);
        }

        // Check for row click
        bool rowClicked = rowHovered &&
                          ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                          !collapseClicked &&
                          !freezeToggled &&
                          !visibilityToggled;

        // Determine final result
        if (collapseClicked)
        {
            result = result with { CollapseToggled = true };
        }

        if (rowClicked)
        {
            var io = ImGui.GetIO();
            result = result with
            {
                Clicked = true,
                CtrlHeld = io.KeyCtrl,
                ShiftHeld = io.KeyShift
            };
        }

        if (freezeToggled)
        {
            result = result with
            {
                FreezeToggled = true,
                NewFreezeValue = newFreezeValue
            };
        }

        if (visibilityToggled)
        {
            result = result with
            {
                VisibilityToggled = true,
                NewVisibilityValue = newVisibilityValue
            };
        }

        return result;
    }

    /// <summary>
    /// Draws a PoserCheckbox centered horizontally and vertically in a table cell.
    /// </summary>
    private static bool DrawCenteredCheckbox(string id, ref bool value, float rowHeight, float leftMargin = 0, float rightMargin = 0, bool enabled = true)
    {
        var checkboxSize = PoserCheckbox.Size;
        var cellWidth = ImGui.GetContentRegionAvail().X - leftMargin - rightMargin;

        var cursorPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(
            cursorPos.X + leftMargin + (cellWidth - checkboxSize) / 2,
            cursorPos.Y + (rowHeight - checkboxSize) / 2));

        // Use 10% alpha when disabled for more transparency
        float alpha = enabled ? 1f : 0.1f;
        return PoserCheckbox.Draw(id, ref value, alpha);
    }
}
