using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Per-entity verbs: playing, night, visible, destroy, and the selection forms of each.</summary>
public partial class MainWindow
{
    private bool ResolveExactActor(ActorId actorId) => _actorControl.Read(actorId) != null;

    private ActorDescriptor? ResolveActorDescriptor(ActorId actorId) =>
        ResolveExactActor(actorId)
            ? _scene.Snapshot.FindActor(actorId)
            : null;

    private bool? PlayingOf(SelectionId id) => _playback.ReadPlaying(id);
    private void SetPlaying(SelectionId id, bool playing) => _playback.SetPlaying(id, playing);
    private bool? NightOf(SelectionId id) => _playback.ReadNight(id);
    private void SetNight(SelectionId id, bool night) => _playback.SetNight(id, night);

    private bool? IsEntityVisible(SelectionId id)
        => _entityCommands.ReadVisibility(id);

    private int SetEntityVisible(SelectionId id, bool visible) =>
        _entityCommands.SetVisibility([id], visible);

    private void SetSelectionVisible(bool visible) =>
        _entityCommands.SetVisibility(_selection.Selected.ToArray(), visible);

    /// <summary>One animation state for every selected actor.</summary>
    private void SetSelectionPaused(bool paused)
    {
        foreach (var id in _selection.Selected)
            SetPlaying(id, !paused);
    }

    /// <summary>Destroys the whole selection, each kind through its own
    /// lifetime seam: actors despawn where the service admits it, spawned
    /// lights destroy while borrowed ones release, the default camera
    /// stays, borrowed objects go back to the map.</summary>
    private async void DestroySelection()
    {
        var selected = _selection.Selected.ToArray();
        await RemoveEntitiesSafely(selected);
    }

    private async Task<int?> RemoveEntitiesSafely(
        IReadOnlyList<SelectionId> ids,
        Action? onRemoved = null)
    {
        var removed = await _entityActions.Remove(ids);
        if (removed > 0) onRemoved?.Invoke();
        return removed;
    }

    /// <summary>The selection's actor, if any — the recenter seat's
    /// target.</summary>
    private SelectionId? SelectedActorRef()
    {
        foreach (var id in _selection.Selected)
            if (id is { Kind: SceneEntityKind.Actor })
                return id;
        return null;
    }

    /// <summary>Whether the LOOK-AT verb has anything to do — the context
    /// menu's "Look at tracked actor", distinct from the row seat's
    /// Brio-style retarget.</summary>
    private bool CanRecenterOnTracked(CameraId id) =>
        _cameraTargets.Read(id)?.CanCenterTrackedActor == true;

    private void RecenterCameraOnTrackedActor(CameraId cameraId)
    {
        var result = _cameraTargets.CenterTrackedActor(cameraId);
        if (!result.Success) _notices.Refused(result.Detail ?? "The camera could not move.");
    }

    /// <summary>Whether the current selection is empty or every selected
    /// entity has <paramref name="parent"/> as its group (null = root).</summary>
    private bool SelectionParentIs(Guid? parent)
    {
        foreach (var selected in _selection.Selected)
            if (_groups.GroupOf(selected)?.Id != parent)
                return false;
        return true;
    }
}
