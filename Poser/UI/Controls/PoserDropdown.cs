using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.UI;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A styled dropdown control with custom appearance.
/// Left side shows current value, right side is a button with dropdown arrow.
/// </summary>
public static class PoserDropdown
{
    private const float DropdownHeight = 24f;
    private const float ButtonWidth = 24f;
    private const float Rounding = 4f;
    private const float MinValueWidth = 80f;
    private const string Ellipsis = "...";

    /// <summary>
    /// Draws a styled dropdown.
    /// </summary>
    /// <param name="id">Unique ID for the dropdown.</param>
    /// <param name="currentIndex">Current selected index (ref).</param>
    /// <param name="items">Array of item labels.</param>
    /// <param name="width">Total width. If 0, uses available width.</param>
    /// <returns>True if selection changed.</returns>
    public static bool Draw(string id, ref int currentIndex, string[] items, float width = 0f)
    {
        if (items.Length == 0)
            return false;

        bool changed = false;
        float scale = PoserUI.Scale;
        float height = DropdownHeight * scale;
        float buttonW = ButtonWidth * scale;
        float rounding = Rounding * scale;
        float minValueW = MinValueWidth * scale;

        // Calculate widths
        float totalWidth = width > 0 ? width : Math.Max(minValueW + buttonW, ImGui.GetContentRegionAvail().X);
        float valueWidth = totalWidth - buttonW;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var valuePos = cursorScreenPos;
        var valueEnd = valuePos + new Vector2(valueWidth, height);
        var buttonPos = new Vector2(valueEnd.X, valuePos.Y);
        var buttonEnd = buttonPos + new Vector2(buttonW, height);

        string popupId = $"{id}_popup";

        // Check if popup is open
        bool isOpen = ImGui.IsPopupOpen(popupId);

        // Handle button click
        ImGui.SetCursorScreenPos(buttonPos);
        ImGui.InvisibleButton($"{id}_btn", new Vector2(buttonW, height));
        bool buttonHovered = ImGui.IsItemHovered();
        bool buttonClicked = ImGui.IsItemClicked();

        if (buttonClicked)
        {
            ImGui.OpenPopup(popupId);
        }

        // Handle value area click (also opens dropdown)
        ImGui.SetCursorScreenPos(valuePos);
        ImGui.InvisibleButton($"{id}_value", new Vector2(valueWidth, height));
        bool valueHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            ImGui.OpenPopup(popupId);
        }

        // Draw control shadow for entire dropdown
        DrawHelpers.DrawControlShadow(drawList, valuePos, buttonEnd, Rounding);

        // Draw value area (left side)
        // Background
        var bgColor = UIColors.ApplyAlpha(UIColors.ControlBackground);
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(valuePos, valueEnd, bgColorU32, rounding, ImDrawFlags.RoundCornersLeft);

        // Border (left side with rounded corners)
        var borderColor = UIColors.ApplyAlpha(UIColors.BorderU32);
        drawList.AddRect(valuePos, valueEnd, borderColor, rounding, ImDrawFlags.RoundCornersLeft, 1f);

        // Hide right border by drawing over it with background color
        drawList.AddLine(
            new Vector2(valueEnd.X, valuePos.Y + 1),
            new Vector2(valueEnd.X, valueEnd.Y - 1),
            bgColorU32, 1f);

        // Draw current value text
        string currentText = currentIndex >= 0 && currentIndex < items.Length ? items[currentIndex] : "";
        float textPadding = 8f * scale;
        float maxTextWidth = valueWidth - textPadding * 2;

        string displayText = TruncateText(currentText, maxTextWidth);
        bool valueTextTruncated = displayText != currentText;

        var textSize = ImGui.CalcTextSize(displayText);
        var textPos = new Vector2(valuePos.X + textPadding, valuePos.Y + (height - textSize.Y) / 2f);
        drawList.AddText(textPos, UIColors.ApplyAlpha(UIColors.TextU32), displayText);

        // Show tooltip if text was truncated and value area is hovered
        if (valueTextTruncated && valueHovered)
        {
            ImGui.SetTooltip(currentText);
        }

        // Draw button area (right side)
        // Button background with gradient like PoserButton
        Vector4 buttonColor;
        if (isOpen)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (buttonHovered || valueHovered)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        buttonColor = UIColors.ApplyAlpha(buttonColor with { W = 1f });
        var buttonColorU32 = ImGui.ColorConvertFloat4ToU32(buttonColor);
        drawList.AddRectFilled(buttonPos, buttonEnd, buttonColorU32, rounding, ImDrawFlags.RoundCornersRight);

        // Button highlight/shadow gradients (like PoserButton)
        if (!isOpen)
        {
            DrawHelpers.DrawButtonGradients(drawList, buttonPos, buttonEnd, height, rounding);
        }

        // Button border (right side with rounded corners)
        drawList.AddRect(buttonPos, buttonEnd, borderColor, rounding, ImDrawFlags.RoundCornersRight, 1f);

        // Draw dropdown arrow (white with black outline)
        var iconFont = UiBuilder.IconFont;
        var arrowIcon = FontAwesomeIcon.ChevronDown.ToIconString();

        ImGui.PushFont(iconFont);
        var iconSize = ImGui.CalcTextSize(arrowIcon);
        ImGui.PopFont();

        var iconPos = buttonPos + (new Vector2(buttonW, height) - iconSize) / 2f;
        float outlineOffset = 1f * scale;
        DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, arrowIcon, UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset);

        // Advance cursor
        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, height));

        // Draw popup
        float popupPadding = 4f * scale;
        float itemHeight = height;
        int maxVisibleItems = 10;
        int visibleItems = Math.Min(items.Length, maxVisibleItems);
        float popupHeight = visibleItems * itemHeight + popupPadding * 2;
        float popupWidth = totalWidth;

        // Calculate popup position - try below first, then above if needed
        var displaySize = ImGui.GetIO().DisplaySize;
        float popupY = valueEnd.Y + 2f * scale;

        // If popup would go off bottom, try putting it above
        if (popupY + popupHeight > displaySize.Y)
        {
            float aboveY = valuePos.Y - popupHeight - 2f * scale;
            if (aboveY >= 0)
            {
                popupY = aboveY;
            }
            else
            {
                // Can't fit above either, pin to bottom of screen
                popupY = displaySize.Y - popupHeight;
            }
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, rounding);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);

        ImGui.SetNextWindowPos(new Vector2(valuePos.X, popupY));
        ImGui.SetNextWindowSize(new Vector2(popupWidth, popupHeight));
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            // Scrollable child for items
            float scrollbarSize = 8f * scale;
            bool needsScroll = items.Length > maxVisibleItems;

            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, scrollbarSize);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4f * scale);

            var childSize = new Vector2(popupWidth - popupPadding * 2, visibleItems * itemHeight);
            using var child = ImRaii.Child("##dropdown_scroll", childSize, false,
                needsScroll ? ImGuiWindowFlags.AlwaysVerticalScrollbar : ImGuiWindowFlags.None);
            if (child)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    bool isSelected = i == currentIndex;

                    var itemPos = ImGui.GetCursorScreenPos();
                    var itemSize = new Vector2(childSize.X - (needsScroll ? scrollbarSize : 0), itemHeight);

                    ImGui.PushID(i);
                    if (ImGui.Selectable($"##item", isSelected, ImGuiSelectableFlags.None, itemSize))
                    {
                        if (currentIndex != i)
                        {
                            currentIndex = i;
                            changed = true;
                        }
                        ImGui.CloseCurrentPopup();
                    }
                    bool itemHovered = ImGui.IsItemHovered();

                    // Truncate text if needed
                    float itemMaxTextWidth = itemSize.X - textPadding * 2;
                    string itemDisplayText = TruncateText(items[i], itemMaxTextWidth);
                    bool itemTruncated = itemDisplayText != items[i];

                    // Draw text on top of selectable
                    var popupDrawList = ImGui.GetWindowDrawList();
                    var itemTextSize = ImGui.CalcTextSize(itemDisplayText);
                    var itemTextPos = new Vector2(itemPos.X + textPadding, itemPos.Y + (itemHeight - itemTextSize.Y) / 2f);
                    popupDrawList.AddText(itemTextPos, UIColors.TextU32, itemDisplayText);

                    // Show tooltip if truncated and hovered
                    if (itemTruncated && itemHovered)
                    {
                        ImGui.SetTooltip(items[i]);
                    }

                    ImGui.PopID();
                }
            }

            ImGui.PopStyleVar(2);
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

        return changed;
    }

    /// <summary>
    /// Gets the dropdown height (scaled).
    /// </summary>
    public static float Height => DropdownHeight * PoserUI.Scale;

    /// <summary>
    /// Truncates text to fit within maxWidth, adding ellipsis if needed.
    /// </summary>
    private static string TruncateText(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var textSize = ImGui.CalcTextSize(text);
        if (textSize.X <= maxWidth)
            return text;

        var ellipsisSize = ImGui.CalcTextSize(Ellipsis);
        float availableWidth = maxWidth - ellipsisSize.X;

        if (availableWidth <= 0)
            return Ellipsis;

        // Binary search for the right length
        int left = 0;
        int right = text.Length;

        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            var subText = text[..mid];
            var subSize = ImGui.CalcTextSize(subText);

            if (subSize.X <= availableWidth)
                left = mid;
            else
                right = mid - 1;
        }

        if (left == 0)
            return Ellipsis;

        return text[..left] + Ellipsis;
    }
}
