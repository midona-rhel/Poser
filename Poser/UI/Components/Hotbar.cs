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

    private static readonly string[] PivotNames = { "Local", "Parent", "Average" };
    private static readonly string[] OrientationNames = { "Local", "Global", "Parent" };
    private static readonly string[] SymmetryNames = { "Off", "Copy", "Mirror" };
    private static readonly string[] ViewModeNames = { "Dots", "Octahedra", "Joints" };

    public Hotbar(IEditorState editorState)
    {
        _editorState = editorState;
    }

    public void Draw()
    {
        float buttonSize = ImGui.GetFrameHeight();
        float comboWidth = 100f;
        float buttonSpacing = 2f;

        // Transform Pivot selector
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Pivot:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(comboWidth);
        int currentPivot = (int)_editorState.TransformPivot;
        if (ImGui.Combo("##pivot", ref currentPivot, PivotNames, PivotNames.Length))
        {
            _editorState.TransformPivot = (TransformPivot)currentPivot;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.TransformPivot switch
            {
                TransformPivot.Local => "Gizmo on first selected entity's position",
                TransformPivot.Parent => "Gizmo on parent of first selected (fallback to entity if no parent)",
                TransformPivot.Average => "Gizmo at average position of all selected entities",
                _ => ""
            });
        }

        ImGui.SameLine();

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
                TransformOrientation.Parent => "Use the parent bone's coordinate axes",
                _ => ""
            });
        }

        ImGui.SameLine();

        // Reset pivot/orientation button
        bool isDefault = _editorState.TransformPivot == TransformPivot.Local &&
                         _editorState.TransformOrientation == TransformOrientation.Local;
        using (ImRaii.Disabled(isDefault))
        {
            if (ImPoser.IconButton("reset_pivot", FontAwesomeIcon.Undo, new Vector2(buttonSize, buttonSize), "Reset to Local/Local"))
            {
                _editorState.TransformPivot = TransformPivot.Local;
                _editorState.TransformOrientation = TransformOrientation.Local;
            }
        }

        ImGui.SameLine(0, 16f); // Gap before symmetry

        // Symmetry mode dropdown
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Symmetry:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(comboWidth);
        int currentSymmetry = (int)_editorState.SymmetryMode;
        if (ImGui.Combo("##symmetry", ref currentSymmetry, SymmetryNames, SymmetryNames.Length))
        {
            _editorState.SymmetryMode = (SymmetryMode)currentSymmetry;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.SymmetryMode switch
            {
                SymmetryMode.Off => "No symmetry - only transform selected bone",
                SymmetryMode.Copy => "Paired bone gets the same transform (both arms up)",
                SymmetryMode.Mirror => "Paired bone gets mirrored transform (left up = right down)",
                _ => ""
            });
        }

        ImGui.SameLine();

        // Reset symmetry button
        using (ImRaii.Disabled(_editorState.SymmetryMode == SymmetryMode.Off))
        {
            if (ImPoser.IconButton("reset_symmetry", FontAwesomeIcon.Undo, new Vector2(buttonSize, buttonSize), "Reset Symmetry to Off"))
            {
                _editorState.SymmetryMode = SymmetryMode.Off;
            }
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
                SkeletonViewMode.Dots => "Skeleton View: Dots with lines",
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
