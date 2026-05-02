using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Poser.UI.Controls;

/// <summary>
/// Standardized collapsible section header with optional action button.
/// </summary>
public static class CollapsibleSection
{
    /// <summary>
    /// Draws a collapsible section header with optional count and action button.
    /// </summary>
    /// <param name="label">The section label</param>
    /// <param name="count">Optional item count to display</param>
    /// <param name="actionIcon">Optional icon for action button</param>
    /// <param name="onAction">Callback when action button is clicked</param>
    /// <param name="actionTooltip">Tooltip for action button</param>
    /// <param name="defaultOpen">Whether section is open by default</param>
    /// <returns>True if section is expanded</returns>
    public static bool Draw(
        string label,
        int? count = null,
        FontAwesomeIcon? actionIcon = null,
        Action? onAction = null,
        string? actionTooltip = null,
        bool defaultOpen = true)
    {
        var style = ImGui.GetStyle();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Draw background for header
        var cursorPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var brighterBg = ImPoser.GetBrighterTableBg();

        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + availWidth, cursorPos.Y + UIConstants.ScaledRowHeight),
            ImGui.GetColorU32(brighterBg));

        // Build label with count
        var displayLabel = count.HasValue ? $"{label} ({count.Value})" : label;

        // Tree node flags
        var headerFlags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (defaultOpen)
            headerFlags |= ImGuiTreeNodeFlags.DefaultOpen;

        // Draw tree node
        bool isOpen = ImGui.TreeNodeEx($"{displayLabel}###{label}_header", headerFlags);

        // Draw action button on the right (if provided)
        if (actionIcon.HasValue && onAction != null)
        {
            ImGui.SameLine(availWidth - UIConstants.ScaledButtonSize - style.ItemSpacing.X);
            if (ImPoser.FontIconButton($"{label}_action", actionIcon.Value, null, actionTooltip))
            {
                onAction();
            }
        }

        if (isOpen)
        {
            ImGui.TreePop();
        }

        return isOpen;
    }

    /// <summary>
    /// Draws a simple collapsing header (ImGui built-in style).
    /// </summary>
    public static bool DrawSimple(string label, bool defaultOpen = true)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        return ImGui.CollapsingHeader($"{label}###{label}_header", flags);
    }
}
