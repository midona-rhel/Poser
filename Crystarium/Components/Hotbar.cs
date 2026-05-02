using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Bottom hotbar with transform tools and options.
/// </summary>
public class Hotbar
{
    private readonly IEditorState _editorState;

    private static readonly string[] OrientationNames = { "Local", "Global" };
    private static readonly string[] ViewModeNames = { "Default", "Octahedra", "Joints" };
    private static readonly string[] SymmetryModeNames = { "Off", "Copy", "Mirror" };

    public Hotbar(IEditorState editorState)
    {
        _editorState = editorState;
    }

    public void Draw()
    {
        float buttonSize = ImGui.GetFrameHeight();
        float comboWidth = 100f;
        float buttonSpacing = 2f;

        // Transform Orientation selector
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Orientation:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(comboWidth);
        int currentOrientation = (int)_editorState.TransformOrientation;
        if (ImGui.Combo("##orientation", ref currentOrientation, OrientationNames, OrientationNames.Length))
        {
            _editorState.TransformOrientation = (TransformOrientation)currentOrientation;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.TransformOrientation switch
            {
                TransformOrientation.Local => "Use the object's local coordinate axes",
                TransformOrientation.Global => "Use world coordinate axes",
                _ => ""
            });
        }

        ImGui.SameLine(0, 12f);

        // Symmetry mode selector
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Symmetry:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(comboWidth);
        int currentSymmetry = (int)_editorState.SymmetryMode;
        if (ImGui.Combo("##symmetry", ref currentSymmetry, SymmetryModeNames, SymmetryModeNames.Length))
        {
            _editorState.SymmetryMode = (SymmetryMode)currentSymmetry;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.SymmetryMode switch
            {
                SymmetryMode.Off => "No symmetry - only selected bones transform",
                SymmetryMode.Copy => "Copy - paired bone (_l/_r) gets same transform",
                SymmetryMode.Mirror => "Mirror - paired bone gets mirrored transform",
                _ => ""
            });
        }

        ImGui.SameLine(0, 16f); // Gap before tool buttons

        // Tool selection buttons (Move, Rotate, Scale, Universal)
        DrawToolButton(TransformTool.Move, FontAwesomeIcon.ArrowsAlt, "Move (G)", buttonSize);
        ImGui.SameLine(0, buttonSpacing);
        DrawToolButton(TransformTool.Rotate, FontAwesomeIcon.Sync, "Rotate (R)", buttonSize);
        ImGui.SameLine(0, buttonSpacing);
        DrawToolButton(TransformTool.Scale, FontAwesomeIcon.ExpandAlt, "Scale (S)", buttonSize);
        ImGui.SameLine(0, buttonSpacing);
        DrawToolButton(TransformTool.Universal, FontAwesomeIcon.Atom, "Universal - Move, Rotate & Scale (U)", buttonSize);

        // Right side buttons
        float rightPadding = 8f;
        float rightButtonSpacing = 4f;

        // Calculate positions from right to left: debug, category toggle, show selected, view mode dropdown
        float debugButtonX = ImGui.GetContentRegionMax().X - buttonSize - rightPadding;
        float categoryButtonX = debugButtonX - buttonSize - rightButtonSpacing;
        float showSelectedButtonX = categoryButtonX - buttonSize - rightButtonSpacing;
        float viewModeComboWidth = 85f;
        float viewModeX = showSelectedButtonX - viewModeComboWidth - rightButtonSpacing;

        // View mode dropdown
        ImGui.SameLine(viewModeX);
        ImGui.SetNextItemWidth(viewModeComboWidth);
        int currentViewMode = (int)_editorState.SkeletonViewMode;
        if (ImGui.Combo("##viewmode", ref currentViewMode, ViewModeNames, ViewModeNames.Length))
        {
            _editorState.SkeletonViewMode = (SkeletonViewMode)currentViewMode;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.SkeletonViewMode switch
            {
                SkeletonViewMode.Default => "Skeleton View: Dots with lines",
                SkeletonViewMode.Octahedra => "Skeleton View: Blender-style bone shapes",
                SkeletonViewMode.Joints => "Skeleton View: Joints only (no lines)",
                _ => ""
            });
        }

        // Show selected bones toggle
        ImGui.SameLine(showSelectedButtonX);
        var showSelectedIcon = _editorState.ShowSelectedBonesOnly ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        var showSelectedColor = _editorState.ShowSelectedBonesOnly
            ? UIConstants.SkeletonColor
            : UIConstants.DisabledTextColor;
        var showSelectedTooltip = _editorState.ShowSelectedBonesOnly
            ? "Show Selected Bones Only: ON (click to show all)"
            : "Show Selected Bones Only: OFF (click to filter)";

        using (ImRaii.PushColor(ImGuiCol.Text, showSelectedColor))
        {
            if (ImPoser.IconButton("show_selected_toggle", showSelectedIcon, new Vector2(buttonSize, buttonSize), showSelectedTooltip))
            {
                _editorState.ShowSelectedBonesOnly = !_editorState.ShowSelectedBonesOnly;
            }
        }

        // Category toggle button
        ImGui.SameLine(categoryButtonX);
        var categoryIcon = _editorState.BoneDisplayMode == BoneDisplayMode.Category
            ? FontAwesomeIcon.LayerGroup  // Category mode
            : FontAwesomeIcon.Sitemap;    // Hierarchy mode
        var categoryColor = UIConstants.DefaultIconColor;
        var categoryTooltip = _editorState.BoneDisplayMode == BoneDisplayMode.Category
            ? "Bone Display: Category (click for Hierarchy)"
            : "Bone Display: Hierarchy (click for Category)";

        using (ImRaii.PushColor(ImGuiCol.Text, categoryColor))
        {
            if (ImPoser.IconButton("category_toggle", categoryIcon, new Vector2(buttonSize, buttonSize), categoryTooltip))
            {
                _editorState.BoneDisplayMode = _editorState.BoneDisplayMode == BoneDisplayMode.Category
                    ? BoneDisplayMode.Hierarchy
                    : BoneDisplayMode.Category;
            }
        }

        // Debug toggle
        ImGui.SameLine(debugButtonX);
        var debugColor = _editorState.DebugMode
            ? new Vector4(1f, 0.5f, 0f, 1f)  // Orange when active
            : UIConstants.DisabledTextColor;

        using (ImRaii.PushColor(ImGuiCol.Text, debugColor))
        {
            if (ImPoser.IconButton("debug_toggle", FontAwesomeIcon.Bug, new Vector2(buttonSize, buttonSize), "Debug Mode: Expand all entities, log untranslated bones"))
            {
                _editorState.DebugMode = !_editorState.DebugMode;
            }
        }
    }

    private void DrawToolButton(TransformTool tool, FontAwesomeIcon icon, string tooltip, float size)
    {
        bool isActive = _editorState.TransformTool == tool;
        var color = isActive ? UIConstants.SkeletonColor : UIConstants.DisabledTextColor;

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (ImPoser.CenteredIconButton($"tool_{tool}", icon, new Vector2(size, size), tooltip))
            {
                _editorState.TransformTool = tool;
            }
        }
    }
}
