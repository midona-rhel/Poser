using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
    private readonly EntityList _entityList;

    public event Action? OnSpawnClone;
    public event Action? OnDeleteSelected;

    public ScenePanel(
        IActorManager actorManager,
        IAnimationService animationService,
        EventBus eventBus)
    {
        _actorManager = actorManager;
        _entityList = new EntityList(actorManager, animationService, eventBus);
    }

    public void Draw()
    {
        ImGui.Text("Scene");
        ImGui.Spacing();

        // Draw entity list (contains ActorList and future sublists)
        _entityList.Draw();

        ImGui.Spacing();

        // Plus and Delete buttons at bottom
        DrawBottomButtons();
    }

    private void DrawBottomButtons()
    {
        bool hasSelection = _actorManager.SelectedActors.Count > 0;
        float buttonSize = UIConstants.ScaledButtonSize;
        float cellPadding = 4f * ImGuiHelpers.GlobalScale; // Match ActorList.CellPaddingX

        // Offset to align with table icon column content
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cellPadding);

        // Plus button on the left
        if (ImPoser.CenteredIconButton(
            "spawn_clone",
            FontAwesomeIcon.Plus,
            new System.Numerics.Vector2(buttonSize, buttonSize),
            "Spawn clone of player"))
        {
            OnSpawnClone?.Invoke();
        }

        ImGui.SameLine();

        // Trash button right-aligned
        float trashButtonWidth = buttonSize * 4;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - trashButtonWidth);

        using (ImRaii.Disabled(!hasSelection))
        {
            if (ImPoser.FontIconButton(
                "delete_selected",
                FontAwesomeIcon.Trash,
                new System.Numerics.Vector2(trashButtonWidth, buttonSize),
                "Delete selected entities",
                hasSelection))
            {
                OnDeleteSelected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _entityList.Dispose();
    }
}
