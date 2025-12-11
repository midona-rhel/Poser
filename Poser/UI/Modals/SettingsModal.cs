using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Config;

namespace Poser.UI.Modals;

/// <summary>
/// Settings modal with category tabs on the left side.
/// Tabbed notebook visual - active tab blends seamlessly with content panel.
/// </summary>
public class SettingsModal
{
    private bool _isOpen;
    private int _activeTabIndex;

    private const float ModalWidth = 650f;
    private const float ModalHeight = 420f;
    private const float TabBarWidth = 120f;
    private const float TabHeight = 32f;
    private const float TabRounding = 6f;
    private const float ContentPadding = 16f;

    private static readonly string[] TabNames = { "Skeleton", "Display" };

    public void Open()
    {
        _isOpen = true;
        ImGui.OpenPopup("Settings##poser_settings_modal");
    }

    public void Draw()
    {
        if (!_isOpen)
            return;

        var displaySize = ImGui.GetIO().DisplaySize;
        var modalSize = new Vector2(ModalWidth, ModalHeight) * ImGuiHelpers.GlobalScale;

        ImGui.SetNextWindowPos(
            new Vector2((displaySize.X - modalSize.X) / 2, (displaySize.Y - modalSize.Y) / 2),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(modalSize, ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;

        if (ImGui.BeginPopupModal("Settings##poser_settings_modal", ref _isOpen, flags))
        {
            DrawContent();
            ImGui.EndPopup();
        }
    }

    private void DrawContent()
    {
        var style = ImGui.GetStyle();
        var availableSize = ImGui.GetContentRegionAvail();
        var drawList = ImGui.GetWindowDrawList();

        float tabBarWidthScaled = TabBarWidth * ImGuiHelpers.GlobalScale;
        float contentWidth = availableSize.X - tabBarWidthScaled;
        float tabHeightScaled = TabHeight * ImGuiHelpers.GlobalScale;
        float spacing = 4f * ImGuiHelpers.GlobalScale;
        float roundingScaled = TabRounding * ImGuiHelpers.GlobalScale;

        var tabBarStart = ImGui.GetCursorScreenPos();

        // Get colors
        var contentBgColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg];
        var borderColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Border];
        var borderColorU32 = ImGui.ColorConvertFloat4ToU32(borderColor);
        // Brighter border for inactive tabs
        var brightBorderColor = new Vector4(
            Math.Min(borderColor.X * 1.5f, 1f),
            Math.Min(borderColor.Y * 1.5f, 1f),
            Math.Min(borderColor.Z * 1.5f, 1f),
            borderColor.W);
        var brightBorderU32 = ImGui.ColorConvertFloat4ToU32(brightBorderColor);

        // Calculate active tab position
        float activeTabTop = tabBarStart.Y + _activeTabIndex * (tabHeightScaled + spacing);
        float activeTabBottom = activeTabTop + tabHeightScaled;

        // Draw tab bar on the left (no border)
        using (var child = ImRaii.Child("##settings_tabs", new Vector2(tabBarWidthScaled, availableSize.Y), false))
        {
            if (child.Success)
            {
                DrawTabBar(contentBgColor, brightBorderU32);
            }
        }

        ImGui.SameLine(0, 0);

        var contentPanelPos = ImGui.GetCursorScreenPos();
        var contentPanelEnd = contentPanelPos + new Vector2(contentWidth, availableSize.Y);

        // Draw content area WITHOUT border (we'll draw custom border)
        float paddingScaled = ContentPadding * ImGuiHelpers.GlobalScale;
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(paddingScaled, paddingScaled)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 0f)) // No automatic border
        using (var child = ImRaii.Child("##settings_content", new Vector2(contentWidth, availableSize.Y), true))
        {
            if (child.Success)
            {
                DrawActiveTabContent();
            }
        }

        // Draw custom content panel border that skips active tab area
        // Top border
        drawList.AddLine(contentPanelPos, new Vector2(contentPanelEnd.X, contentPanelPos.Y), borderColorU32, 1f);
        // Right border
        drawList.AddLine(new Vector2(contentPanelEnd.X, contentPanelPos.Y), contentPanelEnd, borderColorU32, 1f);
        // Bottom border
        drawList.AddLine(new Vector2(contentPanelPos.X, contentPanelEnd.Y), contentPanelEnd, borderColorU32, 1f);

        // Left border - skip where active tab is
        // Above active tab
        if (activeTabTop > tabBarStart.Y)
        {
            drawList.AddLine(contentPanelPos, new Vector2(contentPanelPos.X, activeTabTop), borderColorU32, 1f);
        }
        // Below active tab
        if (activeTabBottom < tabBarStart.Y + availableSize.Y)
        {
            drawList.AddLine(
                new Vector2(contentPanelPos.X, activeTabBottom),
                new Vector2(contentPanelPos.X, contentPanelEnd.Y),
                borderColorU32, 1f);
        }

        // Draw content panel shadow over inactive tabs (but not active tab)
        DrawContentPanelShadow(drawList, contentPanelPos, availableSize.Y, tabBarStart);
    }

    private void DrawContentPanelShadow(ImDrawListPtr drawList, Vector2 contentPos, float height, Vector2 tabBarStart)
    {
        float tabHeightScaled = TabHeight * ImGuiHelpers.GlobalScale;
        float spacing = 4f * ImGuiHelpers.GlobalScale;
        float shadowWidth = 10f * ImGuiHelpers.GlobalScale;

        float activeTabTop = tabBarStart.Y + _activeTabIndex * (tabHeightScaled + spacing);
        float activeTabBottom = activeTabTop + tabHeightScaled;

        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.4f));
        var transparent = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0f));

        // Shadow above active tab
        if (activeTabTop > tabBarStart.Y)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowWidth, tabBarStart.Y),
                new Vector2(contentPos.X, activeTabTop),
                transparent, shadowColor, shadowColor, transparent);
        }

        // Shadow below active tab
        float bottomY = tabBarStart.Y + height;
        if (activeTabBottom < bottomY)
        {
            drawList.AddRectFilledMultiColor(
                new Vector2(contentPos.X - shadowWidth, activeTabBottom),
                new Vector2(contentPos.X, bottomY),
                transparent, shadowColor, shadowColor, transparent);
        }
    }

    private void DrawTabBar(Vector4 contentBgColor, uint brightBorderU32)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursorStart = ImGui.GetCursorScreenPos();

        float tabHeightScaled = TabHeight * ImGuiHelpers.GlobalScale;
        float tabWidthScaled = TabBarWidth * ImGuiHelpers.GlobalScale;
        float roundingScaled = TabRounding * ImGuiHelpers.GlobalScale;
        float spacing = 4f * ImGuiHelpers.GlobalScale;

        var borderColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Border];
        var borderColorU32 = ImGui.ColorConvertFloat4ToU32(borderColor);

        for (int i = 0; i < TabNames.Length; i++)
        {
            var tabPos = cursorStart + new Vector2(0, i * (tabHeightScaled + spacing));
            var tabSize = new Vector2(tabWidthScaled, tabHeightScaled);
            var tabEnd = tabPos + tabSize;

            bool isActive = _activeTabIndex == i;
            bool isHovered = ImGui.IsMouseHoveringRect(tabPos, tabEnd);

            if (isActive)
            {
                // Active tab: same background as content panel, blends seamlessly
                var contentBgU32 = ImGui.ColorConvertFloat4ToU32(contentBgColor);
                drawList.AddRectFilled(tabPos, tabEnd, contentBgU32, roundingScaled, ImDrawFlags.RoundCornersLeft);

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
            else
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

                // Background
                var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.12f, 1f));
                drawList.AddRectFilled(tabPos, tabEnd, bgColor, roundingScaled, ImDrawFlags.RoundCornersLeft);

                if (isHovered)
                {
                    var hoverColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));
                    drawList.AddRectFilled(tabPos, tabEnd, hoverColor, roundingScaled, ImDrawFlags.RoundCornersLeft);
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

            // Draw text
            var textColor = isActive
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 1));

            var textSize = ImGui.CalcTextSize(TabNames[i]);
            var textPos = tabPos + (tabSize - textSize) * 0.5f;
            drawList.AddText(textPos, textColor, TabNames[i]);

            // Handle click
            if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _activeTabIndex = i;
            }

            ImGui.SetCursorScreenPos(tabPos);
            ImGui.Dummy(tabSize);
        }
    }

    private void DrawActiveTabContent()
    {
        // Add padding inside content area
        float padding = ContentPadding * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(padding, padding));

        using (ImRaii.Child("##content_inner", ImGui.GetContentRegionAvail() - new Vector2(padding, padding), false))
        {
            switch (_activeTabIndex)
            {
                case 0:
                    DrawSkeletonTab();
                    break;
                case 1:
                    DrawDisplayTab();
                    break;
            }
        }
    }

    private void DrawSkeletonTab()
    {
        var config = ConfigurationService.Instance.Config.Skeleton;

        ImGui.TextDisabled("Bone Display");
        ImGui.Separator();
        ImGui.Spacing();

        float dotRadius = config.BoneDotRadius;
        if (DrawSliderRow("Dot Radius:", ref dotRadius, 1f, 10f))
            config.BoneDotRadius = dotRadius;

        float lineThickness = config.BoneLineThickness;
        if (DrawSliderRow("Line Thickness:", ref lineThickness, 0.5f, 5f))
            config.BoneLineThickness = lineThickness;

        float lineOpacity = config.BoneLineOpacity;
        if (DrawSliderRow("Line Opacity:", ref lineOpacity, 0f, 1f))
            config.BoneLineOpacity = lineOpacity;

        float octahedraWidth = config.OctahedraWidth;
        if (DrawSliderRow("Octahedra Width:", ref octahedraWidth, 1f, 10f))
            config.OctahedraWidth = octahedraWidth;

        ImGui.Spacing();
        ImGui.TextDisabled("Colors");
        ImGui.Separator();
        ImGui.Spacing();

        uint boneColor = config.BoneColor;
        if (DrawColorRow("Bone Color:", ref boneColor))
            config.BoneColor = boneColor;

        uint outlineColor = config.BoneOutlineColor;
        if (DrawColorRow("Outline Color:", ref outlineColor))
            config.BoneOutlineColor = outlineColor;

        uint selectedColor = config.SelectedBoneColor;
        if (DrawColorRow("Selected Bone:", ref selectedColor))
            config.SelectedBoneColor = selectedColor;

        uint modifiedColor = config.ModifiedBoneColor;
        if (DrawColorRow("Modified Bone:", ref modifiedColor))
            config.ModifiedBoneColor = modifiedColor;

        uint hoveredColor = config.HoveredBoneColor;
        if (DrawColorRow("Hovered Bone:", ref hoveredColor))
            config.HoveredBoneColor = hoveredColor;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetSkeleton();
        }
    }

    private void DrawDisplayTab()
    {
        var config = ConfigurationService.Instance.Config.Display;

        ImGui.TextDisabled("Visibility");
        ImGui.Separator();
        ImGui.Spacing();

        bool showNsfw = config.ShowNsfwBones;
        if (DrawCheckboxRow("Show NSFW Bones:", ref showNsfw))
            config.ShowNsfwBones = showNsfw;

        bool anonymous = config.AnonymousMode;
        if (DrawCheckboxRow("Anonymous Mode:", ref anonymous))
            config.AnonymousMode = anonymous;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults"))
        {
            ConfigurationService.Instance.ResetDisplay();
        }
    }

    private const float LabelWidth = 120f;

    private static bool DrawSliderRow(string label, ref float value, float min, float max)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.SliderFloat($"##{label}", ref value, min, max))
        {
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }

    private static bool DrawColorRow(string label, ref uint color)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);
        var colorVec = ImGui.ColorConvertU32ToFloat4(color);
        if (ImGui.ColorEdit4($"##{label}", ref colorVec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            color = ImGui.ColorConvertFloat4ToU32(colorVec);
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }

    private static bool DrawCheckboxRow(string label, ref bool value)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(LabelWidth * ImGuiHelpers.GlobalScale);
        if (ImGui.Checkbox($"##{label}", ref value))
        {
            ConfigurationService.Instance.Save();
            return true;
        }
        return false;
    }
}
