using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Application.Viewport;
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
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;
using Poser.Domain.Actors;

namespace Poser.Game.Scene;

/// <summary>
/// Creates or reuses the entities in a saved scene. Keeps the previous state
/// of reused map objects so Undo can put back their earlier edits.
/// </summary>
internal sealed class SceneEntityLoader
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
    private readonly IPlaceService _place;
    private readonly IActorRemoveRestore _actorState;
    private readonly Poser.Game.Animation.AnimationSession _animation;
    private sealed record BorrowedBaseline(WorldObjectState State);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneEntityHandle, BorrowedBaseline> _borrowedBaselines = new();

    /// <summary>The selection, so removing something never leaves it
    /// selected.</summary>
    private readonly Poser.Application.Selection.SelectionSession _selection;

    private readonly IPluginLog _log;

    public SceneEntityLoader(
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
        IPluginLog log, IPlaceService place, SceneLifecycleHistory lifecycle, Poser.Game.Animation.AnimationSession animation)
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
        _place = place;
        _actorState = lifecycle.ActorStatePort;
        _animation = animation;
    }

    /// <summary>The name the object list shows for a map object's model path.</summary>
    public string WorldObjectName(string path) => WorldObjects.WorldObjectService.DisplayName(path);

    // ── actors ───────────────────────────────────────────────────────────

    /// <summary>Spawns one actor and gives it the saved model id. Returns null and
    /// the spawn's reason when it fails.</summary>
    public SceneEntityHandle? SpawnActor(SceneActor data, out string? detail)
    {
        // Set the model while spawning, before the actor is first drawn.
        // Changing it afterwards redraws the actor while its collection is
        // being set on the next frame, and the two collide. A saved minion,
        // mount or ornament is spawned from the catalog, as undo does, so it
        // comes back as what it was.
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

    /// <summary>Your own character, for a saved entry marked as yours. Null when it
    /// is not in GPose; the load then spawns a copy.</summary>
    public SceneEntityHandle? FindLocalPlayer() =>
        _actors.Actors.FirstOrDefault(actor =>
            actor.ActorKind is not (ActorKind.Companion or ActorKind.Mount or ActorKind.Ornament)
            && actor.Address != nint.Zero && _actors.IsLocalPlayer(actor)) is { } player
            ? _handles.Track(SceneEntityKind.Actor, player)
            : null;

    /// <summary>Finds the saved character among the actors already in GPose,
    /// skipping actors this load has already used.</summary>
    public SceneEntityHandle? FindMatchingActor(SceneActor data, IReadOnlyCollection<SceneEntityHandle> used)
    {
        var hash = data.IdentityHash ?? SceneActorNames.Hash(SceneActorNames.Resolve(data));
        var taken = used.Select(handle => _handles.Resolve(handle)).ToHashSet();
        var actor = _actors.Actors.FirstOrDefault(candidate => candidate.Address != nint.Zero
            && !candidate.IsCompanion && !_spawns.IsSpawnedActor(candidate)
            && !taken.Contains(candidate) && SceneActorNames.Hash(candidate.Name) == hash);
        return actor == null ? null : _handles.Track(SceneEntityKind.Actor, actor);
    }

    /// <summary>Remembers an actor the load reuses, so undo can put it back as it
    /// was without removing it.</summary>
    public Action CaptureActorRestore(SceneEntityHandle token)
    {
        var actor = _handles.Require<IActor>(token, SceneEntityKind.Actor);
        var state = _actorState.Read(actor);
        var name = _actorState.GetName(actor);
        var id = _bindings.GetActorId(actor)!.Value;
        var animation = _animation.Capture(id);
        return () =>
        {
            bool Current() => ReferenceEquals(_handles.Resolve(token), actor) && _actors.IsAvailable(actor);
            if (!Current()) return;
            _actorState.SetName(actor, name);
            var restored = _animation.Restore(id, animation);
            if (!restored.Success) _log.Warning(restored.Detail ?? "The actor's animation could not be restored.");
            _actorState.Restore(actor, state, Current);
        };
    }

    // ── props and overlays ───────────────────────────────────────────────

    /// <summary>Spawns one object with its saved position and visibility.</summary>
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
        handle.Tint = data.Tint ?? System.Numerics.Vector4.One;
        detail = null;
        return _handles.Track(SceneEntityKind.Prop, handle);
    }

    /// <summary>Adds one overlay from its saved node document. The file check
    /// has already left out entries without one.</summary>
    public SceneEntityHandle? SpawnOverlay(SceneOverlay data, out string? detail)
    {
        var document = data.Node!;
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
        // An overlay entirely off screen exists but shows nothing. Say so,
        // rather than leave someone searching for it.
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
    /// Restores borrowed map objects in place and creates saved spawned objects.
    /// </summary>
    public SceneEntityHandle? AdoptWorldObject(SceneWorldObject data, uint territory, out string? detail)
    {
        detail = null;
        if (!data.Spawned && territory != 0 && territory != _place.Current.TerritoryId)
        {
            detail = "This map object belongs to a different territory.";
            return null;
        }
        var baseline = data.Spawned ? null : _worldObjects.BorrowedState(data.Path, data.MapPosition);
        var handle = data.Spawned
            ? _worldObjects.Spawn(data.Path, data.Transform, data.Visible, out detail)
            : _worldObjects.RestoreBorrowed(data.Path, data.MapPosition, data.Transform, data.Visible, out detail);
        if (handle == null) return null;
        _worldObjects.RestoreState(handle, new WorldObjectState(
            handle.Address, data.Path, data.Spawned, data.Transform, data.Visible)
        {
            Name = data.Name.Length > 0 ? data.Name : null,
            Opacity = data.Opacity,
            Tint = data.Tint,
            Stain = data.Stain,
            FurnitureLights = data.FurnitureLights,
            LoopVfx = data.VfxLoop,
            VfxSpeed = data.VfxSpeed,
            VfxIntensity = data.VfxIntensity,
            VfxPaused = data.VfxPaused,
            NightState = data.NightState,
            AnimationPaused = data.AnimPaused,
        });
        var token = _handles.Track(SceneEntityKind.WorldObject, handle);
        if (baseline is { } previous) _borrowedBaselines.Add(token, new(previous));
        return token;
    }

    /// <summary>Waits, up to <paramref name="bound"/>, for spawned map objects whose
    /// models are still loading, such as housing furniture. Returns the ones still
    /// not loaded. Those are kept rather than removed later, so the load can name
    /// them in its result. Runs on the load's background task.</summary>
    public async Task<IReadOnlyList<SceneEntityHandle>> AwaitWorldObjectsLoaded(
        IReadOnlyList<SceneEntityHandle> worldObjects, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        // Runs on the game's main thread. With keep set, objects still
        // loading are kept in the same step that finds them, so none can be
        // let go on a timer between being found and being kept.
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

    /// <summary>Gives one map object back: a borrowed object gets its earlier state
    /// back, and a spawned copy is removed. Undo of <see cref="AdoptWorldObject"/>.</summary>
    public void ReleaseWorldObject(SceneEntityHandle token) =>
        _handles.Remove<IWorldObject>(token, SceneEntityKind.WorldObject, _history.WorldObjects.Resolve, entity =>
        {
            var world = (WorldObjects.AdoptedWorldObject)entity;
            // The object was already taken over before this load: put back its
            // earlier state instead of letting it go.
            if (_borrowedBaselines.TryGetValue(token, out var baseline))
            {
                _worldObjects.RestoreState(world, baseline.State);
                _borrowedBaselines.Remove(token);
                return;
            }
            if (!_worldObjects.Release(world) && _worldObjects.Adopted.Contains(world))
                throw new InvalidOperationException("The scene world object could not be released.");
        });

    // ── lights ───────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnLight(
        SceneLight data, SceneEntityHandle? attachmentOwner, out string? detail)
    {
        var document = data.Light!;

        // Find the bone the light is attached to before spawning anything.
        // If the bone is missing, the light is not restored at all, rather
        // than spawned loose in the world.
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

        // If the game no longer has the saved gobo, the light is still
        // restored and the message names the gobo.
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

    /// <summary>The default camera's settings, plus what a load can change
    /// besides them: the actor it follows and which camera is live. Without
    /// these, undoing a load could leave the default camera following an
    /// actor that was removed.</summary>
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

    /// <summary>Creates one more camera from its saved settings.</summary>
    public SceneEntityHandle? CreateCamera(SceneCamera data, out string? detail)
    {
        // The load makes the saved camera live only after every camera and target is restored.
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

    /// <summary>Sets the actor a camera the load created follows, and whether it
    /// stays locked to that actor. Returns why it failed, or null.</summary>
    public string? SetCameraTarget(
        SceneEntityHandle camera, SceneEntityHandle targetActor, string displayName,
        bool targetLocked)
    {
        var target = _handles.Require<IVirtualCamera>(camera, SceneEntityKind.Camera);
        var exactActor = _handles.Require<IActor>(targetActor, SceneEntityKind.Actor);
        // Check it is still the same actor before changing the camera's
        // target. If another actor has taken its place, the camera does not
        // follow that one.
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

    /// <summary>Makes a camera the one the game shows. A null camera means the Main
    /// Camera.</summary>
    public string? SetLiveCamera(SceneEntityHandle? camera)
    {
        var target = camera == null ? DefaultCamera : _handles.Require<IVirtualCamera>(camera, SceneEntityKind.Camera);
        if (target is null)
            return "The session has no default camera.";
        _cameras.SetLive(target);
        return null;
    }

    /// <summary>Puts the Main Camera back as <see cref="CaptureDefaultCameraState"/>
    /// found it.</summary>
    public void RestoreDefaultCamera(SceneCameraBaseline baseline)
    {
        if (DefaultCamera is not { } camera)
            return;
        Cameras.CameraDocument.Apply(baseline.Camera, camera);
        // Follow the actor from before the load only if it is still the same
        // actor. The actor the load set is never kept once the load is undone.
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

    /// <summary>Removes an actor when a load fails or is undone. It runs the
    /// same cleanup as clearing the scene, so Poser's gaze and appearance
    /// changes for the actor go with it instead of being matched up later by
    /// name.</summary>
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

    /// <summary>Removes an object the load spawned.</summary>
    public void DestroyProp(SceneEntityHandle prop) => _handles.Remove<IPropHandle>(
        prop, SceneEntityKind.Prop, _history.Props.Resolve, entity => _props.Destroy((PropHandle)entity));

    /// <summary>Removes an overlay the load added.</summary>
    public void DestroyOverlay(SceneEntityHandle overlay) =>
        _handles.Remove<IOverlayNode>(overlay, SceneEntityKind.Overlay, _history.Overlays.Resolve,
            entity => _overlays.Destroy((Poser.Game.Overlays.OverlayNodeHandle)entity));

    /// <summary>Removes a light the load spawned.</summary>
    public void DestroyLight(SceneEntityHandle light) => _handles.Remove<ILight>(
        light, SceneEntityKind.Light, _history.Lights.Resolve, _lighting.DestroyLight);

    /// <summary>Removes a camera the load created.</summary>
    public void DestroyCamera(SceneEntityHandle camera) =>
        _handles.Remove<IVirtualCamera>(
            camera, SceneEntityKind.Camera, _history.Cameras.Resolve, _cameras.DestroyCamera);
}

/// <summary>What a load can change about the session's cameras besides the
/// cameras it creates: the default camera's settings, the actor it follows
/// (null when it follows none) and which camera is live. Undoing the load puts
/// all three back, so the default camera never keeps following an actor the
/// undo removed.</summary>
internal sealed record SceneCameraBaseline(
    CameraFile Camera,
    SceneEntityHandle? Target,
    string TargetName,
    bool TargetLocked,
    SceneEntityHandle? Live);
