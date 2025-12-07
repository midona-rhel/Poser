using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Controllers;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Scene panel containing the entity hierarchy.
/// Uses EntityList for rendering and EventBus for state management.
/// </summary>
public class ScenePanel : IDisposable
{
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnService _spawnService;
    private readonly EntityList _entityList;

    public ScenePanel(
        IActorManager actorManager,
        IAnimationService animationService,
        IActorSpawnService spawnService,
        ICameraService cameraService,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IEventBus eventBus,
        IPosingController controller)
    {
        _actorManager = actorManager;
        _spawnService = spawnService;
        _entityList = new EntityList(actorManager, animationService, spawnService, cameraService, gPoseService, skeletonService, editorState, eventBus, controller);
    }

    public void Draw()
    {
        ImGui.Text("Scene");
        ImGui.Spacing();

        // Calculate height for scrollable region (leave room for buttons)
        float buttonHeight = UIConstants.ScaledButtonSize + ImGui.GetStyle().ItemSpacing.Y * 2;
        float availableHeight = ImGui.GetContentRegionAvail().Y - buttonHeight;

        // Scrollable entity list region
        using (var child = ImRaii.Child("entity_list_scroll", new System.Numerics.Vector2(-1, availableHeight), false))
        {
            if (child.Success)
            {
                _entityList.Draw();
            }
        }

        ImGui.Spacing();

        // Plus and Delete buttons at bottom (outside scroll)
        DrawBottomButtons();
    }

    private void DrawBottomButtons()
    {
        bool hasSelection = _actorManager.SelectedActors.Count > 0;
        float buttonSize = UIConstants.ScaledButtonSize;

        // Match ActorList constants for alignment
        float iconColWidth = 32f * ImGuiHelpers.GlobalScale;
        float cellPadding = 4f * ImGuiHelpers.GlobalScale;

        // Center button like icons in table: cellPadding + (contentWidth - buttonSize) / 2
        float cellContentWidth = iconColWidth - (cellPadding * 2);
        float offsetX = cellPadding + (cellContentWidth - buttonSize) / 2;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        // Plus button aligned with table icon column
        if (ImPoser.CenteredIconButton(
            "spawn_clone",
            FontAwesomeIcon.Plus,
            new System.Numerics.Vector2(buttonSize, buttonSize),
            "Spawn clone of player"))
        {
            var spawned = _spawnService.SpawnPlayerClone();
            if (spawned != null)
            {
                // Select the newly spawned actor
                _actorManager.Select(spawned);
            }
        }

        ImGui.SameLine();

        // Trash button right-aligned
        float trashButtonWidth = buttonSize * 4;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - trashButtonWidth);

        // Only allow deleting spawned actors
        var primarySelected = _actorManager.PrimarySelectedActor;
        bool canDelete = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);

        using (ImRaii.Disabled(!canDelete))
        {
            if (ImPoser.FontIconButton(
                "delete_selected",
                FontAwesomeIcon.Trash,
                new System.Numerics.Vector2(trashButtonWidth, buttonSize),
                canDelete ? "Delete selected entity" : "Can only delete spawned entities",
                canDelete))
            {
                if (primarySelected != null)
                {
                    _spawnService.DestroyActor(primarySelected);
                }
            }
        }
    }

    public void Dispose()
    {
        _entityList.Dispose();
    }
}
