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

    private static readonly string[] PivotNames = { "Individual", "Parent", "Median" };
    private static readonly string[] OrientationNames = { "Local", "Global", "Parent" };

    public Hotbar(IEditorState editorState)
    {
        _editorState = editorState;
    }

    public void Draw()
    {
        float comboWidth = 100f;

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
                TransformPivot.Individual => "Transform around each object's own origin",
                TransformPivot.Parent => "Transform around the parent bone's position",
                TransformPivot.Median => "Transform around the median center of selected objects",
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

        // Right side buttons
        float buttonSize = ImGui.GetFrameHeight();
        float rightPadding = 8f;
        float buttonSpacing = 4f;

        // Calculate positions from right to left: debug, category toggle
        float debugButtonX = ImGui.GetContentRegionMax().X - buttonSize - rightPadding;
        float categoryButtonX = debugButtonX - buttonSize - buttonSpacing;

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
}
