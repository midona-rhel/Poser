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
