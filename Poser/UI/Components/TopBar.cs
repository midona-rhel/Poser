using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the top bar with GPose status, posing mode toggle, and undo/redo buttons.
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
        DrawPosingModeToggle();
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

    private void DrawPosingModeToggle()
    {
        // Only show when in GPose
        if (!_gPoseService.IsGPosing)
            return;

        ImGui.TextDisabled("|");
        ImGui.SameLine();

        var isPosingMode = _editorState.IsPosingMode;
        float buttonHeight = ImGui.GetFrameHeight();

        // Toggle icon button
        var toggleIcon = isPosingMode ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        var toggleColor = isPosingMode
            ? new Vector4(1.0f, 0.7f, 0.3f, 1.0f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1.0f);

        using (ImRaii.PushColor(ImGuiCol.Text, toggleColor))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{toggleIcon.ToIconString()}##pose_toggle", new Vector2(buttonHeight, buttonHeight)))
            {
                _editorState.TogglePosingMode();
            }
        }

        if (ImGui.IsItemHovered())
        {
            var tooltip = isPosingMode
                ? "Pose Mode ON - All actors frozen. Click to exit."
                : "Pose Mode OFF - Click to freeze actors and enable bone posing.";
            ImGui.SetTooltip(tooltip);
        }

        ImGui.SameLine();

        // Label
        if (isPosingMode)
        {
            ImGui.TextColored(toggleColor, "Pose");
        }
        else
        {
            ImGui.TextDisabled("Pose");
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
