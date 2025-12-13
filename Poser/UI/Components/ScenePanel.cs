using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
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
    private readonly EntityList _entityList;

    public ScenePanel(
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        IGPoseService gPoseService,
        IEditorState editorState,
        IActorSpawnService spawnService,
        ICameraService cameraService)
    {
        _selectionService = selectionService;
        _spawnService = spawnService;

        _entityList = new EntityList(
            actorManager,
            selectionService,
            animationService,
            skeletonService,
            gPoseService,
            editorState);
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
            if (PoserButton.DrawIcon("add_entity", FontAwesomeIcon.Plus, "Add entity"))
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

            ImGui.EndPopup();
        }

        row.Spacer();

        // Delete button on the right
        bool canDelete = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);
        string deleteTooltip = canDelete
            ? "Delete selected entity"
            : "Can only delete spawned entities";

        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            using (ImRaii.Disabled(!canDelete))
            {
                if (PoserButton.DrawWithWidth("delete_selected", "Delete", w))
                {
                    if (canDelete && primarySelected != null)
                    {
                        _spawnService.DestroyActor(primarySelected);
                    }
                }
            }
        });
    }
}
