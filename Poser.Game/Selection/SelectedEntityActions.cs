using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.World;
using Dalamud.Plugin.Services;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game;
using Poser.Game.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using System.Linq;
using System.Threading.Tasks;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Selection;

/// <summary>
/// Shows, hides and removes selected things. Each one is looked up in the scene
/// and in the game right before it is changed, and the change goes through the
/// same value and add-and-remove undo steps that the rest of Poser uses.
/// </summary>
public sealed class SelectedEntityActions : ISelectedEntityActions
{
    private const string Unavailable = "That entity is no longer available.";

    private readonly SceneSession _scene;
    private readonly IEntityBindings _bindings;
    private readonly Poser.Application.Presentation.IActorValueControl _actorValues;
    private readonly Poser.Application.Presentation.ISceneObjectControl _objectValues;
    private readonly Poser.Application.Presentation.ILightControl _lights;
    private readonly Poser.Application.Presentation.IOverlayControl _overlayValues;
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnService _actors;
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly PropSpawnService _props;
    private readonly OverlayNodeService _overlays;
    private readonly WorldObjectService _worldObjects;
    private readonly IWorldBorrowRelease _worldRelease;
    private readonly IFramework _framework;
    private readonly EditHistory _history;
    private readonly SceneGroups _groups;
    private readonly SelectionSession _selection;
    private readonly System.Lazy<GroupSteps>? _groupSteps;

    public SelectedEntityActions(
        SceneSession scene,
        IEntityBindings bindings,
        Poser.Application.Presentation.IActorValueControl actorValues,
        Poser.Application.Presentation.ISceneObjectControl objectValues,
        Poser.Application.Presentation.ILightControl lights,
        Poser.Application.Presentation.IOverlayControl overlayValues,
        IActorManager actorManager,
        IActorSpawnService actors,
        ISceneLifecycleHistory lifecycle,
        ILightingService lighting,
        IVirtualCameraService cameras,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        IWorldBorrowRelease worldRelease,
        SceneGroups groups,
        IFramework framework,
        EditHistory history,
        System.Lazy<GroupSteps>? groupSteps = null)
    {
        _groupSteps = groupSteps;
        _scene = scene;
        _bindings = bindings;
        _actorValues = actorValues;
        _objectValues = objectValues;
        _lights = lights;
        _overlayValues = overlayValues;
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
        if (_scene.ReadCurrent(id) is not { CanChangeVisibility: true } current)
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

    public SelectionVisibilityResult SetVisibility(IEnumerable<SelectionId> ids, bool visible)
    {
        var items = new List<SelectionVisibilityItem>();
        var eligible = new List<CurrentSelectionEntity>();
        foreach (var id in ids.Distinct())
        {
            if (_scene.ReadCurrent(id) is not { } current)
            {
                // A thing that left the scene is reported; a bone or a gaze
                // point was never something to show or hide.
                if (EntitySelection.IsEntity(id.Kind)) items.Add(new(id, Outcome.Fail(Unavailable)));
                continue;
            }
            if (current.CanChangeVisibility) eligible.Add(current);
        }
        void Apply()
        {
            foreach (var current in eligible) items.Add(new(current.Id, Write(current, visible)));
        }
        // One thing keeps its own value step, so undo for the selected thing
        // still reaches it; several share one step.
        if (eligible.Count > 1)
            _history.RecordLifecycleBatch(visible ? "Show entities" : "Hide entities", Apply);
        else
            Apply();
        return new(items);
    }

    private Outcome Write(CurrentSelectionEntity current, bool visible)
    {
        try { return WriteVisibility(current, visible); }
        catch (Exception ex) { return Outcome.Fail(ex.Message); }
    }

    private Outcome WriteVisibility(CurrentSelectionEntity current, bool visible)
    {
        Outcome Gone() => Outcome.Fail(Unavailable);
        switch (current.Id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)) return Gone();
                if (!ActorOwnershipMatches(actorId, actor))
                    return new(false, "The actor's ownership changed; select it again.");
                return _actorValues.SetVisibility(actorId, visible);
            case { Light: { } lightId }:
                if (!CurrentLight(lightId, out var light)) return Gone();
                if ((current.Removal == SelectionRemoval.Release)
                        != (light.Ownership != LightOwnership.Spawned))
                    return new(false, "The light's ownership changed; select it again.");
                return _lights.Set(lightId, Poser.Application.Presentation.LightProperties.IsOn, visible);
            case { Prop: { } propId }:
                return CurrentProp(propId, out _)
                    ? _objectValues.Set(propId, Poser.Application.Presentation.PropProperties.Visible, visible) : Gone();
            case { Overlay: { } overlayId }:
                return CurrentOverlay(overlayId, out _)
                    ? _overlayValues.Set(overlayId, Poser.Application.Presentation.OverlayProperties.Visible, visible) : Gone();
            case { WorldObject: { } worldId }:
                return CurrentWorldObject(worldId, out _)
                    ? _objectValues.Set(worldId, Poser.Application.Presentation.WorldObjectProperties.Visible, visible) : Gone();
            default:
                return new(false, "That entity cannot be shown or hidden.");
        }
    }

    public Task<SelectionRemovalResult> Remove(IEnumerable<SelectionId> ids)
    {
        var requested = ids.Distinct().ToArray();
        return _framework.RunOnFrameworkThread(() =>
        {
            var captured = new List<CurrentSelectionEntity>();
            foreach (var id in requested)
                if (_scene.ReadCurrent(id) is { Removal: not SelectionRemoval.None } current)
                    captured.Add(current);
            var outcomes = new List<SelectionRemovalItem>(captured.Count);
            if (captured.Count == 0)
                return new SelectionRemovalResult(outcomes);
            _history.RecordLifecycleBatch("Remove entities", () =>
            {
                // Members leave their groups through the scene prune; the
                // undo puts the structure back with them (#494).
                var structure = _groupSteps?.Value.Capture();
                bool groupedRemoved = false;
                foreach (var request in captured)
                {
                    try
                    {
                        bool grouped = _groups.GroupOf(request.Id) != null;
                        var result = RemoveOne(request);
                        outcomes.Add(result);
                        if (result.Status == SelectionRemovalStatus.Removed)
                        {
                            _selection.Remove(result.Id);
                            groupedRemoved |= grouped;
                        }
                    }
                    catch (Exception ex)
                    {
                        outcomes.Add(new(request.Id, SelectionRemovalStatus.Failed, ex.Message));
                    }
                }
                if (groupedRemoved && structure is not null)
                    _history.AppendBatchPrologue(_groupSteps!.Value.RemovalStructure(structure));
            });
            return new SelectionRemovalResult(outcomes);
        });
    }

    private SelectionRemovalItem RemoveOne(CurrentSelectionEntity request)
    {
        var (id, _, removal) = request;
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
        switch (id)
        {
            case { Actor: { } actorId }:
                if (!CurrentActor(actorId, out var actor)) return Absent();
                if (!ActorOwnershipMatches(actorId, actor))
                    return Refused("The actor's ownership changed; select it again.");
                // A borrowed actor is released; one Poser made is destroyed.
                if (removal == SelectionRemoval.Release)
                    return Released();
                if (!_actors.IsSpawnedActor(actor) && _actors.RemovalRefusal(actor) is { } refusal)
                    return Refused(refusal);
                if (!_lifecycle.DespawnActor(actor, out var note)) return Refused("The actor could not be removed.");
                _selection.RemoveActorLineage(actorId.LogicalId);
                return new(id, SelectionRemovalStatus.Removed, note);
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
                if (!CurrentProp(propId, out var prop)) return Absent();
                _lifecycle.DestroyProp(prop);
                return !_props.Props.Contains(prop) ? Removed() : Refused("The prop could not be removed.");
            case { Camera: { } cameraId }:
                if (!CurrentCamera(cameraId, out var camera)) return Absent();
                if (camera.IsDefault)
                    return Refused("The main camera cannot be removed.");
                _lifecycle.DestroyCamera(camera);
                return !_cameras.Cameras.Contains(camera) ? Removed() : Refused("The camera could not be removed.");
            case { Overlay: { } overlayId }:
                if (!CurrentOverlay(overlayId, out var overlay)) return Absent();
                _lifecycle.DestroyOverlay(overlay);
                return !_overlays.Nodes.Contains(overlay) ? Removed() : Refused("The overlay could not be removed.");
            case { WorldObject: { } worldId }:
                if (!CurrentWorldObject(worldId, out _)) return Absent();
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
            && _lighting.Lights.Contains(light);
    }

    private bool CurrentCamera(CameraId id, out IVirtualCamera camera)
    {
        var result = _bindings.Resolve(id);
        camera = result.Value!;
        return result.Success && camera is { IsValid: true }
            && _cameras.Cameras.Contains(camera);
    }

    private bool CurrentProp(PropId id, out IPropHandle prop)
    {
        var result = _bindings.Resolve(id);
        prop = result.Value!;
        return result.Success && prop is { IsValid: true }
            && _props.Props.Contains(prop);
    }

    private bool CurrentOverlay(OverlayId id, out IOverlayNode overlay)
    {
        var result = _bindings.Resolve(id);
        overlay = result.Value!;
        return result.Success && overlay is { IsValid: true }
            && _overlays.Nodes.Contains(overlay);
    }

    private bool CurrentWorldObject(WorldObjectId id, out IWorldObject world)
    {
        var result = _bindings.Resolve(id);
        world = result.Value!;
        return result.Success && world is { IsValid: true }
            && _worldObjects.Adopted.Contains(world);
    }
}
