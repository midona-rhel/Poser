using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
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
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _bindings.Resolve(actorId) is { Success: true, Value: { } actor }
                    ? _spawnService.IsVisible(actor) : null;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                return _bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light }
                    ? light.IsOn : null;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                return _bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop }
                    ? prop.Visible : null;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } borrowedId }:
                return _bindings.Resolve(borrowedId) is { Success: true, Value: { IsValid: true } borrowed }
                    ? borrowed.Visible : null;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                return _bindings.Resolve(overlayId) is { Success: true, Value: { } node }
                    ? node.Visible : null;
            default:
                return null;
        }
    }

    private void SetEntityVisible(SelectionId id, bool visible)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                if (_bindings.Resolve(actorId) is { Success: true, Value: { } actor })
                    _sessions.Actors.SetVisibility(actor, visible);
                break;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                if (_bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light })
                    _sessions.Lights.SetIsOn(light, visible);
                break;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                if (_bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop })
                    _sessions.Props.SetVisible(prop, visible);
                break;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } borrowedId }:
                if (_bindings.Resolve(borrowedId) is { Success: true, Value: { IsValid: true } borrowed })
                    _sessions.WorldObjects.SetVisible(borrowed, visible);
                break;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                if (_bindings.Resolve(overlayId) is { Success: true, Value: { } node })
                    _sessions.Overlays.SetVisible(node, visible);
                break;
        }
    }

    private void SetSelectionVisible(bool visible)
    {
        foreach (var id in _selection.Selected)
        {
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor })
                        _sessions.Actors.SetVisibility(actor, visible);
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsValid: true } light })
                        _sessions.Lights.SetIsOn(light, visible);
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { IsValid: true } prop })
                        _sessions.Props.SetVisible(prop, visible);
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { IsValid: true } borrowed })
                        _sessions.WorldObjects.SetVisible(borrowed, visible);
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { } node })
                        _sessions.Overlays.SetVisible(node, visible);
                    break;
            }
        }
    }

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
    private void DestroySelection()
    {
        DestroyEntities(_selection.Selected.ToArray());
        _selection.Clear();
    }

    private void DestroyEntities(IReadOnlyList<SelectionId> ids)
    {
        foreach (var id in ids)
        {
            // A locked group keeps its members standing.
            if (_groups.IsLockedMember(id))
                continue;
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor }
                        && (_spawnService.IsSpawnedActor(actor)
                            || _spawnService.RemovalRefusal(actor) is null)
                        && _lifecycle.DespawnActor(actor))
                        _selection.RemoveActorLineage(actorId.LogicalId);
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsValid: true } light })
                    {
                        if (light.Ownership == LightOwnership.Spawned)
                            _lifecycle.DestroyLight(light);
                        else
                            _lightingService.ReleaseLight(light);
                    }
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { IsValid: true } prop })
                        _lifecycle.DestroyProp(prop);
                    break;
                case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                    if (_bindings.Resolve(cameraId) is
                            { Success: true, Value: { IsValid: true } camera }
                        && !camera.IsDefault)
                        _lifecycle.DestroyCamera(camera);
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { } node })
                        _lifecycle.DestroyOverlay(node);
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { IsValid: true } borrowed })
                        _lifecycle.ReleaseWorldObject(borrowed);
                    break;
            }
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
