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
    private readonly IEditorState _editorState;
    private readonly ICameraService _cameraService;
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
        _editorState = editorState;
        _cameraService = cameraService;

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
        float buttonSize = UIConstants.ScaledButtonSize;

        // Match ActorList constants for alignment
        float iconColWidth = 32f * ImGuiHelpers.GlobalScale;
        float cellPadding = 4f * ImGuiHelpers.GlobalScale;

        // Center button like icons in table
        float cellContentWidth = iconColWidth - (cellPadding * 2);
        float offsetX = cellPadding + (cellContentWidth - buttonSize) / 2;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        // Plus button with popup menu
        if (ImPoser.CenteredIconButton(
            "add_entity",
            FontAwesomeIcon.Plus,
            new Vector2(buttonSize, buttonSize),
            "Add entity"))
        {
            ImGui.OpenPopup("##add_entity_popup");
        }

        // Popup menu for add options
        if (ImGui.BeginPopup("##add_entity_popup"))
        {
            if (ImGui.MenuItem("Spawn Actor Clone"))
            {
                _spawnService.SpawnPlayerClone();
            }

            if (ImGui.MenuItem("Create Pivot Point"))
            {
                CreatePivotPoint();
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();

        // Trash button right-aligned
        float trashButtonWidth = buttonSize * 4;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - trashButtonWidth);

        // Only allow deleting spawned actors or pivot points
        bool canDeleteActor = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);
        bool canDeletePivot = _editorState.OrbitTarget is PivotPoint;
        bool canDelete = canDeleteActor || canDeletePivot;

        string deleteTooltip = canDeleteActor
            ? "Delete selected entity"
            : canDeletePivot
                ? "Delete selected pivot point"
                : "Can only delete spawned entities or pivot points";

        using (ImRaii.Disabled(!canDelete))
        {
            if (ImPoser.FontIconButton(
                "delete_selected",
                FontAwesomeIcon.Trash,
                new Vector2(trashButtonWidth, buttonSize),
                deleteTooltip,
                canDelete))
            {
                if (canDeleteActor && primarySelected != null)
                {
                    _spawnService.DestroyActor(primarySelected);
                }
                else if (canDeletePivot && _editorState.OrbitTarget is PivotPoint pivot)
                {
                    _editorState.DeletePivotPoint(pivot);
                }
            }
        }
    }

    private void CreatePivotPoint()
    {
        // Spawn pivot point in front of camera
        var cameraPos = _cameraService.GetCameraPosition();

        // Get camera forward direction from view matrix
        var viewMatrix = _cameraService.GetViewMatrix();
        // Forward vector is -Z axis of view matrix (camera looks down -Z)
        var forward = new Vector3(-viewMatrix.M13, -viewMatrix.M23, -viewMatrix.M33);
        forward = Vector3.Normalize(forward);

        // Position pivot 3 units in front of camera
        var pivotPos = cameraPos + forward * 3f;

        var pivot = _editorState.CreatePivotPoint(pivotPos);
        _editorState.OrbitTarget = pivot;
        _editorState.TransformPivot = TransformPivot.Target;
    }
}
