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
    /// <summary>Whether this entity is playing its animation: an actor's
    /// timeline, an effect's playback, borrowed scenery's animation. Null
    /// for kinds that do not animate, spawned scenery included.</summary>
    private bool? PlayingOf(SelectionId id)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _animation.AnyPlaying(actorId);
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_bindings.Resolve(objectId) is not
                        { Success: true, Value: { IsValid: true } handle })
                    return null;
                if (handle.IsVfx)
                    return !handle.VfxPaused;
                return handle.Spawned ? null : !handle.AnimationPaused;
            default:
                return null;
        }
    }

    private void SetPlaying(SelectionId id, bool playing)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                if (playing)
                    _animation.Resume(actorId);
                else
                    _animation.Pause(actorId);
                break;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_bindings.Resolve(objectId) is not
                        { Success: true, Value: { IsValid: true } handle })
                    return;
                if (handle.IsVfx)
                    _sessions.WorldObjects.SetVfxPaused(handle, !playing);
                else if (!handle.Spawned)
                    _sessions.WorldObjects.SetAnimationPaused(handle, !playing);
                break;
        }
    }

    /// <summary>Scenery's night state; null for everything else.</summary>
    private bool? NightOf(SelectionId id) =>
        id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }

        && _bindings.Resolve(objectId) is
            { Success: true, Value: { IsValid: true, IsVfx: false } handle }

            ? handle.NightState
            : null;

    private void SetNight(SelectionId id, bool night)
    {
        if (id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }
            && _bindings.Resolve(objectId) is
                { Success: true, Value: { IsValid: true, IsVfx: false } handle })
            _sessions.WorldObjects.SetNightState(handle, night);
    }

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

    private Task<int> DestroyEntities(IReadOnlyList<SelectionId> ids) =>
        _entityCommands.Remove(ids);

    private async Task<int?> RemoveEntitiesSafely(
        IReadOnlyList<SelectionId> ids,
        Action? onRemoved = null)
    {
        try
        {
            int removed = await DestroyEntities(ids);
            if (removed > 0)
                onRemoved?.Invoke();
            return removed;
        }
        catch (Exception exception)
        {
            _notices.Failed("Remove", exception.Message);
            return null;
        }
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
    private bool CanRecenterOnTracked(IVirtualCamera camera)
    {
        if (!_cameraService.IsAvailable || camera.IsLocked || !camera.IsLive
            || camera.Kind == CameraKind.Free || camera.FixedPosition != null)
            return false;
        return ResolveCameraTrackedActor(camera) is { } tracked
            && TryResolveExactActor(tracked.Id, out var exact)
            && _spawnService.IsVisible(exact);
    }

    private void RecenterCameraOnTrackedActor(CameraId cameraId)
    {
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success ||
            resolved.Value is not { IsValid: true } camera ||
            _bindings.GetCameraId(camera) != cameraId ||
            !_cameraService.IsAvailable || camera.IsLocked || !camera.IsLive ||
            camera.Kind == CameraKind.Free || camera.FixedPosition != null)
        {
            return;
        }
        var actor = ResolveCameraTrackedActor(camera);
        if (actor == null || !TryResolveExactActor(actor.Id, out var exact) ||
            !_spawnService.IsVisible(exact))
        {
            return;
        }
        _cameraPane.CenterOnActor(actor.Id);
    }

    /// <summary>No gaze at all: the copy's eyes, head and body stay on
    /// the pose. Freezing the parts only pinned where they looked, and the
    /// game's loop kept turning the head after the camera.</summary>
    private void FreezeGaze(IActor copy)
    {
        var mode = _gazeService.SetGazeMode(copy, GazeTargetMode.Detached);
        if (!mode.Success)
            _log.Warning($"Duplicate: the gaze could not be detached: {mode.Detail}");
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
