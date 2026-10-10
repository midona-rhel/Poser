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
using Poser.Entities;
using Poser.Files;
using Poser.Services;
using Poser.Application.Scene;
using Poser.Domain.Transforms;

namespace Poser.Game.Scene;

/// <summary>
/// The ONE seam through which an entity enters or leaves the scene by a user's
/// act, so that act lands in the SAME history the transforms do.
///
/// <para>A transform is undone by restoring state onto something that still
/// exists; a spawn has no "before state" of an object that did not exist, so
/// it is undone by running the opposite act through the service that owns the
/// entity — <see cref="SceneLifecyclePatch"/>. The two directions of one entry
/// are exact inverses and each reports whether it landed, so a spawn the game
/// refuses leaves the entry where it was instead of eating a step of the
/// user's history.</para>
///
/// <para>IDENTITY across the pair is the hard part, and it is why the SLOT —
/// not the entry — is the unit here. Undo destroys the very object the entry
/// was created for, so nothing may hold a native handle, and every entry that
/// names ONE entity must name the SAME slot: "add light" and the later "remove
/// light" are two entries about one light, and undoing past the removal has to
/// destroy the light the removal's own undo just re-created, not the corpse the
/// add was born holding. The typed slot owners provide that re-binding,
/// keyed by the live instance and re-keyed every time a restore mints a new
/// one.</para>
///
/// <para>A slot carries the live instance when there is one, plus the entity's
/// own document (the same <c>.poserlight</c> / <c>.posercam</c> mapping export
/// and scene capture use) captured at the MOMENT OF REMOVAL. Redo therefore
/// restores the entity as the user last had it, not as it was born: an edited
/// light that is deleted and undone comes back edited.</para>
///
/// <para>A PROP's whole identity is the model triple it was spawned from, so
/// a removal captures that plus the transform and visibility the user gave it
/// and comes back as itself. Clearing the list is one act of the user's, so it
/// is one entry over every slot it took.</para>
///
/// <para>An ACTOR is the one whose document cannot rebuild it: its appearance
/// is a redraw, not a receipt, so it is always brought back by re-running the
/// call that made it, and the document then puts back what that call does not
/// decide — placement, visibility, pose. Its restore is also the one that
/// outlives the frame it starts in (see <see cref="IActorLifecycle"/>). A
/// despawn therefore takes an entry only where this seam recorded the spawn;
/// where it did not, the despawn is refused BY NAME rather than passing for
/// an undoable one.</para>
///
/// <para>Borrowed world lights are re-acquired only from the same original
/// native incarnation. Built-in GPose cameras/lights do not have a recreatable
/// lifecycle here.</para>
///
/// <para>An entity that leaves by some path this seam does not own — a scene
/// import, the game itself — leaves its slot holding a dead handle. Every
/// direction therefore checks the entity is still there before touching it: a
/// removal whose entity is already gone is SATISFIED (nothing left to remove),
/// while a restore with no document behind it FAILS rather than minting a
/// default-valued impostor. Leaving GPose clears the history outright and the
/// slots go with it (<see cref="TransformHistory.Cleared"/>), so a slot never
/// outlives the session that made it.</para>
/// </summary>
public sealed class SceneLifecycleHistory : ISceneLifecycleHistory,
    IEntityHistoryBinding<ILight>, IEntityHistoryBinding<IWorldObject>,
    IEntityHistoryBinding<IVirtualCamera>, IEntityHistoryBinding<IPropHandle>,
    IEntityHistoryBinding<IOverlayNode>, IEntityHistoryBinding<IActor>
{
    private readonly TransformHistory _history;
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
        Config.ConfigurationService configuration,
        TransformHistory history,
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
        Integration.ISpawnCollectionPort collections)
        : this(
            history,
            lighting,
            cameras,
            new ActorServiceLifecycle(
                configuration,
                actors, posing, skeletons, poseFiles, poses, imports, animation, framework, log,
                gaze, integration, mcdf, reset, bindings, bonePosing, actorManager, actorStates, collections),
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
        TransformHistory history,
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

    internal SceneGroup SpawnOverlayGroup(string name,
        IReadOnlyList<OverlayNodeState> states, SceneGroups groups,
        GroupSteps groupSteps,
        Func<object, SelectionId?> selection,
        Action<SelectionId, int>? initialize = null) =>
        _overlayOwner.SpawnOverlayGroup(name, states, groups, groupSteps, selection, initialize);

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
