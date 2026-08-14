using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

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
    private readonly IActorSpawnService _actors;

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

    public SceneLifecycleHistory(
        TransformHistory history,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IActorSpawnService actors)
    {
        _history = history;
        _lighting = lighting;
        _cameras = cameras;
        _actors = actors;
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
    /// The live actor plus the call that made it. An actor's APPEARANCE is
    /// not a document this seam can capture — restoring one is the scene
    /// pipeline's asynchronous redraw, not a synchronous receipt — so a
    /// spawn is inverted by re-running the very call that produced it. That
    /// is exact for the spawn it undoes: the actor comes back exactly as it
    /// was born, which is what redoing a spawn means. It is also why a
    /// DESPAWN takes no entry: resurrecting an arbitrary actor — one this
    /// session may never have spawned, wearing edits no document here holds —
    /// is a different problem, and an entry that restored a blank stand-in
    /// would be a worse answer than admitting there is none.
    /// </summary>
    private sealed class ActorSlot
    {
        public IActor? Live;
        public Func<IActor?> Respawn = static () => null;
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
        _history.Append(new SceneLifecyclePatch(
            description,
            () => RemoveActor(slot),
            () => RestoreActor(slot)));
        return actor;
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
        if (_actors.IsSpawnedActor(actor) && !_actors.DestroyActor(actor))
            return false;
        _actorSlots.Remove(actor);
        slot.Live = null;
        return true;
    }

    private bool RestoreActor(ActorSlot slot)
    {
        if (slot.Live != null)
            return true;
        // A clone's source may itself be gone by now; the service answers
        // null and the entry stays redoable rather than half-applied.
        var actor = slot.Respawn();
        if (actor == null)
            return false;
        slot.Live = actor;
        _actorSlots[actor] = slot;
        return true;
    }
}
