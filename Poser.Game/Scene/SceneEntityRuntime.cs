using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Application.Viewport;
using Poser.Scene;
using System;
using Poser.Domain.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;
using Poser.Domain.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// Creates the entities a load restores — actors, props, overlays, map
/// objects, lights and cameras — through their accepted owners, and is the
/// rollback's exact inverse for each. It owns no transaction state; every
/// method is one materialization step.
/// </summary>
internal sealed class SceneEntityRuntime : ISceneMaterializer
{
    private static readonly TimeSpan WorldObjectPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly SceneRuntimeHandles _handles;
    private readonly SceneHistoryBinder _history;
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IActorSpawnService _spawns;
    private readonly ISkeletonService _skeletons;
    private readonly PropSpawnService _props;
    private readonly Poser.Game.Overlays.OverlayNodeService _overlays;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly WorldObjects.WorldService _worldObjects;
    private readonly ICameraProjection _viewport;
    private readonly GazeService _gaze;
    private readonly Poser.Application.Integration.IntegrationReset _integration;
    private readonly IActorManager _actors;

    /// <summary>The selection, so a destroy path can never leave it pointing
    /// at something that no longer exists.</summary>
    private readonly Poser.Application.Selection.SelectionSession _selection;

    private readonly IPluginLog _log;

    public SceneEntityRuntime(
        SceneRuntimeHandles handles,
        SceneHistoryBinder history,
        IFramework framework,
        StableBindingRegistry bindings,
        IActorSpawnService spawns,
        ISkeletonService skeletons,
        PropSpawnService props,
        Poser.Game.Overlays.OverlayNodeService overlays,
        ILightingService lighting,
        IVirtualCameraService cameras,
        WorldObjects.WorldService worldObjects,
        ICameraProjection viewport,
        GazeService gaze,
        Poser.Application.Integration.IntegrationReset integration,
        IActorManager actors,
        Poser.Application.Selection.SelectionSession selection,
        IPluginLog log)
    {
        _handles = handles;
        _history = history;
        _framework = framework;
        _bindings = bindings;
        _spawns = spawns;
        _skeletons = skeletons;
        _props = props;
        _overlays = overlays;
        _lighting = lighting;
        _cameras = cameras;
        _worldObjects = worldObjects;
        _viewport = viewport;
        _gaze = gaze;
        _integration = integration;
        _actors = actors;
        _selection = selection;
        _log = log;
    }

    public string WorldObjectName(string path) => WorldObjects.WorldObjectService.DisplayName(path);

    // ── actors ───────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnActor(SceneActor data, out string? detail)
    {
        // Set the model inside the spawn's deferred-draw window. A second
        // SetModelCharaId redraw races the next-tick collection assignment.
        // A saved catalog kind respawns through the catalog, as history's
        // recreate does, so a minion comes back as a minion.
        string? refusal = null;
        var actor = data.SpawnedKind is { } kind
            ? _spawns.SpawnCatalogActor(new(kind, 0, "", "", 0, data.ModelCharaId))
            : _spawns.SpawnNewActor(data.HasCompanionSlot, data.ModelCharaId, out refusal);
        if (actor is null)
        {
            detail = refusal ?? "The spawn service returned no actor.";
            return null;
        }
        detail = null;
        return _handles.Track(SceneEntityKind.Actor, actor);
    }

    // ── props and overlays ───────────────────────────────────────────────

    public SceneEntityHandle? SpawnProp(SceneProp data, out string? detail)
    {
        var handle = _props.SpawnProp(new PropModel(
            data.Name, data.Model, data.Submodel, data.Variant, string.Empty,
            data.Stain0, data.Stain1, data.AnimationVariant));
        if (handle is null)
        {
            detail = "The object spawn failed.";
            return null;
        }
        handle.Transform = data.Transform;
        handle.Visible = data.Visible;
        detail = null;
        return _handles.Track(SceneEntityKind.Prop, handle);
    }

    public SceneEntityHandle? SpawnOverlay(SceneOverlay data, out string? detail)
    {
        if (data.Node is not { } document)
        {
            detail = "The overlay entry carries no node document.";
            return null;
        }
        detail = null;
        var display = _viewport.DisplaySize;
        if (document.Collider == null && data.CenterRelative && display.X > 0f && display.Y > 0f)
        {
            document = document with
            {
                Position = document.Position + new System.Numerics.Vector2(
                    display.X / 2f, display.Y / 2f),
            };
        }
        // An overlay entirely outside the screen exists but shows nothing —
        // the restore says so instead of leaving the user hunting for it.
        var size = Poser.Domain.Presentation.OverlayNodeGeometry
            .DesignSize(document.Kind) * document.Scale;
        if (document.Collider == null && display.X > 0f && display.Y > 0f &&
            (document.Position.X + size.X < 0f ||
             document.Position.Y + size.Y < 0f ||
             document.Position.X > display.X ||
             document.Position.Y > display.Y))
        {
            detail = $"'{document.Name}' sits entirely off screen at this " +
                "resolution, so it will not be visible.";
        }
        var handle = _overlays.Create(document);
        if (handle is null)
        {
            detail = "The overlay node could not be staged.";
            return null;
        }
        return _handles.Track(SceneEntityKind.Overlay, handle);
    }

    // ── world objects ────────────────────────────────────────────────────

    /// <summary>
    /// Restores one world-object entry by SPAWNING its path anew — any
    /// zone, any position. A document never carries a borrow (ruled
    /// 2026-09-01): borrowing is a live-session act, and the load owes
    /// nothing to whatever the map may or may not be standing.
    /// </summary>
    public SceneEntityHandle? AdoptWorldObject(SceneWorldObject data, out string? detail)
    {
        var handle = _worldObjects.Spawn(
            data.Path, data.Transform, data.Visible, out detail);
        if (handle != null && data.Name.Length > 0)
            handle.Name = data.Name;
        if (handle != null)
        {
            if (data.Opacity < 1f)
                handle.Opacity = data.Opacity;
            if (data.Tint is { } tint)
                handle.Tint = tint;
            handle.Stain = data.Stain;
            handle.FurnitureLights = data.FurnitureLights;
            handle.LoopVfx = data.VfxLoop;
            if (Math.Abs(data.VfxSpeed - 1f) > 0.001f)
                handle.VfxSpeed = data.VfxSpeed;
            if (Math.Abs(data.VfxIntensity - 1f) > 0.001f)
                handle.VfxIntensity = data.VfxIntensity;
            if (data.VfxPaused)
                handle.VfxPaused = true;
            handle.NightState = data.NightState;
            if (data.AnimPaused)
                handle.AnimationPaused = true;
        }
        return handle == null ? null : _handles.Track(SceneEntityKind.WorldObject, handle);
    }

    public async Task<IReadOnlyList<SceneEntityHandle>> AwaitWorldObjectsLoaded(
        IReadOnlyList<SceneEntityHandle> worldObjects, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        // Framework thread. With keep, the pending ones are kept in the SAME
        // framework action that names them: no timed release can land
        // between the load naming one and keeping it.
        List<SceneEntityHandle> Loading(bool keep)
        {
            var pending = new List<SceneEntityHandle>();
            foreach (var token in worldObjects)
            {
                if (_handles.Resolve<IWorldObject>(token, SceneEntityKind.WorldObject)
                        is not WorldObjects.AdoptedWorldObject handle
                    || !_worldObjects.IsLoading(handle))
                    continue;
                pending.Add(token);
                if (keep)
                    _worldObjects.KeepUnloaded(handle);
            }
            return pending;
        }

        if (await FrameworkPoll.Until(
                async () => (await _framework.RunOnFrameworkThread(() => Loading(keep: false))).Count == 0,
                bound, WorldObjectPollInterval, cancellation))
            return Array.Empty<SceneEntityHandle>();
        return await _framework.RunOnFrameworkThread(() => Loading(keep: true));
    }

    public void ReleaseWorldObject(SceneEntityHandle token) =>
        _handles.Remove<IWorldObject>(token, SceneEntityKind.WorldObject, _history.WorldObjects.Resolve, entity =>
        {
            var world = (WorldObjects.AdoptedWorldObject)entity;
            if (!_worldObjects.Release(world) && _worldObjects.Adopted.Contains(world))
                throw new InvalidOperationException("The scene world object could not be released.");
        });

    // ── lights ───────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnLight(
        SceneLight data, SceneEntityHandle? attachmentOwner, out string? detail)
    {
        var document = data.Light!;

        // Resolve the exact attachment bone BEFORE any native spawn — a
        // light whose stated parent bone is missing is refused whole, never
        // spawned detached into world space.
        IBone? bone = null;
        if (data.Attachment is { } attachment)
        {
            if (_handles.Resolve<IActor>(attachmentOwner, SceneEntityKind.Actor) is not { } owner)
            {
                detail = "The attachment owner was not restored.";
                return null;
            }
            var skeleton = _skeletons.GetSkeletons(owner)
                .FirstOrDefault(candidate => candidate.Slot == attachment.Slot);
            bone = skeleton?.Bones.FirstOrDefault(candidate =>
                candidate.PartialId == attachment.PartialId &&
                string.Equals(
                    candidate.BoneName, attachment.BoneName,
                    StringComparison.Ordinal));
            if (bone is null)
            {
                detail = $"The attachment bone '{attachment.BoneName}' " +
                    $"({attachment.Slot}/{attachment.PartialId}) does not exist " +
                    "on the restored actor.";
                return null;
            }
        }

        var light = _lighting.SpawnLight(document.Kind);
        if (light is null)
        {
            detail = "The light spawn failed.";
            return null;
        }

        Lighting.LightDocument.Apply(document, light);
        if (bone is not null)
            light.AttachedBone = bone;

        // A gobo the running client no longer ships degrades with a named
        // detail; the light itself is restored.
        detail = null;
        if (!string.IsNullOrEmpty(document.Gobo))
        {
            var gobo = _lighting.Gobos.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Path, document.Gobo,
                    StringComparison.OrdinalIgnoreCase));
            if (gobo == default)
                detail = $"The saved gobo '{document.Gobo}' is not in the library.";
            else if (!_lighting.ApplyGobo(light, gobo))
                detail = $"The gobo '{gobo.Name}' could not be applied.";
        }
        return _handles.Track(SceneEntityKind.Light, light);
    }

    // ── cameras ──────────────────────────────────────────────────────────

    private IVirtualCamera? DefaultCamera =>
        _cameras.Cameras.FirstOrDefault(camera => camera.IsDefault);

    public SceneEntityHandle? DefaultCameraToken() => DefaultCamera is { } camera
        ? _handles.Track(SceneEntityKind.Camera, camera) : null;

    /// <summary>The default camera's document AND what the load can change
    /// beside it: the followed actor and which camera is live. A document
    /// alone left the default camera following a rolled-back actor.</summary>
    public SceneCameraBaseline CaptureDefaultCameraState()
    {
        var camera = DefaultCamera;
        var target = camera?.TargetActor is { } followed
            ? _handles.Track(SceneEntityKind.Actor, followed) : null;
        var live = _cameras.LiveCamera is { } current
            ? _handles.Track(SceneEntityKind.Camera, current) : null;
        return new(
            camera is null ? new CameraFile() : Cameras.CameraDocument.Capture(camera),
            target, camera?.TargetActorName ?? string.Empty,
            camera?.IsTargetLocked ?? false, live);
    }

    public string? ApplyDefaultCamera(SceneCamera data)
    {
        if (DefaultCamera is not { } camera)
            return "The session has no default camera.";
        Cameras.CameraDocument.Apply(data.Camera!, camera);
        return null;
    }

    public SceneEntityHandle? CreateCamera(SceneCamera data, out string? detail)
    {
        // SceneWorkflow activates the saved view after every camera and target is restored.
        var camera = _cameras.CreateCamera(data.Camera!.Kind, makeLive: false);
        if (camera is null)
        {
            detail = "The camera could not be created.";
            return null;
        }
        Cameras.CameraDocument.Apply(data.Camera!, camera);
        detail = null;
        return _handles.Track(SceneEntityKind.Camera, camera);
    }

    public string? SetCameraTarget(
        SceneEntityHandle? camera, SceneEntityHandle targetActor, string displayName,
        bool targetLocked)
    {
        var target = camera == null ? DefaultCamera : _handles.Resolve<IVirtualCamera>(camera, SceneEntityKind.Camera);
        if (target is null)
            return "The session has no default camera.";
        var exactActor = _handles.Require<IActor>(targetActor, SceneEntityKind.Actor);
        // Validate the exact generation before SetTargetActor can touch any
        // native target state; a replacement occupant is never rebound.
        if (_bindings.GetActorId(exactActor) is not { } targetId ||
            _bindings.Resolve(targetId) is not
                { Success: true, Value: { } resolved } ||
            !ReferenceEquals(resolved, exactActor))
            return "The target actor is no longer available.";
        if (!_cameras.SetTargetActor(target, exactActor, targetId, displayName))
            return "The target actor has no draw object.";
        target.IsTargetLocked = targetLocked;
        return null;
    }

    public string? SetLiveCamera(SceneEntityHandle? camera)
    {
        var target = camera == null ? DefaultCamera : _handles.Resolve<IVirtualCamera>(camera, SceneEntityKind.Camera);
        if (target is null)
            return "The session has no default camera.";
        _cameras.SetLive(target);
        return null;
    }

    public void RestoreDefaultCamera(SceneCameraBaseline baseline)
    {
        if (DefaultCamera is not { } camera)
            return;
        Cameras.CameraDocument.Apply(baseline.Camera, camera);
        // The pre-load target only if it is still the same live actor; the
        // load's own target never survives its rollback.
        if (_handles.Resolve<IActor>(baseline.Target, SceneEntityKind.Actor) is { } actor
            && _bindings.GetActorId(actor) is { } id
            && _cameras.SetTargetActor(camera, actor, id, baseline.TargetName))
            camera.IsTargetLocked = baseline.TargetLocked;
        else
            _cameras.ClearTargetActor(camera);
        _cameras.SetLive(
            _handles.Resolve<IVirtualCamera>(baseline.Live, SceneEntityKind.Camera) is { IsValid: true } live
                ? live : camera);
    }

    // ── rollback ─────────────────────────────────────────────────────────

    /// <summary>A load's rollback and undo: the same pre-delete cleanup a
    /// clear runs, so gaze and appearance ownership go with the actor rather
    /// than being reconciled later by name.</summary>
    public void DestroyActor(SceneEntityHandle actor) => _handles.Remove<IActor>(
        actor, SceneEntityKind.Actor, _history.Actors.Resolve, entity =>
    {
        ActorRemovalCleanup.Prepare(entity, _gaze, _integration, _bindings,
            refusal => _log.Warning($"Scene rollback: {refusal}"));
        var lineage = _bindings.GetActorId(entity)?.LogicalId;
        if (!_spawns.DestroyActor(entity) && _actors.Actors.Contains(entity))
            throw new InvalidOperationException("The scene actor could not be destroyed.");
        if (lineage is { } gone)
            _selection.RemoveActorLineage(gone);
    });

    public void DestroyProp(SceneEntityHandle prop) => _handles.Remove<IPropHandle>(
        prop, SceneEntityKind.Prop, _history.Props.Resolve, entity => _props.Destroy((PropHandle)entity));

    public void DestroyOverlay(SceneEntityHandle overlay) =>
        _handles.Remove<IOverlayNode>(overlay, SceneEntityKind.Overlay, _history.Overlays.Resolve,
            entity => _overlays.Destroy((Poser.Game.Overlays.OverlayNodeHandle)entity));

    public void DestroyLight(SceneEntityHandle light) => _handles.Remove<ILight>(
        light, SceneEntityKind.Light, _history.Lights.Resolve, _lighting.DestroyLight);

    public void DestroyCamera(SceneEntityHandle camera) =>
        _handles.Remove<IVirtualCamera>(
            camera, SceneEntityKind.Camera, _history.Cameras.Resolve, _cameras.DestroyCamera);
}
