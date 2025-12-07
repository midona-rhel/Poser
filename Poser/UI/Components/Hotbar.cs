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

    private static readonly string[] PivotModeNames = { "Local", "World", "Average" };

    public Hotbar(IEditorState editorState)
    {
        _editorState = editorState;
    }

    public void Draw()
    {
        // Vertically center text with the combo box
        float comboHeight = ImGui.GetFrameHeight();
        float textHeight = ImGui.GetTextLineHeight();
        float offsetY = (comboHeight - textHeight) / 2;

        var cursorPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosY(cursorPos.Y + offsetY);
        ImGui.Text("Pivot:");
        ImGui.SameLine();

        ImGui.SetCursorPosY(cursorPos.Y);
        ImGui.SetNextItemWidth(100f);
        int currentMode = (int)_editorState.PivotMode;
        if (ImGui.Combo("##pivot_mode", ref currentMode, PivotModeNames, PivotModeNames.Length))
        {
            _editorState.PivotMode = (PivotMode)currentMode;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_editorState.PivotMode switch
            {
                PivotMode.Local => "Transform around each object's local origin",
                PivotMode.World => "Transform around world origin",
                PivotMode.Average => "Transform around the average center of selected objects",
                _ => ""
            });
        }

        // Right side buttons
        float buttonSize = comboHeight;
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
