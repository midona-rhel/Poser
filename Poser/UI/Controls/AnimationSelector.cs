using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// Reusable animation selector with search and category filtering.
/// Styled to match Poser UI controls.
/// </summary>
public class AnimationSelector
{
    private const float DropdownHeight = 24f;
    private const float ButtonWidth = 24f;
    private const float Rounding = 4f;
    private const float ListHeight = 250f;
    private const float ItemHeight = 26f;

    private readonly IAnimationDataService _animationDataService;
    private readonly ITextureProvider _textureProvider;
    private string _searchText = "";
    private List<AnimationEntry> _filteredAnimations = new();

    // Category filter state
    private bool _showEmotes = true;
    private bool _showActions = true;
    private bool _showOther = true;

    public AnimationSelector(IAnimationDataService animationDataService, ITextureProvider textureProvider)
    {
        _animationDataService = animationDataService;
        _textureProvider = textureProvider;
        RefreshFilteredAnimations();
    }

    /// <summary>
    /// Draw the animation selector.
    /// </summary>
    /// <param name="id">Unique ImGui ID</param>
    /// <param name="currentId">Currently selected timeline ID (null if none)</param>
    /// <param name="onSelect">Callback when an animation is selected</param>
    /// <param name="width">Width of the selector (-1 for auto)</param>
    /// <returns>True if an animation was selected this frame</returns>
    public bool Draw(string id, ushort? currentId, Action<ushort> onSelect, float width = -1)
    {
        bool selected = false;
        float scale = PoserUI.Scale;
        float height = DropdownHeight * scale;
        float buttonW = ButtonWidth * scale;
        float rounding = Rounding * scale;

        // Get current animation name
        string displayText = "Select...";
        if (currentId.HasValue)
        {
            var entry = _animationDataService.GetById(currentId.Value);
            displayText = entry != null ? entry.Name : $"#{currentId}";
        }

        float totalWidth = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        float valueWidth = totalWidth - buttonW;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var valuePos = cursorScreenPos;
        var valueEnd = valuePos + new Vector2(valueWidth, height);
        var buttonPos = new Vector2(valueEnd.X, valuePos.Y);
        var buttonEnd = buttonPos + new Vector2(buttonW, height);

        string popupId = $"{id}_popup";
        bool isOpen = ImGui.IsPopupOpen(popupId);

        // Handle button click
        ImGui.SetCursorScreenPos(buttonPos);
        ImGui.InvisibleButton($"{id}_btn", new Vector2(buttonW, height));
        bool buttonHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            ImGui.OpenPopup(popupId);
            _searchText = "";
            RefreshFilteredAnimations();
        }

        // Handle value area click
        ImGui.SetCursorScreenPos(valuePos);
        ImGui.InvisibleButton($"{id}_value", new Vector2(valueWidth, height));
        bool valueHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            ImGui.OpenPopup(popupId);
            _searchText = "";
            RefreshFilteredAnimations();
        }

        // Draw control shadow
        DrawHelpers.DrawControlShadow(drawList, valuePos, buttonEnd, Rounding);

        // Draw value area (left side)
        var bgColor = UIColors.ApplyAlpha(UIColors.ControlBackground);
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(valuePos, valueEnd, bgColorU32, rounding, ImDrawFlags.RoundCornersLeft);

        var borderColor = UIColors.ApplyAlpha(UIColors.BorderU32);
        drawList.AddRect(valuePos, valueEnd, borderColor, rounding, ImDrawFlags.RoundCornersLeft, 1f);

        // Hide right border
        drawList.AddLine(
            new Vector2(valueEnd.X, valuePos.Y + 1),
            new Vector2(valueEnd.X, valueEnd.Y - 1),
            bgColorU32, 1f);

        // Draw value text
        float textPadding = 8f * scale;
        float maxTextWidth = valueWidth - textPadding * 2;
        string truncatedText = TruncateText(displayText, maxTextWidth);

        var textSize = ImGui.CalcTextSize(truncatedText);
        var textPos = new Vector2(valuePos.X + textPadding, valuePos.Y + (height - textSize.Y) / 2f);
        drawList.AddText(textPos, UIColors.ApplyAlpha(UIColors.TextU32), truncatedText);

        if (truncatedText != displayText && valueHovered)
            ImGui.SetTooltip(displayText);

        // Draw button area (right side)
        Vector4 btnColor;
        if (isOpen)
            btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (buttonHovered || valueHovered)
            btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else
            btnColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        btnColor = UIColors.ApplyAlpha(btnColor with { W = 1f });
        var btnColorU32 = ImGui.ColorConvertFloat4ToU32(btnColor);
        drawList.AddRectFilled(buttonPos, buttonEnd, btnColorU32, rounding, ImDrawFlags.RoundCornersRight);

        if (!isOpen)
            DrawHelpers.DrawButtonGradients(drawList, buttonPos, buttonEnd, height, rounding);

        drawList.AddRect(buttonPos, buttonEnd, borderColor, rounding, ImDrawFlags.RoundCornersRight, 1f);

        // Draw dropdown arrow
        var iconFont = UiBuilder.IconFont;
        var arrowIcon = FontAwesomeIcon.ChevronDown.ToIconString();

        ImGui.PushFont(iconFont);
        var iconSize = ImGui.CalcTextSize(arrowIcon);
        ImGui.PopFont();

        var iconPos = buttonPos + (new Vector2(buttonW, height) - iconSize) / 2f;
        DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, arrowIcon,
            UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), 1f * scale);

        // Advance cursor
        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, height));

        // Draw popup - match control width and position below
        float popupW = totalWidth;
        float popupPadding = 8f * scale;

        // Position popup directly below the control
        ImGui.SetNextWindowPos(new Vector2(valuePos.X, valuePos.Y + height));

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(popupPadding, popupPadding)))
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, rounding))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, UIColors.Background))
        using (ImRaii.PushColor(ImGuiCol.Border, UIColors.Border))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrab, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, UIColors.ControlBackground))
        {
            ImGui.SetNextWindowSize(new Vector2(popupW, 0));
            if (ImGui.BeginPopup(popupId))
            {
                // Search input
                if (PoserTextInput.Draw($"{id}_search", ref _searchText, "Search animations...", popupW - popupPadding * 2, false))
                {
                    RefreshFilteredAnimations();
                }

                // Focus search on popup open
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere(-1);

                ImGui.Spacing();

                // Category filter toggles
                DrawCategoryFilters(id, popupW - popupPadding * 2);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Animation list with background color
                float listH = ListHeight * scale;
                using (ImRaii.PushColor(ImGuiCol.ChildBg, UIColors.Background))
                using (var child = ImRaii.Child($"{id}_list", new Vector2(popupW - popupPadding * 2, listH)))
                {
                    if (child.Success)
                    {
                        foreach (var entry in _filteredAnimations)
                        {
                            if (DrawAnimationEntry(id, entry, currentId, out bool wasSelected))
                            {
                                onSelect(entry.TimelineId);
                                ImGui.CloseCurrentPopup();
                                selected = true;
                            }
                        }

                        if (_filteredAnimations.Count == 0)
                        {
                            ImGui.TextDisabled("No animations found");
                        }
                    }
                }

                ImGui.EndPopup();
            }
        }

        return selected;
    }

    private void DrawCategoryFilters(string id, float availWidth)
    {
        float scale = PoserUI.Scale;
        float spacing = 8f * scale;
        float itemWidth = availWidth / 3f;

        var startX = ImGui.GetCursorPosX();

        // Emotes checkbox
        ImGui.BeginGroup();
        if (PoserCheckbox.Draw($"{id}_emotes", ref _showEmotes))
            RefreshFilteredAnimations();
        ImGui.SameLine();
        ImGui.Text("Emotes");
        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + itemWidth);

        // Actions checkbox
        ImGui.BeginGroup();
        if (PoserCheckbox.Draw($"{id}_actions", ref _showActions))
            RefreshFilteredAnimations();
        ImGui.SameLine();
        ImGui.Text("Actions");
        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.SetCursorPosX(startX + itemWidth * 2);

        // Other checkbox
        ImGui.BeginGroup();
        if (PoserCheckbox.Draw($"{id}_other", ref _showOther))
            RefreshFilteredAnimations();
        ImGui.SameLine();
        ImGui.Text("Other");
        ImGui.EndGroup();
    }

    private bool DrawAnimationEntry(string id, AnimationEntry entry, ushort? currentId, out bool selected)
    {
        selected = false;
        float scale = PoserUI.Scale;
        float itemH = ItemHeight * scale;
        float rounding = Rounding * scale;
        float iconSize = 18f * scale;
        float padding = 6f * scale;

        bool isSelected = currentId.HasValue && currentId.Value == entry.TimelineId;

        var cursorPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        float availWidth = ImGui.GetContentRegionAvail().X;

        var itemPos = cursorPos;
        var itemEnd = itemPos + new Vector2(availWidth, itemH);

        // Handle interaction
        ImGui.InvisibleButton($"{id}_{entry.TimelineId}", new Vector2(availWidth, itemH));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();

        // Draw background
        if (isSelected)
        {
            drawList.AddRectFilled(itemPos, itemEnd, UIColors.ApplyAlpha(UIColors.SelectionActiveU32), rounding);
        }
        else if (hovered)
        {
            drawList.AddRectFilled(itemPos, itemEnd, UIColors.ApplyAlpha(UIColors.SelectionHoveredU32), rounding);
        }

        // Icon position
        var iconBgPos = new Vector2(itemPos.X + padding, itemPos.Y + (itemH - iconSize) / 2f);
        var iconBgEnd = iconBgPos + new Vector2(iconSize, iconSize);

        // Try to draw game icon if available
        bool drewGameIcon = false;
        if (entry.Icon != 0)
        {
            try
            {
                var tex = _textureProvider.GetFromGameIcon(entry.Icon).GetWrapOrEmpty();
                if (tex != null && tex.Handle != nint.Zero)
                {
                    drawList.AddImage(tex.Handle, iconBgPos, iconBgEnd);
                    drewGameIcon = true;
                }
            }
            catch
            {
                // Icon not found, fall back to colored pill
            }
        }

        // Fallback: draw colored pill with FontAwesome icon
        if (!drewGameIcon)
        {
            var iconColor = entry.Category switch
            {
                AnimationCategory.Emote => new Vector4(0.3f, 0.7f, 0.3f, 1f),
                AnimationCategory.Action => new Vector4(0.7f, 0.3f, 0.3f, 1f),
                _ => new Vector4(0.5f, 0.5f, 0.5f, 1f)
            };

            var categoryIcon = entry.Category switch
            {
                AnimationCategory.Emote => FontAwesomeIcon.SmileBeam,
                AnimationCategory.Action => FontAwesomeIcon.Bolt,
                _ => FontAwesomeIcon.Film
            };

            drawList.AddRectFilled(iconBgPos, iconBgEnd, ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(iconColor)), iconSize / 2f);

            var iconFont = UiBuilder.IconFont;
            var iconStr = categoryIcon.ToIconString();

            ImGui.PushFont(iconFont);
            var iconTextSize = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var iconTextPos = iconBgPos + (new Vector2(iconSize, iconSize) - iconTextSize) / 2f;
            drawList.AddText(iconFont, ImGui.GetFontSize(), iconTextPos, UIColors.ApplyAlpha(UIColors.WhiteU32), iconStr);
        }

        // Draw animation name
        float textX = iconBgEnd.X + padding;
        var namePos = new Vector2(textX, itemPos.Y + (itemH - ImGui.GetTextLineHeight()) / 2f);
        drawList.AddText(namePos, UIColors.ApplyAlpha(UIColors.TextU32), entry.Name);

        // Draw timeline ID on the right
        string idText = $"[{entry.TimelineId}]";
        var idSize = ImGui.CalcTextSize(idText);
        var idPos = new Vector2(itemEnd.X - idSize.X - padding, itemPos.Y + (itemH - idSize.Y) / 2f);
        drawList.AddText(idPos, UIColors.ApplyAlpha(UIColors.TextDisabledU32), idText);

        if (clicked)
            selected = true;

        return clicked;
    }

    private void RefreshFilteredAnimations()
    {
        var animations = string.IsNullOrWhiteSpace(_searchText)
            ? _animationDataService.Animations
            : _animationDataService.Search(_searchText, int.MaxValue);

        _filteredAnimations = animations
            .Where(a => (a.Category == AnimationCategory.Emote && _showEmotes) ||
                       (a.Category == AnimationCategory.Action && _showActions) ||
                       (a.Category != AnimationCategory.Emote && a.Category != AnimationCategory.Action && _showOther))
            .ToList();
    }

    /// <summary>
    /// Reset the selector state.
    /// </summary>
    public void Reset()
    {
        _searchText = "";
        _showEmotes = true;
        _showActions = true;
        _showOther = true;
        RefreshFilteredAnimations();
    }

    private static string TruncateText(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var textSize = ImGui.CalcTextSize(text);
        if (textSize.X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var ellipsisSize = ImGui.CalcTextSize(ellipsis);
        float availableWidth = maxWidth - ellipsisSize.X;

        if (availableWidth <= 0)
            return ellipsis;

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

        return left == 0 ? ellipsis : text[..left] + ellipsis;
    }
}
