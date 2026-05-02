using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Split-button dropdown with popup list. Returns true on selection change.</summary>
    public static bool Dropdown(ElementProps props, string[] items, ref int currentIndex)
    {
        Stylesheet.EnsureInitialized();
        if (items.Length == 0) return false;

        bool changed = false;
        float scale = PoserUI.Scale;
        float height = Flex.RowHeight * scale;
        float buttonW = Flex.RowHeight * scale; // square chevron button
        float rounding = 4f * scale;
        float minValueW = 80f * scale;

        float totalWidth = ResolveAvailableWidth(props.Style.Width);
        if (totalWidth < minValueW + buttonW) totalWidth = minValueW + buttonW;
        float valueWidth = totalWidth - buttonW;

        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var valuePos = pos;
        var valueEnd = valuePos + new Vector2(valueWidth, height);
        var btnPos = new Vector2(valueEnd.X, valuePos.Y);
        var btnEnd = btnPos + new Vector2(buttonW, height);

        string id = props.Id ?? "dropdown";
        string popupId = $"{id}_popup";
        bool isOpen = ImGui.IsPopupOpen(popupId);

        // Right (chevron) side hit-test
        ImGui.SetCursorScreenPos(btnPos);
        ImGui.InvisibleButton($"{id}_btn", new Vector2(buttonW, height));
        bool buttonHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) ImGui.OpenPopup(popupId);

        // Left (value) side hit-test
        ImGui.SetCursorScreenPos(valuePos);
        ImGui.InvisibleButton($"{id}_value", new Vector2(valueWidth, height));
        bool valueHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked()) ImGui.OpenPopup(popupId);

        // Chrome
        DrawHelpers.DrawControlShadow(drawList, valuePos, btnEnd, 4f);

        var bgColor = UIColors.ApplyAlpha(UIColors.ControlBackground);
        var bgU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(valuePos, valueEnd, bgU32, rounding, ImDrawFlags.RoundCornersLeft);
        drawList.AddRect(valuePos, valueEnd, UIColors.ApplyAlpha(UIColors.BorderU32), rounding, ImDrawFlags.RoundCornersLeft, 1f);
        drawList.AddLine(new Vector2(valueEnd.X, valuePos.Y + 1), new Vector2(valueEnd.X, valueEnd.Y - 1), bgU32, 1f);

        // Current value text
        string currentText = (currentIndex >= 0 && currentIndex < items.Length) ? items[currentIndex] : "";
        float textPadding = Flex.TextPadding * scale;
        string display = TruncateText(currentText, valueWidth - textPadding * 2);
        var textSize = ImGui.CalcTextSize(display);
        var textPos = new Vector2(valuePos.X + textPadding, valuePos.Y + (height - textSize.Y) / 2f);
        drawList.AddText(textPos, UIColors.ApplyAlpha(UIColors.TextU32), display);
        if (display != currentText && valueHovered) ImGui.SetTooltip(currentText);

        // Chevron button
        Vector4 btnColor;
        if (isOpen)                              btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (buttonHovered || valueHovered)  btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else                                     btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        btnColor = UIColors.ApplyAlpha(btnColor with { W = 1f });
        drawList.AddRectFilled(btnPos, btnEnd, ImGui.ColorConvertFloat4ToU32(btnColor), rounding, ImDrawFlags.RoundCornersRight);
        if (!isOpen) DrawHelpers.DrawButtonGradients(drawList, btnPos, btnEnd, height, 4f);
        drawList.AddRect(btnPos, btnEnd, UIColors.ApplyAlpha(UIColors.BorderU32), rounding, ImDrawFlags.RoundCornersRight, 1f);

        // Chevron icon
        var iconFont = UiBuilder.IconFont;
        var arrowIcon = FontAwesomeIcon.ChevronDown.ToIconString();
        ImGui.PushFont(iconFont);
        var iconSize = ImGui.CalcTextSize(arrowIcon);
        ImGui.PopFont();
        var iconPos = btnPos + (new Vector2(buttonW, height) - iconSize) / 2f;
        DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, arrowIcon,
            UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), 1f * scale);

        // Advance cursor below the dropdown
        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));

        // Popup
        float popupPadding = Flex.SmallGap * scale;
        const int maxVisibleItems = 10;
        int visibleItems = Math.Min(items.Length, maxVisibleItems);
        float popupHeight = visibleItems * height + popupPadding * 2;
        float popupY = valueEnd.Y + 2f * scale;
        var displaySize = ImGui.GetIO().DisplaySize;
        if (popupY + popupHeight > displaySize.Y)
        {
            float aboveY = valuePos.Y - popupHeight - 2f * scale;
            popupY = aboveY >= 0 ? aboveY : displaySize.Y - popupHeight;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, rounding);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);

        ImGui.SetNextWindowPos(new Vector2(valuePos.X, popupY));
        ImGui.SetNextWindowSize(new Vector2(totalWidth, popupHeight));
        if (ImGui.BeginPopup(popupId, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            float scrollbarSize = 8f * scale;
            bool needsScroll = items.Length > maxVisibleItems;
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, scrollbarSize);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 4f * scale);

            var childSize = new Vector2(totalWidth - popupPadding * 2, visibleItems * height);
            using var child = ImRaii.Child("##dropdown_scroll", childSize, false,
                needsScroll ? ImGuiWindowFlags.AlwaysVerticalScrollbar : ImGuiWindowFlags.None);
            if (child)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    bool isSelected = i == currentIndex;
                    var itemPos = ImGui.GetCursorScreenPos();
                    var itemSize = new Vector2(childSize.X - (needsScroll ? scrollbarSize : 0), height);

                    ImGui.PushID(i);
                    if (ImGui.Selectable("##item", isSelected, ImGuiSelectableFlags.None, itemSize))
                    {
                        if (currentIndex != i) { currentIndex = i; changed = true; }
                        ImGui.CloseCurrentPopup();
                    }
                    bool itemHovered = ImGui.IsItemHovered();

                    var popupDrawList = ImGui.GetWindowDrawList();
                    string itemDisplay = TruncateText(items[i], itemSize.X - textPadding * 2);
                    var itemTextSize = ImGui.CalcTextSize(itemDisplay);
                    var itemTextPos = new Vector2(itemPos.X + textPadding, itemPos.Y + (height - itemTextSize.Y) / 2f);
                    popupDrawList.AddText(itemTextPos, UIColors.TextU32, itemDisplay);
                    if (itemDisplay != items[i] && itemHovered) ImGui.SetTooltip(items[i]);

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

    private static string TruncateText(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var size = ImGui.CalcTextSize(text);
        if (size.X <= maxWidth) return text;
        var ellipsisSize = ImGui.CalcTextSize("...");
        float available = maxWidth - ellipsisSize.X;
        if (available <= 0) return "...";
        int left = 0, right = text.Length;
        while (left < right)
        {
            int mid = (left + right + 1) / 2;
            var sub = ImGui.CalcTextSize(text[..mid]);
            if (sub.X <= available) left = mid; else right = mid - 1;
        }
        return left == 0 ? "..." : text[..left] + "...";
    }
}
