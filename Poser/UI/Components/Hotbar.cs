using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;

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
    }
}
