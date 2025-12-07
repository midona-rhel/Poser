using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the top bar with GPose status, mode indicator, and undo/redo buttons.
/// </summary>
public class TopBar
{
    private readonly IGPoseService _gPoseService;
    private readonly IHistoryService _historyService;
    private readonly IActorManager _actorManager;
    private readonly IEditorState _editorState;

    public TopBar(IGPoseService gPoseService, IHistoryService historyService, IActorManager actorManager, IEditorState editorState)
    {
        _gPoseService = gPoseService;
        _historyService = historyService;
        _actorManager = actorManager;
        _editorState = editorState;
    }

    public void Draw()
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;

        DrawGPoseStatus();
        ImGui.SameLine();
        DrawModeIndicator();
        ImGui.SameLine();
        DrawUndoRedoButtons(windowWidth);
    }

    private void DrawGPoseStatus()
    {
        if (_gPoseService.IsGPosing)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "GPose");
        }
        else
        {
            ImGui.TextDisabled("Not in GPose");
        }
    }

    private void DrawModeIndicator()
    {
        // Only show mode when in GPose
        if (!_gPoseService.IsGPosing)
            return;

        ImGui.TextDisabled("|");
        ImGui.SameLine();

        var targetType = _editorState.GetGizmoTargetType();
        var selectedActor = _actorManager.PrimarySelectedActor;

        // Determine mode based on selection state
        if (targetType == GizmoTargetType.Bone)
        {
            // Posing mode - bone is selected
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), "Posing");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Posing bones. Press Tab to exit.");
            }
        }
        else if (selectedActor != null && selectedActor.IsEditMode)
        {
            // Posing mode enabled but no bone selected yet
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.3f, 1.0f), "Posing");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Click a bone to select it, or press Tab to exit.");
            }
        }
        else
        {
            // Actor mode - can only move actors
            ImGui.Text("Actor");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Moving actors. Press Tab or enable skeleton to pose bones.");
            }
        }
    }

    private void DrawUndoRedoButtons(float windowWidth)
    {
        float buttonSize = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float buttonsWidth = (buttonSize * 2) + spacing;
        float rightX = windowWidth - buttonsWidth;

        ImGui.SetCursorPosX(rightX);

        // Undo button
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using (ImRaii.Disabled(!_historyService.CanUndo))
            {
                if (ImGui.Button(FontAwesomeIcon.Undo.ToIconString(), new Vector2(buttonSize, buttonSize)))
                {
                    _historyService.Undo();
                }
            }
        }

        if (_historyService.CanUndo && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Undo: {_historyService.UndoDescription}");
        }

        ImGui.SameLine();

        // Redo button
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using (ImRaii.Disabled(!_historyService.CanRedo))
            {
                if (ImGui.Button(FontAwesomeIcon.Redo.ToIconString(), new Vector2(buttonSize, buttonSize)))
                {
                    _historyService.Redo();
                }
            }
        }

        if (_historyService.CanRedo && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Redo: {_historyService.RedoDescription}");
        }
    }
}
