using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.UI;

public partial class MainWindow
{
    private bool _bulkDestroyOpen;
    private string _bulkDestroyTitle = string.Empty;
    private string _bulkDestroyDescription = string.Empty;
    private Action? _bulkDestroy;

    private void ConfirmDestroyAllLights()
    {
        var lights = _scene.Snapshot.Lights.ToArray();
        if (lights.Length == 0)
            return;
        int spawned = lights.Count(light => light.Ownership == LightOwnership.Spawned);
        _bulkDestroyTitle = "Destroy all lights?";
        _bulkDestroyDescription = $"Are you sure?\nDestroy {spawned} spawned lights.\n"
            + $"Release {lights.Length - spawned} captured lights back to the game.";
        var ids = lights.Select(light => SelectionId.ForLight(light.Id)).ToArray();
        _bulkDestroy = () => _ = RemoveEntitiesSafely(ids);
        _bulkDestroyOpen = true;
    }

    private void ConfirmDestroyAllCameras()
    {
        var cameras = _scene.Snapshot.Cameras.Where(camera => !camera.IsDefault).ToArray();
        if (cameras.Length == 0)
            return;
        _bulkDestroyTitle = "Destroy all cameras?";
        _bulkDestroyDescription = $"Are you sure?\nDestroy {cameras.Length} cameras.\nThe main camera will be kept.";
        var ids = cameras.Select(camera => SelectionId.ForCamera(camera.Id)).ToArray();
        _bulkDestroy = () => _ = RemoveEntitiesSafely(ids);
        _bulkDestroyOpen = true;
    }

    private void ConfirmDestroyAllProps()
    {
        var ids = _scene.Snapshot.Props.Select(prop => SelectionId.ForProp(prop.Id)).ToArray();
        if (ids.Length == 0)
            return;
        _bulkDestroyTitle = "Destroy all objects?";
        _bulkDestroyDescription = $"Are you sure?\nDestroy {ids.Length} spawned objects.\nBorrowed world scenery will be kept.";
        _bulkDestroy = () => _ = RemoveEntitiesSafely(ids);
        _bulkDestroyOpen = true;
    }

    private void ConfirmDestroyAllOverlays()
    {
        var ids = _scene.Snapshot.Overlays.Select(overlay => SelectionId.ForOverlay(overlay.Id)).ToArray();
        if (ids.Length == 0)
            return;
        _bulkDestroyTitle = "Destroy all overlays?";
        _bulkDestroyDescription = $"Are you sure?\nDestroy {ids.Length} overlays.\nReference images will be kept.";
        _bulkDestroy = () => _ = RemoveEntitiesSafely(ids);
        _bulkDestroyOpen = true;
    }

    private void DrawBulkDestroyModal()
    {
        Crystarium.Modal("##bulk-destroy", _bulkDestroyOpen,
            open =>
            {
                _bulkDestroyOpen = open;
                if (!open)
                    _bulkDestroy = null;
            },
            _bulkDestroyTitle,
            body: () =>
            {
                Crystarium.Text(_bulkDestroyDescription, default,
                    TextConstraint.Wrap(ImGui.GetContentRegionAvail().X,
                        whitespace: TextWhitespace.PreLine));
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    _bulkDestroyOpen = false;
                    _bulkDestroy = null;
                }
            },
            footer: () =>
            {
                if (Crystarium.Button("Cancel", id: "bulk-destroy-cancel"))
                {
                    _bulkDestroyOpen = false;
                    _bulkDestroy = null;
                }
                ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
                if (Crystarium.Button("Destroy all", variant: ButtonVariant.Danger,
                        id: "bulk-destroy-confirm") && _bulkDestroyOpen)
                {
                    var destroy = _bulkDestroy;
                    _bulkDestroyOpen = false;
                    _bulkDestroy = null;
                    destroy?.Invoke();
                }
                if (!_bulkDestroyOpen)
                    ImGui.CloseCurrentPopup();
            });
    }
}
