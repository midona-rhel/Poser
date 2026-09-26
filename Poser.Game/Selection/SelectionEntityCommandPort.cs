using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.World;
using Dalamud.Plugin.Services;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Game.World;
using System.Linq;
using System.Threading.Tasks;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Selection;

/// <summary>Game adapter from pointer-free entity commands to current native
/// bindings and the existing value/lifecycle history owners.</summary>
public sealed class SelectionEntityCommandPort : ISelectionEntityCommandPort
{
    private readonly SceneSession _scene;
    private readonly IEntityBindings _bindings;
    private readonly EntitySessions _sessions;
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnService _actors;
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly PropSpawnService _props;
    private readonly OverlayNodeService _overlays;
    private readonly WorldObjectService _worldObjects;
    private readonly IWorldReleasePort _worldRelease;
    private readonly IFramework _framework;
    private readonly TransformHistory _history;
    private readonly SceneGroups _groups;
    private readonly SelectionSession _selection;

    public SelectionEntityCommandPort(
        SceneSession scene,
        IEntityBindings bindings,
        EntitySessions sessions,
        IActorManager actorManager,
        IActorSpawnService actors,
        ISceneLifecycleHistory lifecycle,
        ILightingService lighting,
        IVirtualCameraService cameras,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        IWorldReleasePort worldRelease,
        SceneGroups groups,
        IFramework framework,
        TransformHistory history)
    {
        _scene = scene;
        _bindings = bindings;
        _sessions = sessions;
        _actorManager = actorManager;
        _actors = actors;
        _lifecycle = lifecycle;
        _lighting = lighting;
        _cameras = cameras;
        _props = props;
        _overlays = overlays;
        _worldObjects = worldObjects;
        _worldRelease = worldRelease;
        _framework = framework;
        _history = history;
        _groups = groups;
        _selection = scene.Selection;
    }

    public bool? ReadVisibility(SelectionId id)
    {
        var current = _scene.ReadCurrent(id);
        if (current is not { CanChangeVisibility: true } || current.Id != id)
            return null;

        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)
                    || !ActorOwnershipMatches(actorId, actor))
                    return null;
                return _actors.IsVisible(actor);
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)
                    || (current.Removal == SelectionRemoval.Release)
                        != (light.Ownership != LightOwnership.Spawned))
                    return null;
                return light.IsOn;
            case { Prop: { } propId }:
                return CurrentProp(propId, out var prop) ? prop.Visible : null;
            case { Overlay: { } overlayId }:
                return CurrentOverlay(overlayId, out var overlay)
                    ? overlay.Visible : null;
            case { WorldObject: { } worldId }:
                return CurrentWorldObject(worldId, out var world)
                    ? world.Visible : null;
            default:
                return null;
        }
    }

    public bool SetVisibility(SelectionId id, bool visible)
    {
        if (_scene.ReadCurrent(id) is not { CanChangeVisibility: true } current
            || current.Id != id)
            return false;
        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)
                    || !ActorOwnershipMatches(actorId, actor)) return false;
                return _sessions.Actors.SetVisibility(actorId, visible).Success;
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)
                    || (current.Removal == SelectionRemoval.Release)
                        != (light.Ownership != LightOwnership.Spawned)) return false;
                _sessions.Lights.SetIsOn(light, visible);
                return true;
            case { Prop: { } propId }:
                if (!CurrentProp(propId, out var prop)) return false;
                _sessions.Props.SetVisible(prop, visible);
                return true;
            case { Overlay: { } overlayId }:
                if (!CurrentOverlay(overlayId, out var overlay)) return false;
                _sessions.Overlays.SetVisible(overlay, visible);
                return true;
            case { WorldObject: { } worldId }:
                if (!CurrentWorldObject(worldId, out var world)) return false;
                _sessions.WorldObjects.SetVisible(world, visible);
                return true;
            default:
                return false;
        }
    }

    public Task<SelectionRemovalResult> Remove(IReadOnlyList<SelectionRemovalRequest> requests)
    {
        var captured = requests.ToArray();
        return _framework.RunOnFrameworkThread(() =>
        {
            var outcomes = new List<SelectionRemovalItem>(captured.Length);
            _history.RecordLifecycleBatch("Remove entities", () =>
            {
                foreach (var request in captured)
                {
                    try
                    {
                        var result = RemoveOne(request);
                        outcomes.Add(result);
                        if (result.Status == SelectionRemovalStatus.Removed)
                            _selection.Remove(result.Id);
                    }
                    catch (Exception ex)
                    {
                        outcomes.Add(new(request.Id, SelectionRemovalStatus.Failed, ex.Message));
                    }
                }
            });
            return new SelectionRemovalResult(outcomes);
        });
    }

    private SelectionRemovalItem RemoveOne(SelectionRemovalRequest request)
    {
        var (id, removal) = request;
        SelectionRemovalItem Removed() => new(id, SelectionRemovalStatus.Removed);
        SelectionRemovalItem Refused(string detail) => new(id, SelectionRemovalStatus.Refused, detail);
        SelectionRemovalItem Absent() => new(id, SelectionRemovalStatus.AlreadyAbsent);
        SelectionRemovalItem Released()
        {
            var result = _worldRelease.ReleaseCurrent(id);
            return result.Status switch
            {
                WorldCommandStatus.Applied => Removed(),
                WorldCommandStatus.AlreadyReleased => Absent(),
                _ => Refused(result.Detail ?? "That world asset could not be released."),
            };
        }
        var current = _scene.ReadCurrent(id);
        if (current is null || current.Id != id) return Absent();
        if (current.Removal != removal || removal == SelectionRemoval.None)
            return Refused("That entity cannot be removed by this action.");
        if (_groups.IsLockedMember(id))
            return Refused("Unlock the group before removing its members.");

        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)) return Absent();
                if (!ActorOwnershipMatches(actorId, actor))
                    return Refused("The actor's ownership changed; select it again.");
                bool adopted = _scene.Snapshot.FindActor(actorId)?.IsAdopted == true;
                if (removal == SelectionRemoval.Release)
                {
                    if (!adopted) return Refused("That actor is not borrowed from the world.");
                    return Released();
                }
                if (adopted
                    || (!_actors.IsSpawnedActor(actor)
                        && _actors.RemovalRefusal(actor) is not null))
                    return Refused(_actors.RemovalRefusal(actor) ?? "That actor cannot be destroyed.");
                if (!_lifecycle.DespawnActor(actor)) return Refused("The actor could not be removed.");
                _selection.RemoveActorLineage(actorId.LogicalId);
                return Removed();
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)) return Absent();
                if (light.Ownership == LightOwnership.Spawned)
                {
                    if (removal != SelectionRemoval.Destroy) return Refused("That light is not borrowed.");
                    _lifecycle.DestroyLight(light);
                }
                else
                {
                    if (removal != SelectionRemoval.Release) return Refused("Borrowed lights must be released.");
                    return Released();
                }
                return !_lighting.Lights.Contains(light) ? Removed() : Refused("The light could not be removed.");
            case { Prop: { } propId }:
                if (removal != SelectionRemoval.Destroy
                    || !CurrentProp(propId, out var prop)) return Absent();
                _lifecycle.DestroyProp(prop);
                return !_props.Props.Contains(prop) ? Removed() : Refused("The prop could not be removed.");
            case { Camera: { } cameraId }:
                if (!CurrentCamera(cameraId, out var camera)) return Absent();
                if (removal != SelectionRemoval.Destroy || camera.IsDefault)
                    return Refused("The main camera cannot be removed.");
                _lifecycle.DestroyCamera(camera);
                return !_cameras.Cameras.Contains(camera) ? Removed() : Refused("The camera could not be removed.");
            case { Overlay: { } overlayId }:
                if (removal != SelectionRemoval.Destroy
                    || !CurrentOverlay(overlayId, out var overlay)) return Absent();
                _lifecycle.DestroyOverlay(overlay);
                return !_overlays.Nodes.Contains(overlay) ? Removed() : Refused("The overlay could not be removed.");
            case { WorldObject: { } worldId }:
                if (removal != SelectionRemoval.Release
                    || !CurrentWorldObject(worldId, out _)) return Absent();
                return Released();
            default:
                return Refused("This entity does not support removal.");
        }
    }

    private bool CurrentActor(ActorId id, out IActor actor)
    {
        var result = _bindings.Resolve(id);
        actor = result.Value!;
        return result.Success && actor != null
            && _bindings.GetActorId(actor) == id
            && _actorManager.Actors.Contains(actor);
    }

    private bool ActorOwnershipMatches(ActorId id, IActor actor) =>
        _scene.Snapshot.FindActor(id) is { } descriptor
        && descriptor.IsAdopted == _actorManager.IsAdopted(actor);

    private bool CurrentLight(LightId id, out ILight light)
    {
        var result = _bindings.Resolve(id);
        light = result.Value!;
        return result.Success && light is { IsValid: true }
            && _bindings.GetLightId(light) == id
            && _lighting.Lights.Contains(light);
    }

    private bool CurrentCamera(CameraId id, out IVirtualCamera camera)
    {
        var result = _bindings.Resolve(id);
        camera = result.Value!;
        return result.Success && camera is { IsValid: true }
            && _bindings.GetCameraId(camera) == id
            && _cameras.Cameras.Contains(camera);
    }

    private bool CurrentProp(PropId id, out IPropHandle prop)
    {
        var result = _bindings.Resolve(id);
        prop = result.Value!;
        return result.Success && prop is { IsValid: true }
            && _bindings.GetPropId(prop) == id
            && _props.Props.Contains(prop);
    }

    private bool CurrentOverlay(OverlayId id, out IOverlayNode overlay)
    {
        var result = _bindings.Resolve(id);
        overlay = result.Value!;
        return result.Success && overlay is { IsValid: true }
            && _bindings.GetOverlayId(overlay) == id
            && _overlays.Nodes.Contains(overlay);
    }

    private bool CurrentWorldObject(WorldObjectId id, out IWorldObject world)
    {
        var result = _bindings.Resolve(id);
        world = result.Value!;
        return result.Success && world is { IsValid: true }
            && _bindings.GetWorldObjectId(world) == id
            && _worldObjects.Adopted.Contains(world);
    }
}
