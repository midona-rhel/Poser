using Poser.Application.Posing;
using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Identity;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Application.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Records entity creation and removal in the shared edit history.
/// Each entity's entries share a slot so undo and redo resolve the current
/// replacement instance. Removal captures the latest editable state.
/// Actor restoration may continue on later ticks after the body is recreated;
/// borrowed world objects can only be reclaimed while their original native
/// instance still exists. Clearing history also clears these slots.
/// </summary>
public sealed class SceneLifecycleHistory : ISceneLifecycleHistory,
    IEntityHistoryBinding<ILight>, IEntityHistoryBinding<IWorldObject>,
    IEntityHistoryBinding<IVirtualCamera>, IEntityHistoryBinding<IPropHandle>,
    IEntityHistoryBinding<IOverlayNode>, IEntityHistoryBinding<IActor>
{
    private readonly EditHistory _history;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;

    /// <summary>Each family owner maps its instances to slots shared by
    /// every history entry for that entity; this class only dispatches.</summary>
    private readonly LightLifecycleOwner _lightOwner;
    private readonly CameraLifecycleOwner _cameraOwner;
    private readonly ActorLifecycleOwner _actorOwner;
    private readonly PropLifecycleOwner _propOwner;
    private readonly OverlayLifecycleOwner _overlayOwner;
    private readonly WorldObjectLifecycleOwner _worldObjectOwner;

    internal IActorLifecycle ActorStatePort => _actorOwner.Port;

    /// <summary>Composition root for the Game-internal owners: the host
    /// cannot name them, so the production ports' dependencies are taken
    /// here and handed straight to those ports.</summary>
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
        PoseImportCoordinator imports,
        Poser.Application.Animation.AnimationSession animation,
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
            new ActorServiceLifecycle(
                configuration,
                actors, posing, skeletons, poseFiles, poses, imports, animation, framework, log,
                gaze, integration, mcdf, reset, bindings, bonePosing, actorManager, actorStates, collections, exports),
            new PropServiceLifecycle(props),
            new OverlayServiceLifecycle(overlays),
            new WorldObjectServiceLifecycle(worldObjects),
            light => bindings.GetLightId(light) is { } id
                ? TransformTargetId.ForLight(id) : null,
            worldObject => bindings.GetWorldObjectId((AdoptedWorldObject)worldObject) is { } id
                ? TransformTargetId.ForWorldObject(id) : null,
            prop => bindings.GetPropId((PropHandle)prop) is { } id
                ? TransformTargetId.ForProp(id) : null,
            overlay => overlay is OverlayNodeHandle { Kind: OverlayNodeKind.Collider } node
                && bindings.GetOverlayId(node) is { } id
                ? TransformTargetId.ForCollider(id) : null)
    { }

    /// <summary>Test seam: the actor, prop and overlay halves as ports, so an
    /// entry's two directions can be exercised without a native scene object
    /// and without a native UI node.</summary>
    internal SceneLifecycleHistory(
        EditHistory history,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IActorLifecycle actors,
        IPropLifecycle props,
        IOverlayLifecycle overlays,
        IWorldObjectLifecycle worldObjects,
        Func<ILight, TransformTargetId?>? lightTarget = null,
        Func<object, TransformTargetId?>? worldObjectTarget = null,
        Func<object, TransformTargetId?>? propTarget = null,
        Func<object, TransformTargetId?>? overlayTarget = null)
    {
        _history = history;
        _lighting = lighting;
        _cameras = cameras;
        _worldObjectOwner = new(history, worldObjects, worldObjectTarget);
        _lightOwner = new(history, lighting, lightTarget);
        _cameraOwner = new(history, cameras);
        _actorOwner = new(history, actors);
        _propOwner = new(history, props, propTarget);
        _overlayOwner = new(history, overlays, overlayTarget);
        // A slot exists only to serve entries, and is only ever minted by
        // this seam recording one. When the history drops every entry —
        // leaving GPose is the clear that matters — the slots are holding
        // handles into a session that no longer exists, so they go with it.
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

    // History alone may resolve an old wrapper to its successor. Public IDs
    // and acquisition receipts remain expired after release.
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

    /// <summary>Records one actor spawn. <paramref name="spawn"/> must be
    /// re-runnable: it is the redo.</summary>
    public IActor? SpawnActor(string description, Func<IActor?> spawn, IActor? source = null, string? name = null) =>
        _actorOwner.SpawnActor(description, spawn, source, name);

    /// <summary>See <see cref="IActorLifecycle.WhenPosable"/>.</summary>
    public void WhenPosable(IActor actor, Action<IActor> act) =>
        _actorOwner.WhenPosable(actor, act);

    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source) =>
        _actorOwner.SpawnActorWithPose(description, spawn, source);

    /// <summary>
    /// Removes an owned actor through the shared state capture, regardless of
    /// whether it came from a scene file or its creation entry still exists.
    /// </summary>
    public bool DespawnActor(IActor actor) => _actorOwner.DespawnActor(actor, out _);

    /// <summary>As <see cref="DespawnActor(IActor)"/>; <paramref name="note"/> tells the
    /// user what the recorded undo cannot bring back.</summary>
    public bool DespawnActor(IActor actor, out string? note) =>
        _actorOwner.DespawnActor(actor, out note);

    // ── props ────────────────────────────────────────────────────────────

    /// <summary>Brio's default prop, for the row that names no model.</summary>
    public object? SpawnProp() => _propOwner.SpawnProp();

    public object? SpawnProp(PropModel model) => _propOwner.SpawnProp(model);

    public object? CloneProp(object source) => _propOwner.CloneProp(source);

    public void DestroyProp(object prop) => _propOwner.DestroyProp(prop);

    // ── overlay nodes ────────────────────────────────────────────────────

    public object? CloneOverlay(object source) => _overlayOwner.CloneOverlay(source);

    public object? SpawnOverlay(OverlayNodeKind kind) => _overlayOwner.SpawnOverlay(kind);

    internal OverlayLifecycleOwner.PendingOverlayGroup BeginOverlayGroup(
        IReadOnlyList<OverlayNodeState> states, GroupSteps groupSteps) =>
        _overlayOwner.BeginOverlayGroup(states, groupSteps);

    internal SceneGroup? TryCompleteOverlayGroup(OverlayLifecycleOwner.PendingOverlayGroup pending, string name,
        SceneGroups groups, GroupSteps groupSteps,
        Func<object, SelectionId?> selection,
        Action<SelectionId, int>? initialize = null) =>
        _overlayOwner.TryCompleteOverlayGroup(pending, name, groups, groupSteps, selection, initialize);

    internal void AbortOverlayGroup(OverlayLifecycleOwner.PendingOverlayGroup pending) =>
        _overlayOwner.AbortOverlayGroup(pending);

    /// <summary>Records one overlay node the user added, from a complete
    /// document: a fresh create, a duplicate of the selected node, or a
    /// restored one.</summary>
    public object? SpawnOverlay(OverlayNodeState state) => _overlayOwner.SpawnOverlay(state);

    public void DestroyOverlay(object overlay) => _overlayOwner.DestroyOverlay(overlay);

    // ── adopted world objects ────────────────────────────────────────────

    /// <summary>Takes one BG object into the scene, journalled. Undoing it
    /// RELEASES the claim, which puts the object back exactly where the map
    /// stood it — an adoption's inverse is never a destroy.</summary>
    internal object? AdoptWorldObject(nint address)
        => _worldObjectOwner.Adopt(address);

    internal void RecordAcquiredWorldObject(IWorldObject worldObject) =>
        _worldObjectOwner.AppendAcquisition(worldObject);

    /// <summary>Copies a world asset's authored properties with the next display name.</summary>
    public IWorldObject? CloneWorldObject(IWorldObject source) =>
        _worldObjectOwner.Clone(source);

    /// <summary>Spawns one object from a model path; undo removes the created object.</summary>
    public object? SpawnWorldObject(string path, Transform placement, bool visible) =>
        _worldObjectOwner.Spawn(path, placement, visible);

    /// <summary>Gives one adopted object back to the map, journalled. The
    /// lifecycle owner captures a confirmed release before appending its entry.</summary>
    internal bool ReleaseWorldObject(object worldObject) =>
        _worldObjectOwner.Release(worldObject);

    /// <summary>Compatibility entry point for handle-based callers. Each entity
    /// keeps its normal removal owner, and only confirmed removals are counted.</summary>
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
