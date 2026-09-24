using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using System.Linq;
using System.Threading.Tasks;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.UI;

/// <summary>Host adapter from pointer-free entity commands to current native
/// bindings and the existing value/lifecycle history owners.</summary>
public sealed class SelectionEntityCommandPort : ISelectionEntityCommandPort
{
    private readonly SceneSession _scene;
    private readonly IEntityBindings _bindings;
    private readonly EntitySessions _sessions;
    private readonly IActorSpawnService _actors;
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly PropSpawnService _props;
    private readonly OverlayNodeService _overlays;
    private readonly WorldObjectService _worldObjects;
    private readonly WorldActions _worldActions;
    private readonly SceneGroups _groups;
    private readonly SelectionSession _selection;

    public SelectionEntityCommandPort(
        SceneSession scene,
        IEntityBindings bindings,
        EntitySessions sessions,
        IActorSpawnService actors,
        ISceneLifecycleHistory lifecycle,
        ILightingService lighting,
        IVirtualCameraService cameras,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        WorldActions worldActions,
        SceneGroups groups)
    {
        _scene = scene;
        _bindings = bindings;
        _sessions = sessions;
        _actors = actors;
        _lifecycle = lifecycle;
        _lighting = lighting;
        _cameras = cameras;
        _props = props;
        _overlays = overlays;
        _worldObjects = worldObjects;
        _worldActions = worldActions;
        _groups = groups;
        _selection = scene.Selection;
    }

    public bool SetVisibility(SelectionId id, bool visible)
    {
        if (_scene.ReadCurrent(id) is not { CanChangeVisibility: true })
            return false;
        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)) return false;
                return _sessions.Actors.SetVisibility(actor, visible).Success;
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)) return false;
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

    public async Task<bool> Remove(SelectionId id, SelectionRemoval removal)
    {
        var current = _scene.ReadCurrent(id);
        if (current is null || current.Removal != removal
            || removal == SelectionRemoval.None || _groups.IsLockedMember(id))
            return false;

        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)) return false;
                bool adopted = _scene.Snapshot.FindActor(actorId)?.IsAdopted == true;
                if (removal == SelectionRemoval.Release)
                {
                    if (!adopted) return false;
                    return await _worldActions.Release(id);
                }
                if (adopted
                    || (!_actors.IsSpawnedActor(actor)
                        && _actors.RemovalRefusal(actor) is not null))
                    return false;
                if (!_lifecycle.DespawnActor(actor)) return false;
                _selection.RemoveActorLineage(actorId.LogicalId);
                return true;
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)) return false;
                if (light.Ownership == LightOwnership.Spawned)
                {
                    if (removal != SelectionRemoval.Destroy) return false;
                    _lifecycle.DestroyLight(light);
                }
                else
                {
                    if (removal != SelectionRemoval.Release) return false;
                    return await _worldActions.Release(id);
                }
                return true;
            case { Prop: { } propId }:
                if (removal != SelectionRemoval.Destroy
                    || !CurrentProp(propId, out var prop)) return false;
                _lifecycle.DestroyProp(prop);
                return true;
            case { Camera: { } cameraId }:
                if (removal != SelectionRemoval.Destroy
                    || !CurrentCamera(cameraId, out var camera)
                    || camera.IsDefault) return false;
                _lifecycle.DestroyCamera(camera);
                return true;
            case { Overlay: { } overlayId }:
                if (removal != SelectionRemoval.Destroy
                    || !CurrentOverlay(overlayId, out var overlay)) return false;
                _lifecycle.DestroyOverlay(overlay);
                return true;
            case { WorldObject: { } worldId }:
                if (removal != SelectionRemoval.Release
                    || !CurrentWorldObject(worldId, out _)) return false;
                return await _worldActions.Release(id);
            default:
                return false;
        }
    }

    private bool CurrentActor(ActorId id, out IActor actor)
    {
        var result = _bindings.Resolve(id);
        actor = result.Value!;
        return result.Success && actor != null
            && _bindings.GetActorId(actor) == id;
    }

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
