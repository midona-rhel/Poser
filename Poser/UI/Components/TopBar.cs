using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Data.Config;
using Poser.Services;
using Poser.UI.Controls;

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
        if (!_gPoseService.IsGPosing)
            return;

        ImGui.TextDisabled("|");
        ImGui.SameLine();

        float buttonHeight = ImGui.GetFrameHeight();
        bool isPosingMode = _editorState.IsPosingMode;

        var toggleIcon = isPosingMode ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff;
        var toggleColor = isPosingMode ? UIConstants.ActiveColor : UIConstants.InactiveColor;

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
                ImGui.OpenPopup("##settings_popup");
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }

        // Settings popup menu
        if (ImGui.BeginPopup("##settings_popup"))
        {
            ImGui.TextDisabled("Display");
            ImGui.Separator();

            var showNsfw = PoserSettings.Instance.ShowNsfwBones;
            if (ImGui.Checkbox("Show NSFW Bones", ref showNsfw))
            {
                PoserSettings.Instance.ShowNsfwBones = showNsfw;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Show IVCS genitalia and other adult content bones");
            }

            ImGui.EndPopup();
        }
    }
}
