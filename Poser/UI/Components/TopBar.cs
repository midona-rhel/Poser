using System;
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

    /// <summary>
    /// Event fired when user clicks the Environment button.
    /// </summary>
    public event Action? OnEnvironmentRequested;

    /// <summary>
    /// Event fired when user clicks the References button.
    /// </summary>
    public event Action? OnReferencesRequested;

    /// <summary>
    /// Event fired when user clicks the Library button.
    /// </summary>
    public event Action? OnLibraryRequested;

    /// <summary>
    /// Event fired when user clicks the Body Map button.
    /// </summary>
    public event Action? OnBodyMapRequested;

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
        using var row = Flex.Row(gap: Flex.ItemGap);

        // GPose status on the left
        row.Fill((w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            if (_gPoseService.IsGPosing)
            {
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "GPose");
            }
            else
            {
                ImGui.TextDisabled("Not in GPose");
            }
        });

        // Library button
        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("library", "Lib", w))
            {
                OnLibraryRequested?.Invoke();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Pose Library");
            }
        });

        // Body Map button
        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("bodymap", "Body", w))
            {
                OnBodyMapRequested?.Invoke();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Graphical Bone Selection");
            }
        });

        // References button
        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("references", "Ref", w))
            {
                OnReferencesRequested?.Invoke();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Reference Images");
            }
        });

        // Environment button
        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("environment", "Env", w))
            {
                OnEnvironmentRequested?.Invoke();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Time & Weather");
            }
        });

        // Settings button on the right
        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("settings", "Settings", w))
            {
                _settingsModal.Open();
            }
        });

        // Draw settings modal (outside row)
        _settingsModal.Draw();
    }
}
