using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

/// <summary>
/// Static helper methods for common UI patterns.
/// Uses aggressive inlining for frequently-called methods.
/// </summary>
public static class ImPoser
{
    #region Layout Utilities

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetLineHeight()
    {
        return ImGui.GetTextLineHeight() + (ImGui.GetStyle().FramePadding.Y * 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetRemainingWidth()
    {
        return ImGui.GetContentRegionAvail().X;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RightAlign(float width)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (GetRemainingWidth() - width));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CenterInCell(float contentWidth)
    {
        var cellWidth = ImGui.GetContentRegionAvail().X;
        var offset = MathF.Max(0, (cellWidth - contentWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VerticalCenter(float contentHeight, float cellHeight)
    {
        var offset = MathF.Max(0, (cellHeight - contentHeight) * 0.5f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VerticalCenterText()
    {
        var style = ImGui.GetStyle();
        var cursorPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosY(cursorPos.Y + style.CellPadding.Y / 2);
    }

    #endregion

    #region Icons

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FontIcon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (color.HasValue)
                ImGui.TextColored(color.Value, icon.ToIconString());
            else
                ImGui.Text(icon.ToIconString());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FontIconDisabled(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextDisabled(icon.ToIconString());
        }
    }

    public static void CenterIconInCell(FontAwesomeIcon icon, Vector4? color = null, string? tooltip = null)
    {
        var iconStr = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconStr);
            var cellSize = new Vector2(ImGui.GetContentRegionAvail().X, UIConstants.ScaledRowHeight);

            var cursorPos = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(
                cursorPos.X + (cellSize.X - iconSize.X) / 2,
                cursorPos.Y + (cellSize.Y - iconSize.Y) / 2));

            if (color.HasValue)
                ImGui.TextColored(color.Value, iconStr);
            else
                ImGui.TextDisabled(iconStr);
        }

        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    #endregion

    #region Buttons

    public static bool FontIconButton(string id, FontAwesomeIcon icon, Vector2? size = null, string? tooltip = null, bool enabled = true)
    {
        bool clicked = false;
        var buttonSize = size ?? new Vector2(UIConstants.ScaledButtonSize);

        using (ImRaii.Disabled(!enabled))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                clicked = ImGui.Button($"{icon.ToIconString()}##{id}", buttonSize);
            }
        }

        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked && enabled;
    }

    public static bool FontIconButtonRight(string id, FontAwesomeIcon icon, float position, string? tooltip = null, bool enabled = true)
    {
        var size = new Vector2(UIConstants.ScaledButtonSize);
        var pixelPos = ImGui.GetWindowSize().X - ((ImGui.CalcTextSize("XXII").X + (ImGui.GetStyle().FramePadding.X * 2)) * position);

        ImGui.SetCursorPosX(pixelPos);

        return FontIconButton(id, icon, size, tooltip, enabled);
    }

    #endregion

    #region Checkboxes

    public static bool DrawCenteredCheckbox(string id, ref bool value)
    {
        var checkboxSize = ImGui.GetFrameHeight();
        var cellWidth = ImGui.GetContentRegionAvail().X;

        var cursorPos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(
            cursorPos.X + (cellWidth - checkboxSize) / 2,
            cursorPos.Y));

        return ImGui.Checkbox(id, ref value);
    }

    #endregion

    #region Tooltips

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AttachTooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(text);
        }
    }

    #endregion

    #region Style Helpers

    /// <summary>
    /// Gets the slightly brighter background color for table rows.
    /// </summary>
    public static Vector4 GetBrighterTableBg()
    {
        var style = ImGui.GetStyle();
        var tableBgColor = style.Colors[(int)ImGuiCol.TableRowBg];
        return new Vector4(
            MathF.Min(tableBgColor.X + 0.05f, 1f),
            MathF.Min(tableBgColor.Y + 0.05f, 1f),
            MathF.Min(tableBgColor.Z + 0.05f, 1f),
            tableBgColor.W);
    }

    /// <summary>
    /// Gets the tab hovered color for row highlighting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 GetTabHoveredColor()
    {
        return ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered];
    }

    /// <summary>
    /// Gets the tab active color for selection highlighting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 GetTabActiveColor()
    {
        return ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive];
    }

    #endregion
}
