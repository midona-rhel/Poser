using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

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
    /// </summary>
    public static EntityListItemResult Draw(EntityListItemConfig config, Vector4 tabHovered, Vector4 tabActive)
    {
        var result = new EntityListItemResult();

        // Brighter color for selected+hovered state
        var selectedHoverColor = new Vector4(
            Math.Min(1f, tabActive.X * 1.3f),
            Math.Min(1f, tabActive.Y * 1.3f),
            Math.Min(1f, tabActive.Z * 1.3f),
            tabActive.W);

        ImGui.TableNextRow();
        ImPoser.HighlightRowIfSelected(config.IsSelected, tabActive);

        // Column 1: Name (with collapse button, icon, indentation)
        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();
        ImPoser.ApplyTreeIndentation(config.Depth);

        // Collapse/expand button or dot
        if (config.IsCollapsible)
        {
            var arrowIcon = config.IsCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
            if (ImPoser.IconButton($"collapse_{config.Id}", arrowIcon, new Vector2(buttonSize, buttonSize)))
            {
                result = result with { CollapseToggled = true };
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

        // Name (selectable)
        if (config.TextColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, config.TextColor.Value);
        }

        if (ImPoser.TransparentSelectable($"{config.Name}##{config.Id}", config.IsSelected, tabHovered, selectedHoverColor))
        {
            var io = ImGui.GetIO();
            result = result with
            {
                Clicked = true,
                CtrlHeld = io.KeyCtrl,
                ShiftHeld = io.KeyShift
            };
        }

        if (config.TextColor.HasValue)
        {
            ImGui.PopStyleColor();
        }

        // Tooltip
        if (config.Tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(config.Tooltip);
        }

        // Column 2: Freeze checkbox
        ImGui.TableNextColumn();
        if (config.ShowFreezeCheckbox)
        {
            bool frozen = config.IsFrozen;
            if (ImPoser.DrawCenteredCheckbox($"##freeze_{config.Id}", ref frozen))
            {
                result = result with
                {
                    FreezeToggled = true,
                    NewFreezeValue = frozen
                };
            }
        }

        // Column 3: Visibility checkbox
        ImGui.TableNextColumn();
        if (config.ShowVisibilityCheckbox)
        {
            bool visible = config.IsVisible;
            if (ImPoser.DrawCenteredCheckbox($"##vis_{config.Id}", ref visible))
            {
                result = result with
                {
                    VisibilityToggled = true,
                    NewVisibilityValue = visible
                };
            }
        }

        return result;
    }
}
