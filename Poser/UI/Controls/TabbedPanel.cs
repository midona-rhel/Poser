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

        // Use foreground draw list so border/shadow render on top of child windows
        var fgDrawList = ImGui.GetForegroundDrawList();

        // Draw custom content panel border that skips active tab area
        DrawContentBorder(fgDrawList, contentPanelPos, contentPanelEnd, tabBarStart, availableSize.Y,
            activeTabTop, activeTabBottom, borderColorU32);

        // Draw content panel shadow (outside, all sides)
        DrawContentPanelShadow(fgDrawList, contentPanelPos, contentPanelEnd, tabBarStart,
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

    private void DrawContentPanelShadow(ImDrawListPtr drawList, Vector2 contentPos, Vector2 contentEnd,
        Vector2 tabBarStart, float tabHeightScaled, float spacingScaled)
    {
        float activeTabTop = tabBarStart.Y + _activeTabIndex * (tabHeightScaled + spacingScaled);
        float activeTabBottom = activeTabTop + tabHeightScaled;
        float shadowSize = 8f * ImGuiHelpers.GlobalScale;

        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.4f));
        var transparent = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0f));

        // Left shadow - above active tab
        if (activeTabTop > contentPos.Y)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowSize, contentPos.Y),
                new Vector2(contentPos.X, activeTabTop),
                transparent, shadowColor, shadowColor, transparent);
        }

        // Left shadow - below active tab
        if (activeTabBottom < contentEnd.Y)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowSize, activeTabBottom),
                new Vector2(contentPos.X, contentEnd.Y),
                transparent, shadowColor, shadowColor, transparent);
        }

        // Top shadow
        drawList.AddRectFilledMultiColor(
            new Vector2(contentPos.X, contentPos.Y - shadowSize),
            new Vector2(contentEnd.X, contentPos.Y),
            transparent, transparent, shadowColor, shadowColor);

        // Right shadow
        drawList.AddRectFilledMultiColor(
            new Vector2(contentEnd.X, contentPos.Y),
            new Vector2(contentEnd.X + shadowSize, contentEnd.Y),
            shadowColor, transparent, transparent, shadowColor);

        // Bottom shadow
        drawList.AddRectFilledMultiColor(
            new Vector2(contentPos.X, contentEnd.Y),
            new Vector2(contentEnd.X, contentEnd.Y + shadowSize),
            shadowColor, shadowColor, transparent, transparent);

        // Corner shadows using radial gradient (quarter circles)
        // Use same shadowColor (0.4 alpha) - the DrawCornerShadow will fade it radially

        // Top-right corner
        DrawCornerShadow(drawList, new Vector2(contentEnd.X, contentPos.Y), shadowSize, shadowColor, transparent, 3);

        // Bottom-right corner
        DrawCornerShadow(drawList, contentEnd, shadowSize, shadowColor, transparent, 0);

        // Bottom-left corner
        DrawCornerShadow(drawList, new Vector2(contentPos.X, contentEnd.Y), shadowSize, shadowColor, transparent, 1);

        // Top-left corner
        DrawCornerShadow(drawList, contentPos, shadowSize, shadowColor, transparent, 2);
    }

    private static void DrawCornerShadow(ImDrawListPtr drawList, Vector2 corner, float radius,
        uint innerColor, uint outerColor, int quadrant)
    {
        // Use triangle fan approach for proper radial gradient
        // This creates triangles from the corner (inner color) to arc points (outer color)
        const int segments = 8;

        // Calculate start and end angles for each quadrant
        float startAngle, endAngle;
        switch (quadrant)
        {
            case 0: // bottom-right: 0 to 90 degrees (right to down)
                startAngle = 0f;
                endAngle = MathF.PI * 0.5f;
                break;
            case 1: // bottom-left: 90 to 180 degrees (down to left)
                startAngle = MathF.PI * 0.5f;
                endAngle = MathF.PI;
                break;
            case 2: // top-left: 180 to 270 degrees (left to up)
                startAngle = MathF.PI;
                endAngle = MathF.PI * 1.5f;
                break;
            case 3: // top-right: 270 to 360 degrees (up to right)
                startAngle = MathF.PI * 1.5f;
                endAngle = MathF.PI * 2f;
                break;
            default:
                return;
        }

        // Get white pixel UV for solid color rendering
        var uv = ImGui.GetFontTexUvWhitePixel();

        // Reserve vertices: 1 center + (segments + 1) arc points
        // Reserve indices: segments * 3 (one triangle per segment)
        int vtxCount = segments + 2;
        int idxCount = segments * 3;

        uint vtxBase = (uint)drawList.VtxBuffer.Size;
        drawList.PrimReserve(idxCount, vtxCount);

        // Center vertex (inner color)
        drawList.PrimWriteVtx(corner, uv, innerColor);

        // Arc vertices (outer color)
        float angleStep = (endAngle - startAngle) / segments;
        for (int i = 0; i <= segments; i++)
        {
            float angle = startAngle + i * angleStep;
            var pos = corner + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            drawList.PrimWriteVtx(pos, uv, outerColor);
        }

        // Write triangle indices (fan from center)
        for (int i = 0; i < segments; i++)
        {
            drawList.PrimWriteIdx((ushort)vtxBase);           // center
            drawList.PrimWriteIdx((ushort)(vtxBase + 1 + i)); // current arc point
            drawList.PrimWriteIdx((ushort)(vtxBase + 2 + i)); // next arc point
        }
    }
}
