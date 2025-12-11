using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

/// <summary>
/// Reusable tabbed panel controller with left-side tabs.
/// Uses UIColors for consistent theming.
/// </summary>
public class TabbedPanel
{
    private readonly IReadOnlyList<ITabPane> _panes;
    private int _activeTabIndex;

    // Layout constants
    private const float TabBarWidth = 120f;
    private const float TabHeight = 32f;
    private const float TabRounding = 6f;
    private const float TabSpacing = 4f;
    private const float ContentPadding = 16f;

    public TabbedPanel(params ITabPane[] panes)
    {
        if (panes.Length == 0)
            throw new ArgumentException("TabbedPanel requires at least one pane", nameof(panes));

        _panes = panes;
        _activeTabIndex = 0;
    }

    public TabbedPanel(IReadOnlyList<ITabPane> panes)
    {
        if (panes.Count == 0)
            throw new ArgumentException("TabbedPanel requires at least one pane", nameof(panes));

        _panes = panes;
        _activeTabIndex = 0;
    }

    /// <summary>
    /// Gets or sets the active tab index.
    /// </summary>
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set => _activeTabIndex = Math.Clamp(value, 0, _panes.Count - 1);
    }

    /// <summary>
    /// Draws the tabbed panel, filling available space.
    /// </summary>
    public void Draw()
    {
        var availableSize = ImGui.GetContentRegionAvail();
        var drawList = ImGui.GetWindowDrawList();

        float tabBarWidthScaled = TabBarWidth * ImGuiHelpers.GlobalScale;
        float contentWidth = availableSize.X - tabBarWidthScaled;
        float tabHeightScaled = TabHeight * ImGuiHelpers.GlobalScale;
        float spacingScaled = TabSpacing * ImGuiHelpers.GlobalScale;
        float roundingScaled = TabRounding * ImGuiHelpers.GlobalScale;

        var tabBarStart = ImGui.GetCursorScreenPos();

        // Get colors from UIColors
        var contentBgColor = UIColors.Background;
        var borderColor = UIColors.Border;
        var borderColorU32 = UIColors.BorderU32;

        // Brighter border for inactive tabs
        var brightBorderColor = new Vector4(
            Math.Min(borderColor.X * 1.5f, 1f),
            Math.Min(borderColor.Y * 1.5f, 1f),
            Math.Min(borderColor.Z * 1.5f, 1f),
            borderColor.W);
        var brightBorderU32 = ImGui.ColorConvertFloat4ToU32(brightBorderColor);

        // Calculate active tab position
        float activeTabTop = tabBarStart.Y + _activeTabIndex * (tabHeightScaled + spacingScaled);
        float activeTabBottom = activeTabTop + tabHeightScaled;

        // Draw tab bar on the left
        using (var child = ImRaii.Child("##tabbed_panel_tabs", new Vector2(tabBarWidthScaled, availableSize.Y), false))
        {
            if (child.Success)
            {
                DrawTabBar(contentBgColor, brightBorderU32, borderColorU32, roundingScaled, tabHeightScaled, tabBarWidthScaled, spacingScaled);
            }
        }

        ImGui.SameLine(0, 0);

        var contentPanelPos = ImGui.GetCursorScreenPos();
        var contentPanelEnd = contentPanelPos + new Vector2(contentWidth, availableSize.Y);

        // Draw content area
        float paddingScaled = ContentPadding * ImGuiHelpers.GlobalScale;
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(0, paddingScaled)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 0f))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, contentBgColor))
        using (var child = ImRaii.Child("##tabbed_panel_content", new Vector2(contentWidth, availableSize.Y), true))
        {
            if (child.Success)
            {
                // Apply inner padding - all sides
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(paddingScaled, paddingScaled));
                using (ImRaii.Child("##tabbed_panel_content_inner", ImGui.GetContentRegionAvail() - new Vector2(paddingScaled, paddingScaled), false))
                {
                    _panes[_activeTabIndex].Draw();
                }
            }
        }

        // Draw custom content panel border that skips active tab area
        DrawContentBorder(drawList, contentPanelPos, contentPanelEnd, tabBarStart, availableSize.Y,
            activeTabTop, activeTabBottom, borderColorU32);

        // Draw content panel shadow over inactive tabs
        DrawContentPanelShadow(drawList, contentPanelPos, availableSize.Y, tabBarStart,
            tabHeightScaled, spacingScaled);
    }

    private void DrawTabBar(Vector4 contentBgColor, uint brightBorderU32, uint borderColorU32,
        float roundingScaled, float tabHeightScaled, float tabWidthScaled, float spacingScaled)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursorStart = ImGui.GetCursorScreenPos();

        for (int i = 0; i < _panes.Count; i++)
        {
            var tabPos = cursorStart + new Vector2(0, i * (tabHeightScaled + spacingScaled));
            var tabSize = new Vector2(tabWidthScaled, tabHeightScaled);
            var tabEnd = tabPos + tabSize;

            bool isActive = _activeTabIndex == i;
            bool isHovered = ImGui.IsMouseHoveringRect(tabPos, tabEnd);

            if (isActive)
            {
                DrawActiveTab(drawList, tabPos, tabEnd, tabSize, contentBgColor, borderColorU32, roundingScaled);
            }
            else
            {
                DrawInactiveTab(drawList, tabPos, tabEnd, tabSize, isHovered, brightBorderU32, roundingScaled);
            }

            // Draw text
            var textColor = isActive ? UIColors.TextU32 : UIColors.TextDisabledU32;
            var textSize = ImGui.CalcTextSize(_panes[i].Name);
            var textPos = tabPos + (tabSize - textSize) * 0.5f;
            drawList.AddText(textPos, textColor, _panes[i].Name);

            // Handle click
            if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _activeTabIndex = i;
            }

            ImGui.SetCursorScreenPos(tabPos);
            ImGui.Dummy(tabSize);
        }
    }

    private static void DrawActiveTab(ImDrawListPtr drawList, Vector2 tabPos, Vector2 tabEnd, Vector2 tabSize,
        Vector4 contentBgColor, uint borderColorU32, float roundingScaled)
    {
        // Active tab: same background as content panel, blends seamlessly
        var contentBgU32 = ImGui.ColorConvertFloat4ToU32(contentBgColor);
        drawList.AddRectFilled(tabPos, tabEnd, contentBgU32, roundingScaled, ImDrawFlags.RoundCornersLeft);

        // Gradient overlay - SelectionActive fading from left to middle
        var selectionColor = UIColors.SelectionActive;
        var gradientStart = tabPos;
        var gradientEnd = new Vector2(tabPos.X + tabSize.X * 0.5f, tabEnd.Y);
        var gradientColorStart = ImGui.ColorConvertFloat4ToU32(selectionColor with { W = 0.5f });
        var gradientColorEnd = ImGui.ColorConvertFloat4ToU32(selectionColor with { W = 0f });
        drawList.AddRectFilledMultiColor(
            gradientStart, gradientEnd,
            gradientColorStart, gradientColorEnd, gradientColorEnd, gradientColorStart);

        // Outline: top, left, bottom only (no right - connects to content)
        // Top
        drawList.AddLine(
            new Vector2(tabPos.X + roundingScaled, tabPos.Y),
            new Vector2(tabEnd.X, tabPos.Y),
            borderColorU32, 1f);
        // Bottom
        drawList.AddLine(
            new Vector2(tabPos.X + roundingScaled, tabEnd.Y),
            new Vector2(tabEnd.X, tabEnd.Y),
            borderColorU32, 1f);
        // Left arc top
        drawList.AddBezierQuadratic(
            new Vector2(tabPos.X + roundingScaled, tabPos.Y),
            new Vector2(tabPos.X, tabPos.Y),
            new Vector2(tabPos.X, tabPos.Y + roundingScaled),
            borderColorU32, 1f, 8);
        // Left straight
        drawList.AddLine(
            new Vector2(tabPos.X, tabPos.Y + roundingScaled),
            new Vector2(tabPos.X, tabEnd.Y - roundingScaled),
            borderColorU32, 1f);
        // Left arc bottom
        drawList.AddBezierQuadratic(
            new Vector2(tabPos.X, tabEnd.Y - roundingScaled),
            new Vector2(tabPos.X, tabEnd.Y),
            new Vector2(tabPos.X + roundingScaled, tabEnd.Y),
            borderColorU32, 1f, 8);
    }

    private static void DrawInactiveTab(ImDrawListPtr drawList, Vector2 tabPos, Vector2 tabEnd, Vector2 tabSize,
        bool isHovered, uint brightBorderU32, float roundingScaled)
    {
        // Inactive tab: darker background with drop shadow
        var shadowOffset = new Vector2(2, 2) * ImGuiHelpers.GlobalScale;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.4f));
        drawList.AddRectFilled(
            tabPos + shadowOffset,
            tabEnd + shadowOffset,
            shadowColor,
            roundingScaled,
            ImDrawFlags.RoundCornersLeft);

        // Background - 60% opacity of Background color for inactive state
        var bgColor = UIColors.Background with { W = UIColors.Background.W * 0.6f };
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(tabPos, tabEnd, bgColorU32, roundingScaled, ImDrawFlags.RoundCornersLeft);

        // Gradient overlay on hover - SelectionHovered fading from left to middle
        if (isHovered)
        {
            var selectionColor = UIColors.SelectionHovered;
            var gradientStart = tabPos;
            var gradientEnd = new Vector2(tabPos.X + tabSize.X * 0.5f, tabEnd.Y);
            var gradientColorStart = ImGui.ColorConvertFloat4ToU32(selectionColor with { W = 0.5f });
            var gradientColorEnd = ImGui.ColorConvertFloat4ToU32(selectionColor with { W = 0f });
            drawList.AddRectFilledMultiColor(
                gradientStart, gradientEnd,
                gradientColorStart, gradientColorEnd, gradientColorEnd, gradientColorStart);
        }

        // Full outline (all sides) with brighter border
        // Top
        drawList.AddLine(
            new Vector2(tabPos.X + roundingScaled, tabPos.Y),
            new Vector2(tabEnd.X, tabPos.Y),
            brightBorderU32, 1f);
        // Bottom
        drawList.AddLine(
            new Vector2(tabPos.X + roundingScaled, tabEnd.Y),
            tabEnd,
            brightBorderU32, 1f);
        // Right
        drawList.AddLine(
            new Vector2(tabEnd.X, tabPos.Y),
            tabEnd,
            brightBorderU32, 1f);
        // Left arc top
        drawList.AddBezierQuadratic(
            new Vector2(tabPos.X + roundingScaled, tabPos.Y),
            new Vector2(tabPos.X, tabPos.Y),
            new Vector2(tabPos.X, tabPos.Y + roundingScaled),
            brightBorderU32, 1f, 8);
        // Left straight
        drawList.AddLine(
            new Vector2(tabPos.X, tabPos.Y + roundingScaled),
            new Vector2(tabPos.X, tabEnd.Y - roundingScaled),
            brightBorderU32, 1f);
        // Left arc bottom
        drawList.AddBezierQuadratic(
            new Vector2(tabPos.X, tabEnd.Y - roundingScaled),
            new Vector2(tabPos.X, tabEnd.Y),
            new Vector2(tabPos.X + roundingScaled, tabEnd.Y),
            brightBorderU32, 1f, 8);
    }

    private static void DrawContentBorder(ImDrawListPtr drawList, Vector2 contentPanelPos, Vector2 contentPanelEnd,
        Vector2 tabBarStart, float height, float activeTabTop, float activeTabBottom, uint borderColorU32)
    {
        // Top border
        drawList.AddLine(contentPanelPos, new Vector2(contentPanelEnd.X, contentPanelPos.Y), borderColorU32, 1f);
        // Right border
        drawList.AddLine(new Vector2(contentPanelEnd.X, contentPanelPos.Y), contentPanelEnd, borderColorU32, 1f);
        // Bottom border
        drawList.AddLine(new Vector2(contentPanelPos.X, contentPanelEnd.Y), contentPanelEnd, borderColorU32, 1f);

        // Left border - skip where active tab is
        if (activeTabTop > tabBarStart.Y)
        {
            drawList.AddLine(contentPanelPos, new Vector2(contentPanelPos.X, activeTabTop), borderColorU32, 1f);
        }
        if (activeTabBottom < tabBarStart.Y + height)
        {
            drawList.AddLine(
                new Vector2(contentPanelPos.X, activeTabBottom),
                new Vector2(contentPanelPos.X, contentPanelEnd.Y),
                borderColorU32, 1f);
        }
    }

    private void DrawContentPanelShadow(ImDrawListPtr drawList, Vector2 contentPos, float height,
        Vector2 tabBarStart, float tabHeightScaled, float spacingScaled)
    {
        float activeTabTop = tabBarStart.Y + _activeTabIndex * (tabHeightScaled + spacingScaled);
        float activeTabBottom = activeTabTop + tabHeightScaled;
        float shadowSize = 8f * ImGuiHelpers.GlobalScale;
        float padding = 2f * ImGuiHelpers.GlobalScale;

        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.4f));
        var transparent = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0f));

        float bottomY = tabBarStart.Y + height;

        // Left shadow - above active tab (with padding from edges)
        if (activeTabTop > tabBarStart.Y + padding)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowSize, tabBarStart.Y + padding),
                new Vector2(contentPos.X, activeTabTop),
                transparent, shadowColor, shadowColor, transparent);
        }

        // Left shadow - below active tab (with padding from edges)
        if (activeTabBottom < bottomY - padding)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowSize, activeTabBottom),
                new Vector2(contentPos.X, bottomY - padding),
                transparent, shadowColor, shadowColor, transparent);
        }
    }
}
