using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Modals;

namespace Poser.UI.Components;

/// <summary>
/// Renders the top bar with GPose status, posing mode toggle, and undo/redo buttons.
/// Injects services directly - reads state from services, calls methods on services.
/// </summary>
public class TopBar
{
    private readonly IGPoseService _gPoseService;
    private readonly IEditorState _editorState;
    private readonly IHistoryService _historyService;
    private readonly SettingsModal _settingsModal = new();

    public TopBar(
        IGPoseService gPoseService,
        IEditorState editorState,
        IHistoryService historyService)
    {
        _gPoseService = gPoseService;
        _editorState = editorState;
        _historyService = historyService;
    }

    public void Draw()
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;

        DrawGPoseStatus();
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

    private void DrawUndoRedoButtons(float windowWidth)
    {
        float buttonSize = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float buttonsWidth = (buttonSize * 3) + (spacing * 2); // 3 buttons: settings, undo, redo
        float rightX = windowWidth - buttonsWidth;

        ImGui.SetCursorPosX(rightX);

        // Settings button
        DrawSettingsButton(buttonSize);
        ImGui.SameLine();

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

    private void DrawSettingsButton(float buttonSize)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString(), new Vector2(buttonSize, buttonSize)))
            {
                _settingsModal.Open();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }

        // Draw settings modal
        _settingsModal.Draw();
    }
}
