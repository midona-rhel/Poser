using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>What a prop entry has to put back: the model it was spawned from
/// plus everything the user can change about it afterwards. Captured at the
/// MOMENT OF REMOVAL, for the same reason a light's document is.</summary>
internal readonly record struct PropState(
    string Name,
    PropModel Model,
    Transform Transform,
    bool Visible);

/// <summary>
/// The prop half of <see cref="SceneLifecycleHistory"/>. A spawned prop is a
/// native graphics-scene object with no entity interface of its own — the
/// scene runtime already names one by an opaque token for exactly that reason
/// — so this states only the acts an entry performs on that token.
/// <see cref="PropServiceLifecycle"/> is the sole production implementation;
/// the indirection is what lets an entry's two directions be proven without
/// the game.
/// </summary>
internal interface IPropLifecycle
{
    IReadOnlyList<object> Props { get; }

    object? Spawn(PropModel model);

    bool IsLive(object prop);

    void Destroy(object prop);

    PropState Read(object prop);

    void Apply(object prop, PropState state);
}

internal sealed class PropServiceLifecycle : IPropLifecycle
{
    private readonly PropSpawnService _props;

    public PropServiceLifecycle(PropSpawnService props) => _props = props;

    public IReadOnlyList<object> Props
    {
        get
        {
            var live = new List<object>(_props.Props.Count);
            foreach (var prop in _props.Props)
                live.Add(prop);
            return live;
        }
    }

    public object? Spawn(PropModel model) => _props.SpawnProp(model);

    public bool IsLive(object prop) => ((PropHandle)prop).IsValid;

    public void Destroy(object prop) => _props.Destroy((PropHandle)prop);

    public PropState Read(object prop)
    {
        var handle = (PropHandle)prop;
        return new PropState(
            handle.Name, handle.Model, handle.Transform, handle.Visible);
    }

    public void Apply(object prop, PropState state)
    {
        var handle = (PropHandle)prop;
        handle.Name = state.Name;
        handle.Transform = state.Transform;
        handle.Visible = state.Visible;
    }
}

/// <summary>What an actor entry has to put back BEYOND the spawn that made
/// it: where the user had stood it, whether they had it in sight, and the pose
/// they had authored on it. Captured at the MOMENT OF REMOVAL, exactly as a
/// light's document and a prop's state are.</summary>
internal readonly record struct ActorState(
    Transform Placement,
    bool Visible,
    PoseFile? Pose)
{
    /// <summary>Partial-root scales by "partial:bone", the head scaling a
    /// pose file cannot carry (its bones are keyed by name and the roots
    /// share the body's). Applied after the pose lands.</summary>
    public IReadOnlyDictionary<string, System.Numerics.Vector3>? PartialRootScales { get; init; }

    /// <summary>Physics bones by "partial:bone": what sits on them beyond
    /// the simulation (Customize+ offset, rotation, scale; Poser transforms)
    /// as final-minus-raw deltas, applied on the copy's own raw.</summary>
    public IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)>? PhysicsDeltas { get; init; }
}

/// <summary>
/// The actor half of <see cref="SceneLifecycleHistory"/>. An actor's restore
/// is the one that cannot finish in the frame it starts — a respawned body's
/// draw object, and the skeleton hanging off it, are several ticks behind the
/// call that made them — so the seam names the acts and this port owns the
/// waiting. <see cref="ActorServiceLifecycle"/> is the sole production
/// implementation; the indirection is what lets an entry's two directions be
/// proven without the game.
/// </summary>
internal interface IActorLifecycle
{
    bool IsSpawned(object actor);

    bool Destroy(object actor);

    ActorState Read(object actor);

    /// <summary>Runs <paramref name="act"/> once the actor's body is
    /// posable — the same wait a restore gets — or reports that it never
    /// became so.</summary>
    void WhenPosable(object actor, Action<object> act);

    /// <summary>Puts <paramref name="state"/> back onto a JUST-RESPAWNED
    /// actor. Placement and pose land once the body is posable, so this
    /// returns long before the actor looks right; the restore is complete or
    /// it says why, and never fails the entry that asked for it — the actor
    /// is back either way.</summary>
    void Restore(object actor, ActorState state);

    /// <summary>The seam's refusal channel. An act it cannot journal says so
    /// here rather than passing for one it can.</summary>
    void Note(string detail);
}

/// <summary>
/// The overlay-node half of <see cref="SceneLifecycleHistory"/>, the prop
/// port's twin. A node's whole identity IS its document, so — like a prop and
/// unlike an actor — both directions of a removal are exact, and the port
/// exists for the reason the prop port does: an entry's two directions must be
/// provable without the game's UI.
/// </summary>
internal interface IOverlayLifecycle
{
    IReadOnlyList<object> Overlays { get; }

    object? Create(OverlayNodeState state);

    bool IsLive(object overlay);

    void Destroy(object overlay);

    OverlayNodeState Read(object overlay);
}

internal sealed class OverlayServiceLifecycle : IOverlayLifecycle
{
    private readonly OverlayNodeService _overlays;

    public OverlayServiceLifecycle(OverlayNodeService overlays) =>
        _overlays = overlays;

    public IReadOnlyList<object> Overlays
    {
        get
        {
            var live = new List<object>(_overlays.Nodes.Count);
            foreach (var overlay in _overlays.Nodes)
                live.Add(overlay);
            return live;
        }
    }

    public object? Create(OverlayNodeState state) => _overlays.Create(state);

    public bool IsLive(object overlay) => ((OverlayNodeHandle)overlay).IsValid;

    public void Destroy(object overlay) =>
        _overlays.Destroy((OverlayNodeHandle)overlay);

    public OverlayNodeState Read(object overlay) =>
        ((OverlayNodeHandle)overlay).State;
}

/// <summary>What a WORLD OBJECT entry has to put back. A BORROWED
/// object's identity is the address the claim was taken at — the map's
/// own thing, re-claimed. A SPAWNED one was DESTROYED by its release, so
/// its undo re-creates the path anew; re-adopting its freed address
/// dereferenced a dead vtable and crashed (2026-09-01).</summary>
internal readonly record struct WorldObjectState(
    nint Address,
    string Path,
    bool Spawned,
    Transform Placement,
    bool Visible);

/// <summary>
/// The adopted-world-object half of <see cref="SceneLifecycleHistory"/>. It is
/// the one half whose "remove" is a RESTORE rather than a destroy: releasing a
/// claim gives the map its object back exactly as it stood, and re-adopting is
/// taking the same address again. Both directions are therefore exactly
/// statable, which is why an adoption takes an entry where a captured world
/// LIGHT does not — that one has no address-stable inverse to state.
/// </summary>
internal interface IWorldObjectLifecycle
{
    IReadOnlyList<object> WorldObjects { get; }

    object? Adopt(nint address);

    /// <summary>Re-creates a spawned entry from its recorded path.</summary>
    object? Spawn(string path, Transform placement, bool visible);

    /// <summary>Whether the world graph still stands this address — the
    /// deref guard before any re-adopt: a streamed-out or destroyed
    /// object's memory is not readable.</summary>
    bool AddressLive(nint address);

    bool IsLive(object worldObject);

    void Release(object worldObject);

    WorldObjectState Read(object worldObject);

    void Apply(object worldObject, WorldObjectState state);
}

internal sealed class WorldObjectServiceLifecycle : IWorldObjectLifecycle
{
    private readonly WorldObjectService _worldObjects;

    public WorldObjectServiceLifecycle(WorldObjectService worldObjects) =>
        _worldObjects = worldObjects;

    public IReadOnlyList<object> WorldObjects
    {
        get
        {
            var live = new List<object>(_worldObjects.Adopted.Count);
            foreach (var worldObject in _worldObjects.Adopted)
                live.Add(worldObject);
            return live;
        }
    }

    public object? Adopt(nint address) => _worldObjects.Adopt(address);

    public object? Spawn(string path, Transform placement, bool visible) =>
        _worldObjects.Spawn(path, placement, visible, out _);

    public bool AddressLive(nint address)
    {
        foreach (var row in _worldObjects.EnumerateWorld())
            if (row.Address == address)
                return true;
        return false;
    }

    public bool IsLive(object worldObject) =>
        ((AdoptedWorldObject)worldObject).IsValid;

    public void Release(object worldObject) =>
        _worldObjects.Release((AdoptedWorldObject)worldObject);

    public WorldObjectState Read(object worldObject)
    {
        var handle = (AdoptedWorldObject)worldObject;
        return new WorldObjectState(
            handle.Address, handle.Path, handle.Spawned,
            handle.Transform, handle.Visible);
    }

    public void Apply(object worldObject, WorldObjectState state)
    {
        var handle = (AdoptedWorldObject)worldObject;
        handle.Transform = state.Placement;
        handle.Visible = state.Visible;
    }
}

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
/// add was born holding. <see cref="_lightSlots"/> and its siblings are that
/// re-binding, keyed by the live instance and re-keyed every time a restore
/// mints a new one.</para>
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
/// <para>Only OWNED entities are recorded. A captured world light is borrowed,
/// not spawned, and its release is a restoration of the game's own object; the
/// default GPose camera cannot be destroyed at all. Neither has an inverse
/// this seam can state, so neither takes an entry.</para>
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
public sealed class SceneLifecycleHistory
{
    private readonly TransformHistory _history;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IActorLifecycle _actors;
    private readonly IPropLifecycle _props;
    private readonly IOverlayLifecycle _overlayNodes;
    private readonly IWorldObjectLifecycle _worldObjects;

    /// <summary>Live instance → slot, by reference: the re-binding that makes
    /// every entry about one entity share one slot. Keys are dropped as the
    /// entity is removed and re-added as a restore mints its successor.
    /// </summary>
    private readonly Dictionary<object, LightSlot> _lightSlots =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<object, CameraSlot> _cameraSlots =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<object, ActorSlot> _actorSlots =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<object, PropSlot> _propSlots =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<object, OverlaySlot> _overlaySlots =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<object, WorldObjectSlot> _worldObjectSlots =
        new(ReferenceEqualityComparer.Instance);

    public SceneLifecycleHistory(
        TransformHistory history,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IActorSpawnService actors,
        IPosingService posing,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        Posing.CleanPoseFacade poses,
        Dalamud.Plugin.Services.IFramework framework,
        Dalamud.Plugin.Services.IPluginLog log,
        PropSpawnService props,
        OverlayNodeService overlays,
        WorldObjectService worldObjects,
        IGazeService gaze,
        Poser.Application.Integration.ActorIntegrationSession integration,
        Bindings.StableBindingRegistry bindings,
        IBonePosingService bonePosing)
        : this(
            history,
            lighting,
            cameras,
            new ActorServiceLifecycle(
                actors, posing, skeletons, poseFiles, poses, framework, log,
                gaze, integration, bindings, bonePosing),
            new PropServiceLifecycle(props),
            new OverlayServiceLifecycle(overlays),
            new WorldObjectServiceLifecycle(worldObjects))
    {
    }

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
        IWorldObjectLifecycle worldObjects)
    {
        _history = history;
        _lighting = lighting;
        _cameras = cameras;
        _actors = actors;
        _props = props;
        _overlayNodes = overlays;
        _worldObjects = worldObjects;
        // A slot exists only to serve entries, and is only ever minted by
        // this seam recording one. When the history drops every entry —
        // leaving GPose is the clear that matters — the slots are holding
        // handles into a session that no longer exists, so they go with it.
        _history.Cleared += ForgetSlots;
    }

    private void ForgetSlots()
    {
        _lightSlots.Clear();
        _cameraSlots.Clear();
        _actorSlots.Clear();
        _propSlots.Clear();
        _overlaySlots.Clear();
        _worldObjectSlots.Clear();
    }

    // ── lights ───────────────────────────────────────────────────────────

    /// <summary>The live light, plus the document that rebuilds it once the
    /// live one is gone.</summary>
    private sealed class LightSlot
    {
        public ILight? Live;
        public LightFile Document = new();

        /// <summary>False until a removal has actually read the light. A
        /// restore without one would spawn a default-valued impostor wearing
        /// the entry's name, so it fails instead.</summary>
        public bool HasDocument;
    }

    public ILight? SpawnLight(LightKind kind) =>
        RecordLightSpawn(
            $"Add {KindName(kind)} light", () => _lighting.SpawnLight(kind));

    public ILight? CloneLight(ILight source) =>
        RecordLightSpawn(
            $"Clone light '{source.Name}'", () => _lighting.CloneLight(source));

    /// <summary>Records a light that some OTHER path already created — a
    /// file import, which owns its own spawn — under this seam's discipline.
    /// </summary>
    public ILight? RecordSpawnedLight(string description, ILight? light) =>
        AppendLightSpawn(description, light);

    public void DestroyLight(ILight light)
    {
        // A borrowed light is released, not destroyed; there is no spawn that
        // inverts a release, so the act stands unrecorded rather than
        // pretending to be undoable.
        if (!_lighting.IsSpawnedLight(light))
        {
            _lighting.DestroyLight(light);
            return;
        }
        string description = $"Remove light '{light.Name}'";
        var slot = SlotFor(light);
        if (!RemoveLight(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RestoreLight(slot),
            () => RemoveLight(slot)));
    }

    private ILight? RecordLightSpawn(string description, Func<ILight?> spawn) =>
        AppendLightSpawn(description, spawn());

    private ILight? AppendLightSpawn(string description, ILight? light)
    {
        if (light == null)
            return null;
        var slot = SlotFor(light);
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RemoveLight(slot),
            () => RestoreLight(slot)));
        return light;
    }

    private LightSlot SlotFor(ILight light)
    {
        if (_lightSlots.TryGetValue(light, out var existing))
            return existing;
        var slot = new LightSlot { Live = light };
        _lightSlots[light] = slot;
        return slot;
    }

    private bool RemoveLight(LightSlot slot)
    {
        if (slot.Live is not { } light)
            return false;
        if (light.IsValid)
        {
            // Captured HERE, not at spawn: what redo must restore is the
            // light as the user last had it.
            slot.Document = LightFileService.CreateLightFile(light);
            slot.HasDocument = true;
            _lighting.DestroyLight(light);
        }
        _lightSlots.Remove(light);
        slot.Live = null;
        return true;
    }

    private bool RestoreLight(LightSlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var light = _lighting.SpawnLight(slot.Document.Kind);
        if (light == null)
            return false;
        LightFileService.Apply(slot.Document, light);
        ApplyGobo(slot.Document.Gobo, light);
        slot.Live = light;
        _lightSlots[light] = slot;
        return true;
    }

    /// <summary>Re-projects a saved gobo through the live library. A path the
    /// running client no longer ships is dropped rather than pushed at the
    /// game — the import path's own rule.</summary>
    private void ApplyGobo(string? path, ILight light)
    {
        if (string.IsNullOrEmpty(path))
            return;
        foreach (var gobo in _lighting.Gobos)
            if (string.Equals(
                    gobo.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _lighting.ApplyGobo(light, gobo);
                return;
            }
    }

    private static string KindName(LightKind kind) => kind switch
    {
        LightKind.Point => "point",
        LightKind.Area => "area",
        LightKind.Directional => "directional",
        _ => "spot",
    };

    // ── cameras ──────────────────────────────────────────────────────────

    private sealed class CameraSlot
    {
        public IVirtualCamera? Live;
        public CameraFile Document = new();
        public bool HasDocument;
    }

    public IVirtualCamera? CreateCamera(CameraKind kind) =>
        RecordCameraSpawn(
            kind == CameraKind.Free ? "Add free camera" : "Add camera",
            () => _cameras.CreateCamera(kind));

    public IVirtualCamera? CloneCamera(IVirtualCamera source) =>
        RecordCameraSpawn(
            $"Clone camera '{source.Name}'",
            () => _cameras.CloneCamera(source));

    /// <summary>Records a camera some other path already created — a file
    /// import — under this seam's discipline.</summary>
    public IVirtualCamera? RecordSpawnedCamera(
        string description, IVirtualCamera? camera) =>
        AppendCameraSpawn(description, camera);

    public void DestroyCamera(IVirtualCamera camera)
    {
        // The GPose session's own camera cannot be destroyed, so there is
        // nothing to record and nothing to invert.
        if (camera.IsDefault)
        {
            _cameras.DestroyCamera(camera);
            return;
        }
        string description = $"Remove camera '{camera.Name}'";
        var slot = SlotFor(camera);
        if (!RemoveCamera(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RestoreCamera(slot),
            () => RemoveCamera(slot)));
    }

    private IVirtualCamera? RecordCameraSpawn(
        string description, Func<IVirtualCamera?> create) =>
        AppendCameraSpawn(description, create());

    private IVirtualCamera? AppendCameraSpawn(
        string description, IVirtualCamera? camera)
    {
        if (camera == null)
            return null;
        var slot = SlotFor(camera);
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RemoveCamera(slot),
            () => RestoreCamera(slot)));
        return camera;
    }

    private CameraSlot SlotFor(IVirtualCamera camera)
    {
        if (_cameraSlots.TryGetValue(camera, out var existing))
            return existing;
        var slot = new CameraSlot { Live = camera };
        _cameraSlots[camera] = slot;
        return slot;
    }

    private bool RemoveCamera(CameraSlot slot)
    {
        if (slot.Live is not { } camera)
            return false;
        if (camera.IsValid)
        {
            slot.Document = CameraFileService.CreateCameraFile(camera);
            slot.HasDocument = true;
            _cameras.DestroyCamera(camera);
        }
        _cameraSlots.Remove(camera);
        slot.Live = null;
        return true;
    }

    private bool RestoreCamera(CameraSlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var camera = _cameras.CreateCamera(slot.Document.Kind);
        if (camera == null)
            return false;
        CameraFileService.Apply(slot.Document, camera);
        slot.Live = camera;
        _cameraSlots[camera] = slot;
        return true;
    }

    // ── actors ───────────────────────────────────────────────────────────

    /// <summary>
    /// The live actor, the call that made it, and what the user had made of
    /// it when it left.
    ///
    /// <para>An actor's APPEARANCE is still not a document this seam can
    /// capture — restoring one is the scene pipeline's asynchronous redraw,
    /// not a synchronous receipt — so an actor is always brought back by
    /// re-running the very call that produced it. That call is now worth far
    /// more than it was: a clone carries the source's Penumbra collection
    /// (<c>ISpawnCollectionPort</c>), so re-running it reproduces the modded
    /// appearance and not a bare body. <see cref="Document"/> then puts back
    /// everything the spawn does not decide — placement, visibility, and the
    /// pose the user authored — captured at the MOMENT OF REMOVAL, exactly as
    /// a light's and a prop's are.</para>
    ///
    /// <para>So a DESPAWN takes an entry now, where it did not before, but
    /// only for an actor whose spawn this seam RECORDED. Without
    /// <see cref="HasRespawn"/> there is no call to run again and no
    /// appearance to reproduce, and an entry restoring a blank stand-in would
    /// still be a worse answer than admitting there is none: those despawns
    /// are refused by name through <see cref="IActorLifecycle.Note"/>.</para>
    /// </summary>
    private sealed class ActorSlot
    {
        public IActor? Live;
        public Func<IActor?> Respawn = static () => null;
        public bool HasRespawn;
        public ActorState Document;
        public bool HasDocument;
    }

    /// <summary>Records one actor spawn. <paramref name="spawn"/> must be
    /// re-runnable: it is the redo.</summary>
    public IActor? SpawnActor(string description, Func<IActor?> spawn)
    {
        var actor = spawn();
        if (actor == null)
            return null;
        var slot = SlotFor(actor);
        slot.Respawn = spawn;
        slot.HasRespawn = true;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RemoveActor(slot),
            () => RestoreActor(slot)));
        return actor;
    }

    /// <summary>
    /// A duplicate WITH the source's pose and placement: the source is read
    /// the way a despawn reads it (placement, visibility, whole-skeleton
    /// pose), the copy is spawned, and that state is restored onto it once
    /// its body is posable — the same waiting restore a respawn gets. The
    /// live animation is not carried: duplication is a pose snapshot, by
    /// decision (2026-09-02); the caller freezes the copy. Redo replays the
    /// snapshot, so the copy comes back posed, not idling.
    /// </summary>
    /// <summary>See <see cref="IActorLifecycle.WhenPosable"/>.</summary>
    public void WhenPosable(IActor actor, Action<IActor> act) =>
        _actors.WhenPosable(actor, a => act((IActor)a));

    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source)
    {
        var state = _actors.Read(source);
        IActor? Posed()
        {
            var copy = spawn();
            if (copy != null)
                _actors.Restore(copy, state);
            return copy;
        }
        var actor = Posed();
        if (actor == null)
            return null;
        var slot = SlotFor(actor);
        slot.Respawn = Posed;
        slot.HasRespawn = true;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RemoveActor(slot),
            () => RestoreActor(slot)));
        return actor;
    }

    /// <summary>
    /// Despawns one actor, as a step of the user's history when it can be
    /// one. Spawning an actor was undoable and despawning it was not, which
    /// made the pair asymmetric in the direction that costs the user work.
    ///
    /// <para>The entry exists exactly when this seam recorded the spawn: only
    /// then is there a call to run again, and only then does re-running it
    /// reproduce the appearance and the collection. Every other despawn — an
    /// actor cloned straight through the world tab, one adopted from the
    /// overlay, one spawned before the history was last cleared — is destroyed
    /// all the same and NAMED as unundoable, never silently skipped.</para>
    /// </summary>
    public bool DespawnActor(IActor actor)
    {
        if (!_actorSlots.TryGetValue(actor, out var slot) || !slot.HasRespawn)
        {
            _actors.Note(
                $"Despawning '{actor.Name}' cannot be undone: Poser has no record of spawning this actor, so it has no call to run again and no way to reproduce the appearance it is wearing.");
            return _actors.Destroy(actor);
        }
        string description = $"Despawn actor '{actor.Name}'";
        if (!RemoveActor(slot))
            return false;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RestoreActor(slot),
            () => RemoveActor(slot)));
        return true;
    }

    private ActorSlot SlotFor(IActor actor)
    {
        if (_actorSlots.TryGetValue(actor, out var existing))
            return existing;
        var slot = new ActorSlot { Live = actor };
        _actorSlots[actor] = slot;
        return slot;
    }

    private bool RemoveActor(ActorSlot slot)
    {
        if (slot.Live is not { } actor)
            return false;
        // Despawned by the actor menu already: the removal this undo names
        // has happened, so it reports the truth rather than failing on a
        // corpse and pinning every older entry behind it.
        if (_actors.IsSpawned(actor))
        {
            // Captured HERE, not at spawn: the actor comes back where the
            // user left it, in the pose they gave it — the same rule the
            // light and the prop follow.
            slot.Document = _actors.Read(actor);
            slot.HasDocument = true;
            if (!_actors.Destroy(actor))
                return false;
        }
        _actorSlots.Remove(actor);
        slot.Live = null;
        return true;
    }

    private bool RestoreActor(ActorSlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasRespawn)
            return false;
        // A clone's source may itself be gone by now; the service answers
        // null and the entry stays redoable rather than half-applied.
        var actor = slot.Respawn();
        if (actor == null)
            return false;
        slot.Live = actor;
        _actorSlots[actor] = slot;
        // The body is back; the placement and the pose land on it over the
        // next few ticks, because the skeleton they need is built with a draw
        // object the spawn deliberately defers. The entry has landed either
        // way: the actor IS restored, and a pose that cannot follow says so
        // rather than leaving the step un-consumed and unrepeatable.
        if (slot.HasDocument)
            _actors.Restore(actor, slot.Document);
        return true;
    }

    // ── props ────────────────────────────────────────────────────────────

    /// <summary>The live prop token, plus the state that rebuilds it once the
    /// live one is gone. A prop's whole identity is its model triple, so —
    /// unlike an actor — a removal IS invertible and takes an entry.</summary>
    private sealed class PropSlot
    {
        public object? Live;
        public PropState Document;
        public bool HasDocument;
    }

    /// <summary>Brio's default prop, for the row that names no model.</summary>
    public object? SpawnProp() =>
        SpawnProp(new PropModel("Object", 9001, 249, 1, string.Empty));

    public object? SpawnProp(PropModel model)
    {
        var prop = _props.Spawn(model);
        if (prop == null)
            return null;
        var slot = SlotFor(prop);
        _history.Append(new SceneLifecyclePatch(
            $"Add object '{_props.Read(prop).Name}'",
            () => RemoveProp(slot),
            () => RestoreProp(slot)));
        return prop;
    }

    /// <summary>
    /// A prop's clone is its model triple spawned again, standing where the
    /// source stands and showing what the source shows. The NAME is the one
    /// thing it does not take: a spawn names itself, exactly as a cloned
    /// light takes a freshly generated name rather than the source's
    /// (LightingService.SpawnInternal).
    /// </summary>
    public object? CloneProp(object source)
    {
        var state = _props.Read(source);
        var prop = _props.Spawn(state.Model);
        if (prop == null)
            return null;
        _props.Apply(prop, state with { Name = _props.Read(prop).Name });
        var slot = SlotFor(prop);
        _history.Append(new SceneLifecyclePatch(
            $"Clone object '{state.Name}'",
            () => RemoveProp(slot),
            () => RestoreProp(slot)));
        return prop;
    }

    public void DestroyProp(object prop)
    {
        string description = $"Remove object '{_props.Read(prop).Name}'";
        var slot = SlotFor(prop);
        if (!RemoveProp(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RestoreProp(slot),
            () => RemoveProp(slot)));
    }

    /// <summary>
    /// Clearing the prop list is ONE act of the user's, so it is ONE entry
    /// over every slot it took. Each direction reports the truth for the whole
    /// set: a partial restore answers false and leaves the entry where it was,
    /// exactly as a refused single spawn does, so the step is retried rather
    /// than consumed.
    /// </summary>
    public void DestroyAllProps()
    {
        var props = _props.Props;
        if (props.Count == 0)
            return;
        var slots = new List<PropSlot>(props.Count);
        foreach (var prop in props)
            slots.Add(SlotFor(prop));
        if (!RemoveProps(slots))
            return;
        _history.Append(new SceneLifecyclePatch(
            props.Count == 1 ? "Remove object" : $"Remove {props.Count} objects",
            () => RestoreProps(slots),
            () => RemoveProps(slots)));
    }

    private PropSlot SlotFor(object prop)
    {
        if (_propSlots.TryGetValue(prop, out var existing))
            return existing;
        var slot = new PropSlot { Live = prop };
        _propSlots[prop] = slot;
        return slot;
    }

    private bool RemoveProp(PropSlot slot)
    {
        if (slot.Live is not { } prop)
            return false;
        if (_props.IsLive(prop))
        {
            // Captured HERE, not at spawn: a prop the user moved comes back
            // where they left it.
            slot.Document = _props.Read(prop);
            slot.HasDocument = true;
            _props.Destroy(prop);
        }
        _propSlots.Remove(prop);
        slot.Live = null;
        return true;
    }

    private bool RestoreProp(PropSlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var prop = _props.Spawn(slot.Document.Model);
        if (prop == null)
            return false;
        _props.Apply(prop, slot.Document);
        slot.Live = prop;
        _propSlots[prop] = slot;
        return true;
    }

    private bool RemoveProps(IReadOnlyList<PropSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RemoveProp(slot);
        return landed;
    }

    private bool RestoreProps(IReadOnlyList<PropSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RestoreProp(slot);
        return landed;
    }

    // ── overlay nodes ──────────────────────────────────────

    /// <summary>The live node token, plus the document that rebuilds it once
    /// the live one is gone. An overlay node's whole identity IS that document
    /// — there is no second, native half of it — so a removal inverts as
    /// cleanly as an addition and takes an entry.</summary>
    private sealed class OverlaySlot
    {
        public object? Live;
        public OverlayNodeState Document = new();
        public bool HasDocument;
    }

    public object? SpawnOverlay(OverlayNodeKind kind) =>
        SpawnOverlay(OverlayNodeService.DefaultState(kind));

    /// <summary>Records one overlay node the user added, from a complete
    /// document: a fresh create, a duplicate of the selected node, or a
    /// restored one.</summary>
    public object? SpawnOverlay(OverlayNodeState state)
    {
        var overlay = _overlayNodes.Create(state);
        if (overlay == null)
            return null;
        var slot = OverlaySlotFor(overlay);
        _history.Append(new SceneLifecyclePatch(
            $"Add {KindName(state.Kind)} '{_overlayNodes.Read(overlay).Name}'",
            () => RemoveOverlay(slot),
            () => RestoreOverlay(slot)));
        return overlay;
    }

    public void DestroyOverlay(object overlay)
    {
        var document = _overlayNodes.Read(overlay);
        string description =
            $"Remove {KindName(document.Kind)} '{document.Name}'";
        var slot = OverlaySlotFor(overlay);
        if (!RemoveOverlay(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RestoreOverlay(slot),
            () => RemoveOverlay(slot)));
    }

    /// <summary>Clearing the overlay list is ONE act of the user's, so it is
    /// ONE entry over every slot it took — the prop list's own rule.</summary>
    public void DestroyAllOverlays()
    {
        var overlays = _overlayNodes.Overlays;
        if (overlays.Count == 0)
            return;
        var slots = new List<OverlaySlot>(overlays.Count);
        foreach (var overlay in overlays)
            slots.Add(OverlaySlotFor(overlay));
        if (!RemoveOverlays(slots))
            return;
        _history.Append(new SceneLifecyclePatch(
            overlays.Count == 1
                ? "Remove overlay"
                : $"Remove {overlays.Count} overlays",
            () => RestoreOverlays(slots),
            () => RemoveOverlays(slots)));
    }

    private OverlaySlot OverlaySlotFor(object overlay)
    {
        if (_overlaySlots.TryGetValue(overlay, out var existing))
            return existing;
        var slot = new OverlaySlot { Live = overlay };
        _overlaySlots[overlay] = slot;
        return slot;
    }

    private bool RemoveOverlay(OverlaySlot slot)
    {
        if (slot.Live is not { } overlay)
            return false;
        if (_overlayNodes.IsLive(overlay))
        {
            // Captured HERE, not at creation: a node the user rewrote comes
            // back saying what they last made it say.
            slot.Document = _overlayNodes.Read(overlay);
            slot.HasDocument = true;
            _overlayNodes.Destroy(overlay);
        }
        _overlaySlots.Remove(overlay);
        slot.Live = null;
        return true;
    }

    private bool RestoreOverlay(OverlaySlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasDocument)
            return false;
        var overlay = _overlayNodes.Create(slot.Document);
        if (overlay == null)
            return false;
        slot.Live = overlay;
        _overlaySlots[overlay] = slot;
        return true;
    }

    private bool RemoveOverlays(IReadOnlyList<OverlaySlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RemoveOverlay(slot);
        return landed;
    }

    private bool RestoreOverlays(IReadOnlyList<OverlaySlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RestoreOverlay(slot);
        return landed;
    }

    private static string KindName(OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Balloon => "balloon",
        OverlayNodeKind.Status => "status",
        _ => "dialog",
    };

    // ── adopted world objects ────────────────────────────────────────────

    /// <summary>The live claim, plus the state that takes it again once the
    /// live one is gone. A claim's whole identity is the ADDRESS it was taken
    /// at, because the object behind it belongs to the map and outlives every
    /// claim on it — so both directions are exactly statable and an adoption
    /// takes an entry.</summary>
    private sealed class WorldObjectSlot
    {
        public object? Live;
        public WorldObjectState Document;
        public bool HasDocument;
    }

    /// <summary>Takes one BG object into the scene, journalled. Undoing it
    /// RELEASES the claim, which puts the object back exactly where the map
    /// stood it — an adoption's inverse is never a destroy.</summary>
    public object? AdoptWorldObject(nint address)
    {
        var worldObject = _worldObjects.Adopt(address);
        if (worldObject == null)
            return null;
        var slot = WorldObjectSlotFor(worldObject);
        _history.Append(new SceneLifecyclePatch(
            "Add world object",
            () => ReleaseWorldObjectSlot(slot),
            () => RestoreWorldObject(slot)));
        return worldObject;
    }

    /// <summary>Spawns one object from a model path, journalled like an
    /// adoption: undoing it takes the copy out of the scene again.</summary>
    public object? SpawnWorldObject(string path, Transform placement, bool visible)
    {
        var worldObject = _worldObjects.Spawn(path, placement, visible);
        if (worldObject == null)
            return null;
        var slot = WorldObjectSlotFor(worldObject);
        _history.Append(new SceneLifecyclePatch(
            "Add world object",
            () => ReleaseWorldObjectSlot(slot),
            () => RestoreWorldObject(slot)));
        return worldObject;
    }

    /// <summary>Gives one adopted object back to the map, journalled. Undoing
    /// it re-adopts the same address and puts back the placement the user had
    /// given it.</summary>
    public void ReleaseWorldObject(object worldObject)
    {
        var slot = WorldObjectSlotFor(worldObject);
        if (!ReleaseWorldObjectSlot(slot))
            return;
        _history.Append(new SceneLifecyclePatch(
            "Remove world object",
            () => RestoreWorldObject(slot),
            () => ReleaseWorldObjectSlot(slot)));
    }

    /// <summary>Giving the whole list back is ONE act of the user's, so it is
    /// ONE entry over every slot it took — the prop list's own rule.</summary>
    public void ReleaseAllWorldObjects()
    {
        var worldObjects = _worldObjects.WorldObjects;
        if (worldObjects.Count == 0)
            return;
        var slots = new List<WorldObjectSlot>(worldObjects.Count);
        foreach (var worldObject in worldObjects)
            slots.Add(WorldObjectSlotFor(worldObject));
        if (!ReleaseWorldObjectSlots(slots))
            return;
        _history.Append(new SceneLifecyclePatch(
            worldObjects.Count == 1
                ? "Remove world object"
                : $"Remove {worldObjects.Count} world objects",
            () => RestoreWorldObjects(slots),
            () => ReleaseWorldObjectSlots(slots)));
    }

    private WorldObjectSlot WorldObjectSlotFor(object worldObject)
    {
        if (_worldObjectSlots.TryGetValue(worldObject, out var existing))
            return existing;
        var slot = new WorldObjectSlot { Live = worldObject };
        _worldObjectSlots[worldObject] = slot;
        return slot;
    }

    private bool ReleaseWorldObjectSlot(WorldObjectSlot slot)
    {
        if (slot.Live is not { } worldObject)
            return false;
        if (_worldObjects.IsLive(worldObject))
        {
            // Captured HERE, not at adoption: an object the user moved comes
            // back where they left it. What the RELEASE writes to the game is
            // the map's own placement — the service captured that at adoption
            // and this document never touches it.
            slot.Document = _worldObjects.Read(worldObject);
            slot.HasDocument = true;
        }
        _worldObjects.Release(worldObject);
        _worldObjectSlots.Remove(worldObject);
        slot.Live = null;
        return true;
    }

    private bool RestoreWorldObject(WorldObjectSlot slot)
    {
        if (slot.Live != null)
            return true;
        if (!slot.HasDocument)
            return false;
        // A spawned entry was destroyed — its undo re-creates the path.
        // A borrowed one re-claims its address, but only after the world
        // walk confirms the object still stands: dereferencing a
        // streamed-out address is the crash, not a refusal.
        var worldObject = slot.Document.Spawned
            ? _worldObjects.Spawn(
                slot.Document.Path,
                slot.Document.Placement,
                slot.Document.Visible)
            : _worldObjects.AddressLive(slot.Document.Address)
                ? _worldObjects.Adopt(slot.Document.Address)
                : null;
        if (worldObject == null)
            return false;
        _worldObjects.Apply(worldObject, slot.Document);
        slot.Live = worldObject;
        _worldObjectSlots[worldObject] = slot;
        return true;
    }

    private bool ReleaseWorldObjectSlots(IReadOnlyList<WorldObjectSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= ReleaseWorldObjectSlot(slot);
        return landed;
    }

    private bool RestoreWorldObjects(IReadOnlyList<WorldObjectSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RestoreWorldObject(slot);
        return landed;
    }

    // ── group removal ────────────────────────────────────────────────────

    /// <summary>
    /// Removing a SELECTION is one act of the user's, so it is ONE entry over
    /// every slot it took — the prop list's own rule, widened from "every prop"
    /// to "everything selected". Each direction reports the truth for the whole
    /// set: a partial restore answers false and leaves the entry where it was,
    /// so the step is retried rather than consumed.
    ///
    /// <para>ACTORS route through <see cref="DespawnActor"/>, which journals
    /// the removal when this seam recorded the spawn and names the unundoable
    /// classes otherwise — the group op inherits exactly the single-actor
    /// rule. A selection is homogeneous by construction, so in practice an
    /// entry is over one kind; the signature takes them all because the seam
    /// must not depend on that staying true.</para>
    ///
    /// <para>Returns how many entities it removed, so a caller can say what
    /// happened without counting live handles that no longer exist.</para>
    /// </summary>
    public int DestroySelection(
        IReadOnlyList<IActor>? actors = null,
        IReadOnlyList<object>? props = null,
        IReadOnlyList<ILight>? lights = null,
        IReadOnlyList<IVirtualCamera>? cameras = null,
        IReadOnlyList<object>? overlays = null)
    {
        int removed = 0;

        // Actors route through the journaled despawn: an entry exists exactly
        // when this seam recorded the spawn, and DespawnActor already names
        // the unundoable classes instead of skipping them.
        foreach (var actor in actors ?? Array.Empty<IActor>())
        {
            if (!_actors.IsSpawned(actor))
                continue;
            DespawnActor(actor);
            removed++;
        }

        var propSlots = new List<PropSlot>();
        foreach (var prop in props ?? Array.Empty<object>())
            propSlots.Add(SlotFor(prop));
        var lightSlots = new List<LightSlot>();
        foreach (var light in lights ?? Array.Empty<ILight>())
        {
            // A borrowed light is released, not destroyed, and a release has no
            // spawn that inverts it — the single-light rule, applied per member.
            if (!_lighting.IsSpawnedLight(light))
            {
                _lighting.DestroyLight(light);
                removed++;
                continue;
            }
            lightSlots.Add(SlotFor(light));
        }
        var cameraSlots = new List<CameraSlot>();
        foreach (var camera in cameras ?? Array.Empty<IVirtualCamera>())
        {
            // The session's own camera cannot be destroyed at all.
            if (camera.IsDefault)
                continue;
            cameraSlots.Add(SlotFor(camera));
        }
        var overlaySlots = new List<OverlaySlot>();
        foreach (var overlay in overlays ?? Array.Empty<object>())
            overlaySlots.Add(OverlaySlotFor(overlay));

        int journaled = propSlots.Count + lightSlots.Count +
            cameraSlots.Count + overlaySlots.Count;
        if (journaled == 0)
            return removed;

        bool Remove() =>
            RemoveProps(propSlots) &
            RemoveLights(lightSlots) &
            RemoveCameras(cameraSlots) &
            RemoveOverlays(overlaySlots);

        bool Restore() =>
            RestoreProps(propSlots) &
            RestoreLights(lightSlots) &
            RestoreCameras(cameraSlots) &
            RestoreOverlays(overlaySlots);

        if (!Remove())
            return removed;
        removed += journaled;
        _history.Append(new SceneLifecyclePatch(
            journaled == 1 ? "Remove 1 entity" : $"Remove {journaled} entities",
            Restore,
            Remove));
        return removed;
    }

    private bool RemoveLights(IReadOnlyList<LightSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RemoveLight(slot);
        return landed;
    }

    private bool RestoreLights(IReadOnlyList<LightSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RestoreLight(slot);
        return landed;
    }

    private bool RemoveCameras(IReadOnlyList<CameraSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RemoveCamera(slot);
        return landed;
    }

    private bool RestoreCameras(IReadOnlyList<CameraSlot> slots)
    {
        bool landed = true;
        foreach (var slot in slots)
            landed &= RestoreCamera(slot);
        return landed;
    }
}
