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
        IGPoseService gPoseService,
        IEditorState editorState,
        IActorSpawnService spawnService)
    {
        _selectionService = selectionService;
        _spawnService = spawnService;

        _entityList = new EntityList(
            actorManager,
            selectionService,
            animationService,
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
        float buttonSize = UIConstants.ScaledButtonSize;

        // Match ActorList constants for alignment
        float iconColWidth = 32f * ImGuiHelpers.GlobalScale;
        float cellPadding = 4f * ImGuiHelpers.GlobalScale;

        // Center button like icons in table
        float cellContentWidth = iconColWidth - (cellPadding * 2);
        float offsetX = cellPadding + (cellContentWidth - buttonSize) / 2;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        // Plus button - spawn clone
        if (ImPoser.CenteredIconButton(
            "spawn_clone",
            FontAwesomeIcon.Plus,
            new Vector2(buttonSize, buttonSize),
            "Spawn clone of player"))
        {
            _spawnService.SpawnPlayerClone();
        }

        ImGui.SameLine();

        // Trash button right-aligned
        float trashButtonWidth = buttonSize * 4;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - trashButtonWidth);

        // Only allow deleting spawned actors
        bool canDelete = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);

        using (ImRaii.Disabled(!canDelete))
        {
            if (ImPoser.FontIconButton(
                "delete_selected",
                FontAwesomeIcon.Trash,
                new Vector2(trashButtonWidth, buttonSize),
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
}
