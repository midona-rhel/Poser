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

    /// <summary>
    /// Applies tree-view indentation by inserting invisible spacers.
    /// Each level = button size + half item spacing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyTreeIndentation(int depth)
    {
        if (depth <= 0) return;

        float buttonSize = ImGui.GetFrameHeight();
        float halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float indentWidth = buttonSize + halfSpacing;

        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(indentWidth, buttonSize));
            ImGui.SameLine(0, 0);
        }
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

    /// <summary>
    /// Simple icon button without outline. Good for collapse/expand arrows.
    /// </summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, Vector2? size = null, string? tooltip = null)
    {
        bool clicked;
        var buttonSize = size ?? new Vector2(UIConstants.ScaledButtonSize);

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered] with { W = 0.3f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] with { W = 0.5f }))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", buttonSize);
        }

        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    public static bool FontIconButton(string id, FontAwesomeIcon icon, Vector2? size = null, string? tooltip = null, bool enabled = true)
    {
        bool clicked = false;
        var buttonSize = size ?? new Vector2(UIConstants.ScaledButtonSize);
        var startPos = ImGui.GetCursorScreenPos();

        using (ImRaii.Disabled(!enabled))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                clicked = ImGui.Button($"{icon.ToIconString()}##{id}", buttonSize);
            }
        }

        // Draw black outline (1 pixel by scale)
        var drawList = ImGui.GetWindowDrawList();
        var outlineThickness = 1f * ImGuiHelpers.GlobalScale;
        drawList.AddRect(startPos, startPos + buttonSize, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), 0, ImDrawFlags.None, outlineThickness);

        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked && enabled;
    }

    /// <summary>
    /// Button with visually centered FontAwesome icon.
    /// Uses Button + manual icon draw for perfect centering.
    /// </summary>
    public static bool CenteredIconButton(string id, FontAwesomeIcon icon, Vector2? size = null,
        string? tooltip = null, bool enabled = true)
    {
        var buttonSize = size ?? new Vector2(UIConstants.ScaledButtonSize);
        bool clicked = false;

        using (ImRaii.PushId(id))
        using (ImRaii.Disabled(!enabled))
        {
            var startPos = ImGui.GetCursorScreenPos();

            // Draw button background (empty label)
            clicked = ImGui.Button("##btn", buttonSize);

            var drawList = ImGui.GetWindowDrawList();

            // Calculate icon size and center position
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var iconStr = icon.ToIconString();
                var iconSize = ImGui.CalcTextSize(iconStr);

                var iconPos = new Vector2(
                    startPos.X + (buttonSize.X - iconSize.X) / 2,
                    startPos.Y + (buttonSize.Y - iconSize.Y) / 2);

                var textColor = enabled
                    ? ImGui.GetColorU32(ImGuiCol.Text)
                    : ImGui.GetColorU32(ImGuiCol.TextDisabled);
                drawList.AddText(iconPos, textColor, iconStr);
            }

            // Draw black outline (1 pixel by scale)
            var outlineThickness = 1f * ImGuiHelpers.GlobalScale;
            drawList.AddRect(startPos, startPos + buttonSize, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), 0, ImDrawFlags.None, outlineThickness);
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

    /// <summary>
    /// Button that uses a FontAwesome icon for sizing but displays text centered on top.
    /// The icon is invisible (0% opacity) and only used to ensure consistent button sizing.
    /// </summary>
    public static bool TextOverIconButton(string id, FontAwesomeIcon sizeIcon, string text, Vector2? size = null, string? tooltip = null)
    {
        bool clicked;
        var buttonSize = size ?? new Vector2(UIConstants.ScaledButtonSize);
        var startPos = ImGui.GetCursorScreenPos();

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered] with { W = 0.3f }))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] with { W = 0.5f }))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            // Draw invisible icon button for sizing
            using (ImRaii.PushColor(ImGuiCol.Text, Vector4.Zero))
            {
                clicked = ImGui.Button($"{sizeIcon.ToIconString()}##{id}", buttonSize);
            }
        }

        // Draw text centered on top
        if (!string.IsNullOrEmpty(text))
        {
            var drawList = ImGui.GetWindowDrawList();
            var textSize = ImGui.CalcTextSize(text);
            var textPos = new Vector2(
                startPos.X + (buttonSize.X - textSize.X) / 2,
                startPos.Y + (buttonSize.Y - textSize.Y) / 2);
            var textColor = ImGui.GetColorU32(UIConstants.DisabledTextColor);
            drawList.AddText(textPos, textColor, text);
        }

        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    /// <summary>
    /// Invisible dummy spacer that matches the size of an icon button.
    /// Not clickable, not hoverable - purely for layout/indentation.
    /// </summary>
    public static void InvisibleButtonSpacer(Vector2? size = null)
    {
        var spacerSize = size ?? new Vector2(ImGui.GetFrameHeight());
        ImGui.Dummy(spacerSize);
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

    #region Toggle Controls

    /// <summary>
    /// Toggle button with lock icon (like Brio's ToggleLock).
    /// Returns (toggleClicked, lockClicked).
    /// </summary>
    public static (bool toggleClicked, bool lockClicked) ToggleLock(
        string label,
        float width,
        ref bool enabled,
        ref bool locked,
        bool disableOnLock = false)
    {
        bool toggleClicked = false;
        bool lockClicked = false;

        using (ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(ImGuiCol.Tab)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, ImGui.GetStyle().FrameRounding))
        {
            var height = 25 * ImGuiHelpers.GlobalScale;
            using var child = ImRaii.Child($"###{label}_togglelock", new Vector2(width - 2f, height), false, ImGuiWindowFlags.NoScrollbar);

            if (child.Success)
            {
                // Toggle button
                using (ImRaii.Disabled(locked && disableOnLock))
                {
                    if (ToggleButton($"{label}###toggle", new Vector2(53, 25), enabled))
                    {
                        toggleClicked = true;
                        enabled = !enabled;
                    }
                }

                ImGui.SameLine();

                // Lock button
                using (ImRaii.Disabled(!enabled))
                {
                    var lockIcon = locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock;
                    var lockTooltip = locked ? "Unlock" : "Lock";
                    if (IconButton($"{label}_lock", lockIcon, null, lockTooltip))
                    {
                        lockClicked = true;
                        locked = !locked;
                    }
                }
            }
        }

        return (toggleClicked, lockClicked);
    }

    /// <summary>
    /// Toggle button that changes color when active.
    /// </summary>
    public static bool ToggleButton(string label, Vector2 size, bool isActive)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(isActive ? ImGuiCol.TabActive : ImGuiCol.Tab)))
        {
            return ImGui.Button(label, size);
        }
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

    #region Form Layout

    /// <summary>
    /// Draws a label and positions cursor for the control.
    /// Call this, then draw your control on the same line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Label(string text, float labelWidth)
    {
        ImGui.Text(text);
        ImGui.SameLine(labelWidth);
    }

    /// <summary>
    /// Draws a section header (disabled text with spacing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SectionHeader(string text)
    {
        ImGui.TextDisabled(text);
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws a section separator with header.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SectionSeparator(string? headerText = null)
    {
        ImGui.Spacing();
        ImGui.Separator();
        if (headerText != null)
        {
            ImGui.TextDisabled(headerText);
            ImGui.Spacing();
        }
    }

    #endregion

    #region Table Row Helpers

    /// <summary>
    /// Sets table row background color if the row is selected.
    /// Call immediately after TableNextRow().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HighlightRowIfSelected(bool isSelected, Vector4 activeColor)
    {
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(activeColor));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(activeColor));
        }
    }

    /// <summary>
    /// Sets table row background color for hover effect.
    /// Call after checking ImGui.IsItemHovered().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HighlightRowOnHover(Vector4 hoverColor)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(hoverColor));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(hoverColor));
        }
    }

    /// <summary>
    /// Draws a transparent selectable (no header colors) with hover highlighting.
    /// Returns true if clicked.
    /// </summary>
    public static bool TransparentSelectable(string label, Vector4 hoverColor)
    {
        return TransparentSelectable(label, false, hoverColor, hoverColor);
    }

    /// <summary>
    /// Draws a transparent selectable with separate colors for normal hover and selected+hover states.
    /// Returns true if clicked.
    /// </summary>
    public static bool TransparentSelectable(string label, bool isSelected, Vector4 hoverColor, Vector4 selectedHoverColor)
    {
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        {
            ImGui.AlignTextToFramePadding();
            clicked = ImGui.Selectable(label, false);
            HighlightRowOnHover(isSelected ? selectedHoverColor : hoverColor);
        }
        return clicked;
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
