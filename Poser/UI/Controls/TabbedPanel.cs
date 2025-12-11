using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.UI.Effects;

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
    public void Draw() => Draw(null);

    /// <summary>
    /// Draws the tabbed panel with an optional parent draw list for overlays.
    /// </summary>
    /// <param name="overlayDrawList">Draw list to use for border/shadow. If null, uses foreground draw list.</param>
    public void Draw(ImDrawListPtr? overlayDrawList)
    {
        var availableSize = ImGui.GetContentRegionAvail();
        var drawList = ImGui.GetWindowDrawList();

        float tabBarWidthScaled = TabBarWidth * PoserUI.Scale;
        float contentWidth = availableSize.X - tabBarWidthScaled;
        float tabHeightScaled = TabHeight * PoserUI.Scale;
        float spacingScaled = TabSpacing * PoserUI.Scale;
        float roundingScaled = TabRounding * PoserUI.Scale;

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

                // Draw shadow on right edge of tab bar (cast by content panel onto tabs)
                var tabDrawList = ImGui.GetWindowDrawList();
                DrawTabBarRightShadow(tabDrawList, tabBarStart, tabBarWidthScaled, availableSize.Y,
                    activeTabTop, activeTabBottom);
            }
        }

        ImGui.SameLine(0, 0);

        var contentPanelPos = ImGui.GetCursorScreenPos();
        var contentPanelEnd = contentPanelPos + new Vector2(contentWidth, availableSize.Y);

        // Draw content area
        // Outer child: background only, no scroll, no border
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 0f))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, contentBgColor))
        using (var child = ImRaii.Child("##tabbed_panel_content", new Vector2(contentWidth, availableSize.Y), false))
        {
            if (child.Success)
            {
                // Draw border inside content panel
                var contentDrawList = ImGui.GetWindowDrawList();
                DrawContentBorder(contentDrawList, contentPanelPos, contentPanelEnd, tabBarStart, availableSize.Y,
                    activeTabTop, activeTabBottom, borderColorU32);

                // Inner scrollable child with padding around it
                float paddingScaled = ContentPadding * PoserUI.Scale;
                var available = ImGui.GetContentRegionAvail();
                float scrollbarSize = 12f * PoserUI.Scale;

                // Offset inner child by padding on all sides (includes 1px for border visibility)
                float offset = paddingScaled;
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(offset, offset));
                var innerSize = available - new Vector2(offset * 2, offset * 2);

                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(0, paddingScaled)))
                using (ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, scrollbarSize))
                using (ImRaii.PushStyle(ImGuiStyleVar.ScrollbarRounding, 6f * PoserUI.Scale))
                using (ImRaii.Child("##tabbed_panel_content_inner", innerSize, false))
                {
                    _panes[_activeTabIndex].Draw();
                }
            }
        }

        // Draw content panel outer shadows (top, right, bottom + corners)
        // Left shadow is drawn in the tab bar child window above
        var overlayDL = overlayDrawList ?? ImGui.GetForegroundDrawList();
        DrawContentPanelShadow(overlayDL, contentPanelPos, contentPanelEnd);
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
        // Active tab: gradient from selection color to content background
        // Using vertex manipulation to properly respect rounded corners
        var colorLeft = UIColors.SelectionActive with { W = 0.5f };
        colorLeft = new Vector4(
            contentBgColor.X + (colorLeft.X - contentBgColor.X) * colorLeft.W,
            contentBgColor.Y + (colorLeft.Y - contentBgColor.Y) * colorLeft.W,
            contentBgColor.Z + (colorLeft.Z - contentBgColor.Z) * colorLeft.W,
            contentBgColor.W);
        var colorRight = contentBgColor;

        DrawHelpers.DrawRoundedRectWithHorizontalGradient(drawList, tabPos, tabSize,
            colorLeft, colorRight, roundingScaled, ImDrawFlags.RoundCornersLeft);

        // Outline: top, left, bottom only (no right - connects to content)
        // Pass contentBgColor to hide the right edge
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(contentBgColor);
        DrawHelpers.DrawRoundedLeftBorder(drawList, tabPos, tabEnd, roundingScaled, borderColorU32, false, bgColorU32);
    }

    private static void DrawInactiveTab(ImDrawListPtr drawList, Vector2 tabPos, Vector2 tabEnd, Vector2 tabSize,
        bool isHovered, uint brightBorderU32, float roundingScaled)
    {
        // Background - 60% opacity of Background color for inactive state
        var bgColor = UIColors.Background with { W = UIColors.Background.W * 0.6f };
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);

        if (isHovered)
        {
            // Gradient from hover color to background using vertex manipulation
            var colorLeft = UIColors.SelectionHovered with { W = 0.5f };
            colorLeft = new Vector4(
                bgColor.X + (colorLeft.X - bgColor.X) * colorLeft.W,
                bgColor.Y + (colorLeft.Y - bgColor.Y) * colorLeft.W,
                bgColor.Z + (colorLeft.Z - bgColor.Z) * colorLeft.W,
                bgColor.W);

            DrawHelpers.DrawRoundedRectWithHorizontalGradient(drawList, tabPos, tabSize,
                colorLeft, bgColor, roundingScaled, ImDrawFlags.RoundCornersLeft);
        }
        else
        {
            // Solid background
            drawList.AddRectFilled(tabPos, tabEnd, bgColorU32, roundingScaled, ImDrawFlags.RoundCornersLeft);
        }

        // Outline without right edge (tabs connect to content panel)
        DrawHelpers.DrawRoundedLeftBorder(drawList, tabPos, tabEnd, roundingScaled, brightBorderU32, false, bgColorU32);
    }

    private static void DrawContentBorder(ImDrawListPtr drawList, Vector2 contentPanelPos, Vector2 contentPanelEnd,
        Vector2 tabBarStart, float height, float activeTabTop, float activeTabBottom, uint borderColorU32)
    {
        // Offset to draw inside clip region
        float rightX = contentPanelEnd.X - 1;
        float bottomY = contentPanelEnd.Y - 1;

        // Top border
        drawList.AddLine(contentPanelPos, new Vector2(rightX, contentPanelPos.Y), borderColorU32, 1f);
        // Right border
        drawList.AddLine(new Vector2(rightX, contentPanelPos.Y), new Vector2(rightX, bottomY), borderColorU32, 1f);
        // Bottom border
        drawList.AddLine(new Vector2(contentPanelPos.X, bottomY), new Vector2(rightX, bottomY), borderColorU32, 1f);

        // Left border - skip where active tab is
        if (activeTabTop > tabBarStart.Y)
        {
            drawList.AddLine(contentPanelPos, new Vector2(contentPanelPos.X, activeTabTop), borderColorU32, 1f);
        }
        if (activeTabBottom < tabBarStart.Y + height)
        {
            drawList.AddLine(
                new Vector2(contentPanelPos.X, activeTabBottom),
                new Vector2(contentPanelPos.X, bottomY),
                borderColorU32, 1f);
        }
    }

    private static void DrawContentPanelShadow(ImDrawListPtr drawList, Vector2 contentPos, Vector2 contentEnd)
    {
        // Drop shadow on all sides except left (where tabs connect)
        DrawHelpers.DrawDropShadow(drawList, contentPos, contentEnd, DrawHelpers.Edge.Left);
    }

    private static void DrawTabBarRightShadow(ImDrawListPtr drawList, Vector2 tabBarStart, float tabBarWidth, float tabBarHeight,
        float activeTabTop, float activeTabBottom)
    {
        var areaMin = tabBarStart;
        var areaMax = new Vector2(tabBarStart.X + tabBarWidth, tabBarStart.Y + tabBarHeight);

        // Inner shadow on right edge of tab bar with gap for active tab
        DrawHelpers.DrawInnerEdgeShadow(drawList, DrawHelpers.Edge.Right, areaMin, areaMax,
            8f, activeTabTop, activeTabBottom);
    }
}
