using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    public event Action<ActorBase, bool>? OnAnimationFreezeToggle;
    public event Action<ActorBase, bool>? OnPhysicsFreezeToggle;
    public event Action? OnSpawnClone;
    public event Action? OnDeleteSelected;

    public ScenePanel(
        IActorManager actorManager,
        IAnimationService animationService,
        EventBus eventBus)
    {
        _actorManager = actorManager;
        _entityList = new EntityList(actorManager, animationService, eventBus);

        // Wire up events
        _entityList.OnAnimationFreezeToggle += (actor, freeze) => OnAnimationFreezeToggle?.Invoke(actor, freeze);
        _entityList.OnPhysicsFreezeToggle += (actor, freeze) => OnPhysicsFreezeToggle?.Invoke(actor, freeze);
        _entityList.OnSpawnClone += () => OnSpawnClone?.Invoke();
    }

    public void Draw()
    {
        ImGui.Text("Scene");
        ImGui.Spacing();

        // Draw entity list (contains ActorList and future sublists)
        _entityList.Draw();

        ImGui.Spacing();

        // Delete button at bottom
        DrawDeleteButton();
    }

    private void DrawDeleteButton()
    {
        bool hasSelection = _actorManager.SelectedActors.Count > 0;

        using (ImRaii.Disabled(!hasSelection))
        {
            if (ImPoser.FontIconButton(
                "delete_selected",
                FontAwesomeIcon.Trash,
                new System.Numerics.Vector2(ImPoser.GetRemainingWidth(), UIConstants.ScaledButtonSize),
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
