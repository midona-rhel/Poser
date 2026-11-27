using Poser.Application.Posing;
using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Identity;
using Poser.Game.Overlays;
using Poser.Game.Posing;
using Poser.Game.WorldObjects;
using Poser.Application.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Records adding and removing things in the scene as undo steps. Something
/// brought back by undo or redo is a new object in the game, so every undo
/// step for one thing shares a slot that points at its current copy.
/// Removing something saves its latest state first. Bringing an actor back
/// can go on for a few frames after its body is recreated. A map object can
/// only be taken over again while the game's original object still exists.
/// Clearing the undo history clears these slots too.
/// </summary>
public sealed class SceneLifecycleHistory : ISceneLifecycleHistory,
    IEntityHistoryBinding<ILight>, IEntityHistoryBinding<IWorldObject>,
    IEntityHistoryBinding<IVirtualCamera>, IEntityHistoryBinding<IPropHandle>,
    IEntityHistoryBinding<IOverlayNode>, IEntityHistoryBinding<IActor>
{
    private readonly EditHistory _history;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;

    /// <summary>One helper per kind of thing keeps the slots for that kind.
    /// This class only passes calls on to them.</summary>
    private readonly LightUndoSteps _lightOwner;
    private readonly CameraUndoSteps _cameraOwner;
    private readonly ActorUndoSteps _actorOwner;
    private readonly PropUndoSteps _propOwner;
    private readonly OverlayUndoSteps _overlayOwner;
    private readonly WorldObjectUndoSteps _worldObjectOwner;

    internal IActorRemoveRestore ActorStatePort => _actorOwner.Port;

    /// <summary>The helpers are internal to the game code, so the plugin
    /// cannot create them. This constructor takes what they need and builds
    /// them.</summary>
    public SceneLifecycleHistory(
        Application.Settings.ConfigurationService configuration,
        EditHistory history,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IActorSpawnService actors,
        IPosingService posing,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        IPoseImportCommands poses,
        Poser.Game.Posing.PoseImportCoordinator imports,
        Poser.Game.Animation.AnimationSession animation,
        Dalamud.Plugin.Services.IFramework framework,
        Dalamud.Plugin.Services.IPluginLog log,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        GazeService gaze,
        Poser.Application.Integration.IntegrationSelectors integration,
        Poser.Application.Integration.McdfTransaction mcdf,
        Poser.Application.Integration.IntegrationReset reset,
        Bindings.StableBindingRegistry bindings,
        IBonePosingService bonePosing,
        IActorManager actorManager,
        ActorStateSnapshots actorStates,
        Integration.ISpawnCollectionPort collections,
        Poser.Game.Posing.PoseExportCapture exports)
        : this(
            history,
            lighting,
            cameras,
            new ActorRemoveRestore(
                configuration,
                actors, posing, skeletons, poseFiles, poses, imports, animation, framework, log,
                gaze, integration, mcdf, reset, bindings, bonePosing, actorManager, actorStates, collections, exports),
            new PropRemoveRestore(props),
            new OverlayRemoveRestore(overlays),
            new WorldObjectReleaseRestore(worldObjects),
            light => bindings.GetLightId(light) is { } id
                ? TransformTargetId.ForLight(id) : null,
            worldObject => bindings.GetWorldObjectId((AdoptedWorldObject)worldObject) is { } id
                ? TransformTargetId.ForWorldObject(id) : null,
            prop => bindings.GetPropId((PropHandle)prop) is { } id
                ? TransformTargetId.ForProp(id) : null,
            overlay => overlay is OverlayNodeHandle { Kind: OverlayNodeKind.Collider } node
                && bindings.GetOverlayId(node) is { } id
                ? TransformTargetId.ForCollider(id) : null,
            actor => bindings.GetActorId(actor) is { } id
                ? TransformTargetId.ForActor(id) : null)
    { }

    /// <summary>For tests: actors, props, overlays and world objects are
    /// passed in as interfaces, so undo and redo can be tested without real
    /// game objects.</summary>
    internal SceneLifecycleHistory(
        EditHistory history,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IActorRemoveRestore actors,
        IPropRemoveRestore props,
        IOverlayRemoveRestore overlays,
        IWorldObjectReleaseRestore worldObjects,
        Func<ILight, TransformTargetId?>? lightTarget = null,
        Func<object, TransformTargetId?>? worldObjectTarget = null,
        Func<object, TransformTargetId?>? propTarget = null,
        Func<object, TransformTargetId?>? overlayTarget = null,
        Func<IActor, TransformTargetId?>? actorTarget = null)
    {
        _history = history;
        _lighting = lighting;
        _cameras = cameras;
        _worldObjectOwner = new(history, worldObjects, worldObjectTarget);
        _lightOwner = new(history, lighting, lightTarget);
        _cameraOwner = new(history, cameras);
        _actorOwner = new(history, actors, actorTarget);
        _propOwner = new(history, props, propTarget);
        _overlayOwner = new(history, overlays, overlayTarget);
        // A slot only exists for undo steps. When the history is cleared,
        // most importantly on leaving GPose, the slots point at objects that
        // no longer exist, so they are cleared too.
        _history.Cleared += ForgetSlots;
    }

    private void ForgetSlots()
    {
        _lightOwner.Clear();
        _cameraOwner.Clear();
        _actorOwner.Clear();
        _propOwner.Clear();
        _overlayOwner.Clear();
        _worldObjectOwner.Clear();
    }

    // ── history identity ─────────────────────────────────────────────────

    // Only undo and redo may follow an old object to the copy that replaced
    // it. Ids handed out elsewhere stay invalid once the object is gone.
    ILight? IEntityHistoryResolver<ILight>.Resolve(ILight light) => _lightOwner.CurrentLight(light);

    IActor? IEntityHistoryResolver<IActor>.Resolve(IActor actor) => _actorOwner.Resolve(actor);

    void IEntityHistoryBinding<IActor>.BindReplacement(IActor original, IActor replacement) =>
        _actorOwner.BindReplacement(original, replacement);

    void IEntityHistoryBinding<ILight>.BindReplacement(ILight original, ILight replacement) =>
        _lightOwner.BindReplacement(original, replacement);

    void IEntityHistoryBinding<IWorldObject>.BindReplacement(IWorldObject original, IWorldObject replacement) =>
        _worldObjectOwner.BindReplacement(original, replacement);

    void IEntityHistoryBinding<IVirtualCamera>.BindReplacement(IVirtualCamera original, IVirtualCamera replacement) =>
        _cameraOwner.BindReplacement(original, replacement);

    void IEntityHistoryBinding<IPropHandle>.BindReplacement(IPropHandle original, IPropHandle replacement) =>
        _propOwner.BindReplacement(original, replacement);

    void IEntityHistoryBinding<IOverlayNode>.BindReplacement(IOverlayNode original, IOverlayNode replacement) =>
        _overlayOwner.BindReplacement(original, replacement);

    IWorldObject? IEntityHistoryResolver<IWorldObject>.Resolve(IWorldObject worldObject) =>
        _worldObjectOwner.CurrentWorldObject(worldObject);

    IVirtualCamera? IEntityHistoryResolver<IVirtualCamera>.Resolve(IVirtualCamera camera) => _cameraOwner.Resolve(camera);

    IPropHandle? IEntityHistoryResolver<IPropHandle>.Resolve(IPropHandle prop) => _propOwner.Resolve(prop) as IPropHandle;

    IOverlayNode? IEntityHistoryResolver<IOverlayNode>.Resolve(IOverlayNode overlay) => _overlayOwner.Resolve(overlay) as IOverlayNode;

    // ── lights ───────────────────────────────────────────────────────────

    public ILight? SpawnLight(LightKind kind) => _lightOwner.SpawnLight(kind);

    public ILight? CloneLight(ILight source) => _lightOwner.CloneLight(source);

    public ILight? RecordSpawnedLight(string description, ILight? light) =>
        _lightOwner.RecordSpawnedLight(description, light);

    public void DestroyLight(ILight light) => _lightOwner.DestroyLight(light);

    // ── cameras ──────────────────────────────────────────────────────────

    public IVirtualCamera? CreateCamera(CameraKind kind) => _cameraOwner.CreateCamera(kind);

    public IVirtualCamera? CloneCamera(IVirtualCamera source) => _cameraOwner.CloneCamera(source);

    public void DestroyCamera(IVirtualCamera camera) => _cameraOwner.DestroyCamera(camera);

    // ── actors ───────────────────────────────────────────────────────────

    /// <summary>Records one actor spawn as an undo step. <paramref name="spawn"/>
    /// must be safe to run again, because redo runs it.</summary>
    public IActor? SpawnActor(string description, Func<IActor?> spawn, IActor? source = null, string? name = null) =>
        _actorOwner.SpawnActor(description, spawn, source, name);

    /// <summary>See <see cref="IActorRemoveRestore.WhenPosable"/>.</summary>
    public void WhenPosable(IActor actor, Action<IActor> act) =>
        _actorOwner.WhenPosable(actor, act);

    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source) =>
        _actorOwner.SpawnActorWithPose(description, spawn, source);

    /// <summary>
    /// Removes an actor Poser spawned, saving its state first so undo can
    /// bring it back. This works whether the actor came from a scene file or
    /// was added by hand, and whether or not the undo step that added it still
    /// exists.
    /// </summary>
    public bool DespawnActor(IActor actor) => _actorOwner.DespawnActor(actor, out _);

    /// <summary>As <see cref="DespawnActor(IActor)"/>; <paramref name="note"/> says
    /// what undo cannot bring back.</summary>
    public bool DespawnActor(IActor actor, out string? note) =>
        _actorOwner.DespawnActor(actor, out note);

    // ── props ────────────────────────────────────────────────────────────

    /// <summary>Spawns Brio's default prop, for when no model was chosen.</summary>
    public object? SpawnProp() => _propOwner.SpawnProp();

    public object? SpawnProp(PropModel model) => _propOwner.SpawnProp(model);

    public object? CloneProp(object source) => _propOwner.CloneProp(source);

    public void DestroyProp(object prop) => _propOwner.DestroyProp(prop);

    // ── overlay nodes ────────────────────────────────────────────────────

    public object? CloneOverlay(object source) => _overlayOwner.CloneOverlay(source);

    public object? SpawnOverlay(OverlayNodeKind kind) => _overlayOwner.SpawnOverlay(kind);

    internal OverlayUndoSteps.PendingOverlayGroup BeginOverlayGroup(
        IReadOnlyList<OverlayNodeState> states, GroupSteps groupSteps) =>
        _overlayOwner.BeginOverlayGroup(states, groupSteps);

    internal SceneGroup? TryCompleteOverlayGroup(OverlayUndoSteps.PendingOverlayGroup pending, string name,
        SceneGroups groups, GroupSteps groupSteps,
        Func<object, SelectionId?> selection,
        Action<SelectionId, int>? initialize = null) =>
        _overlayOwner.TryCompleteOverlayGroup(pending, name, groups, groupSteps, selection, initialize);

    internal void AbortOverlayGroup(OverlayUndoSteps.PendingOverlayGroup pending) =>
        _overlayOwner.AbortOverlayGroup(pending);

    /// <summary>Adds one overlay as an undo step, from its full state: a new
    /// one, a duplicate of the selected one, or a restored one.</summary>
    public object? SpawnOverlay(OverlayNodeState state) => _overlayOwner.SpawnOverlay(state);

    public void DestroyOverlay(object overlay) => _overlayOwner.DestroyOverlay(overlay);

    // ── adopted world objects ────────────────────────────────────────────

    /// <summary>Takes over one map object as an undo step. Undoing it gives
    /// the object back to the map, exactly where the map placed it; it never
    /// destroys the object.</summary>
    internal object? AdoptWorldObject(nint address)
        => _worldObjectOwner.Adopt(address);

    internal void RecordAcquiredWorldObject(IWorldObject worldObject) =>
        _worldObjectOwner.AppendAcquisition(worldObject);

    /// <summary>Copies a world object's settings into a new object, with the next free name.</summary>
    public IWorldObject? CloneWorldObject(IWorldObject source) =>
        _worldObjectOwner.Clone(source);

    /// <summary>Spawns one object from a model path; undo removes the created object.</summary>
    public object? SpawnWorldObject(string path, Transform placement, bool visible) =>
        _worldObjectOwner.Spawn(path, placement, visible);

    /// <summary>Gives one taken-over object back to the map as an undo step.
    /// The step is recorded only once the object has really been given back.</summary>
    internal bool ReleaseWorldObject(object worldObject) =>
        _worldObjectOwner.Release(worldObject);

    /// <summary>Removes the given things as one undo step. Each is removed the
    /// usual way for its kind, and only those really removed are counted.</summary>
    public int DestroySelection(
        IReadOnlyList<IActor>? actors = null,
        IReadOnlyList<object>? props = null,
        IReadOnlyList<ILight>? lights = null,
        IReadOnlyList<IVirtualCamera>? cameras = null,
        IReadOnlyList<object>? overlays = null)
    {
        int removed = 0;
        var actorPort = _actorOwner.Port;
        var propPort = _propOwner.Port;
        var overlayPort = _overlayOwner.Port;
        _history.RecordLifecycleBatch("Remove selection", () =>
        {
            foreach (var actor in actors ?? Array.Empty<IActor>())
                if (actorPort.IsSpawned(actor) && DespawnActor(actor)) removed++;
            foreach (var prop in props ?? Array.Empty<object>())
            {
                if (!propPort.IsLive(prop)) continue;
                DestroyProp(prop);
                if (!propPort.IsLive(prop)) removed++;
            }
            foreach (var light in lights ?? Array.Empty<ILight>())
            {
                if (!_lighting.Lights.Contains(light)) continue;
                DestroyLight(light);
                if (!_lighting.Lights.Contains(light)) removed++;
            }
            foreach (var camera in cameras ?? Array.Empty<IVirtualCamera>())
            {
                if (camera.IsDefault || !_cameras.Cameras.Contains(camera)) continue;
                DestroyCamera(camera);
                if (!_cameras.Cameras.Contains(camera)) removed++;
            }
            foreach (var overlay in overlays ?? Array.Empty<object>())
            {
                if (!overlayPort.IsLive(overlay)) continue;
                DestroyOverlay(overlay);
                if (!overlayPort.IsLive(overlay)) removed++;
            }
        });
        return removed;
    }
}
