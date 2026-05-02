using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Game.Structs;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Scene panel containing the entity hierarchy.
/// Injects services directly - reads state from services, calls methods on services.
/// </summary>
public class ScenePanel
{
    private readonly ISelectionService _selectionService;
    private readonly IActorSpawnService _spawnService;
    private readonly ILightingService? _lightingService;
    private readonly IVirtualCameraService? _virtualCameraService;
    private readonly EntityList _entityList;

    public ScenePanel(
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        IGPoseService gPoseService,
        IEditorState editorState,
        IActorSpawnService spawnService,
        ICameraService cameraService,
        ILightingService? lightingService = null,
        IVirtualCameraService? virtualCameraService = null)
    {
        _selectionService = selectionService;
        _spawnService = spawnService;
        _lightingService = lightingService;
        _virtualCameraService = virtualCameraService;

        _entityList = new EntityList(
            actorManager,
            selectionService,
            animationService,
            skeletonService,
            gPoseService,
            editorState,
            lightingService,
            virtualCameraService);
    }

    public void Draw()
    {
        ImGui.Text("Scene");
        ImGui.Spacing();

        // Calculate height for scrollable region (leave room for buttons)
        float buttonHeight = UIConstants.ScaledButtonSize + ImGui.GetStyle().ItemSpacing.Y * 2;
        float availableHeight = ImGui.GetContentRegionAvail().Y - buttonHeight;

        // Scrollable entity list region
        using (var child = ImRaii.Child("entity_list_scroll", new Vector2(-1, availableHeight), false))
        {
            if (child.Success)
            {
                _entityList.Draw();
            }
        }

        ImGui.Spacing();

        DrawBottomButtons();
    }

    private void DrawBottomButtons()
    {
        var primarySelected = _selectionService.GetFirstSelected<IActor>();

        using var row = Flex.Row(gap: Flex.ItemGap);

        // Add button on the left
        row.Fixed(Flex.RowHeight, () =>
        {
            if (Crystarium.IconButton(FontAwesomeIcon.Plus, new ButtonProps { Id = "add_entity", Tooltip = "Add entity" }))
            {
                ImGui.OpenPopup("##add_entity_popup");
            }
        });

        // Popup menu for add options (drawn outside Fixed but still works)
        if (ImGui.BeginPopup("##add_entity_popup"))
        {
            if (ImGui.MenuItem("Spawn Actor Clone"))
            {
                _spawnService.SpawnPlayerClone();
            }

            // Camera spawning option
            if (_virtualCameraService != null && _virtualCameraService.IsAvailable)
            {
                ImGui.Separator();

                if (ImGui.MenuItem("Create Camera"))
                {
                    var camera = _virtualCameraService.CreateCamera();
                    _selectionService.Select(camera);
                }
            }

            // Light spawning options
            if (_lightingService != null && _lightingService.IsAvailable)
            {
                ImGui.Separator();

                if (ImGui.MenuItem("Spawn Spot Light"))
                {
                    _lightingService.BeginPlacement(LightType.SpotLight);
                }

                if (ImGui.MenuItem("Spawn Point Light"))
                {
                    _lightingService.BeginPlacement(LightType.AreaLight);
                }

                if (ImGui.MenuItem("Spawn Flat Light"))
                {
                    _lightingService.BeginPlacement(LightType.FlatLight);
                }
            }

            ImGui.EndPopup();
        }

        row.Spacer();

        // Delete button on the right
        var selectedLight = _selectionService.GetFirstSelected<LightEntity>();
        var selectedCamera = _selectionService.GetFirstSelected<VirtualCameraEntity>();
        bool canDeleteActor = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);
        bool canDeleteLight = selectedLight != null && _lightingService != null && _lightingService.IsSpawnedLight(selectedLight);
        bool canDeleteCamera = selectedCamera != null && _virtualCameraService != null && _virtualCameraService.IsVirtualCamera(selectedCamera);
        bool canDelete = canDeleteActor || canDeleteLight || canDeleteCamera;

        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            using (ImRaii.Disabled(!canDelete))
            {
                if (Crystarium.Button("Delete", new ButtonProps { Id = "delete_selected", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                {
                    if (canDeleteCamera && selectedCamera != null)
                    {
                        _virtualCameraService!.DeleteCamera(selectedCamera);
                    }
                    else if (canDeleteLight && selectedLight != null)
                    {
                        _lightingService!.DestroyLight(selectedLight);
                    }
                    else if (canDeleteActor && primarySelected != null)
                    {
                        _spawnService.DestroyActor(primarySelected);
                    }
                }
            }
        });
    }
}
