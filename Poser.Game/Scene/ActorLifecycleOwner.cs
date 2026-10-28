using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Application.Posing;
using Poser.Game.Entities;

namespace Poser.Game.Scene;

/// <summary>The actor's authored values at removal, independent of its original spawn source.</summary>
internal readonly record struct ActorState(
    Transform Placement,
    bool Visible,
    PoseFile? Pose)
{
    public ActorRuntimeState? Runtime { get; init; }
    public IReadOnlyList<PoseImportWrite>? CopyBones { get; init; }
    public bool FreezePoseOnRestore { get; init; }
    /// <summary>Whether the restored actor comes back paused: a posed
    /// duplicate's frozen basis, or an actor that was paused when it was read
    /// (#483). A running actor comes back running.</summary>
    public bool RestoresPaused => FreezePoseOnRestore || Runtime?.Paused == true;
    /// <summary>Partial-root scales keyed by "partial:bone". Pose files cannot
    /// distinguish roots that share a name; these scales are set before import.</summary>
    public IReadOnlyDictionary<string, System.Numerics.Vector3>? PartialRootScales { get; init; }

    /// <summary>Physics-bone positions and scales relative to their parents,
    /// keyed by "partial:bone". Rotation is left to the simulation.</summary>
    public IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)>? PhysicsDeltas { get; init; }
}

/// <summary>
/// Operations used to capture, remove, and recreate actors for history.
/// The implementation schedules restoration after the native body and skeleton
/// become ready; callers do not wait synchronously for that work.
/// </summary>
internal interface IActorLifecycle
{
    string GetName(object actor);
    void SetName(object actor, string name);
    void NameCreated(object actor, string seed);

    bool IsSpawned(object actor);

    bool Destroy(object actor);

    ActorState Read(object actor);

    /// <summary>Reads <paramref name="actor"/>'s pose for a copy and hands
    /// it to <paramref name="done"/>, or null when it could not be read. The
    /// live lifecycle answers on a later tick, once the source's caches are in
    /// the frame the copy's import writes in.</summary>
    void CapturePoseForCopy(object actor, Action<ActorState?> done) =>
        done(Read(actor) with { Runtime = null });

    Action<IActor, Action>? CaptureBodyProfile(IActor source) => null;

    IActor? Recreate(ActorState state) => null;

    /// <summary>Runs <paramref name="act"/> once the actor's body is
    /// posable — the same wait a restore gets — or reports that it never
    /// became so.</summary>
    void WhenPosable(object actor, Action<object> act);

    /// <summary>Schedules restoration onto a newly spawned actor. Later pose or
    /// placement failures are reported through Note and do not fail the history
    /// entry that already recreated the body.</summary>
    void Restore(object actor, ActorState state, Func<bool>? stillCurrent = null);

    /// <summary>The seam's refusal channel. An act it cannot journal says so
    /// here rather than passing for one it can.</summary>
    void Note(string detail);
}

/// <summary>
/// Retains the latest removal snapshot. Production recreates a fresh body
/// from that state; the initial factory is only the fallback without a runtime snapshot.
/// </summary>
internal sealed class ActorLifecycleSlot
{
    public string? Name;
    public IActor? Live;
    public Func<IActor?> Respawn = static () => null;
    public bool HasRespawn;
    public ActorState Document;
    public bool HasDocument;
    public bool PosedDuplicate;
    public Action<IActor, Action>? ApplyBodyProfile;
}

/// <summary>Records actor creation and removal. Restoration recreates the body
/// from its runtime snapshot, falling back to the original creation callback
/// when no runtime snapshot exists, then restores its saved state.</summary>
internal sealed class ActorLifecycleOwner
{
    private readonly EditHistory _history;
    private readonly IActorLifecycle _actors;
    private readonly LifecycleSlotOwner<IActor, ActorLifecycleSlot> _slots;

    public ActorLifecycleOwner(EditHistory history, IActorLifecycle actors)
    {
        _history = history;
        _actors = actors;
        _slots = new(
            actor => new ActorLifecycleSlot { Live = actor },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveActor, RestoreActor, retainAliases: true);
    }

    public IActorLifecycle Port => _actors;

    public void Clear() => _slots.Clear();

    public IActor? Resolve(IActor actor) => _slots.Resolve(actor);

    public void BindReplacement(IActor original, IActor replacement) =>
        _slots.BindReplacement(original, replacement);

    /// <summary>Records one actor spawn. <paramref name="spawn"/> must be
    /// re-runnable: it is the redo.</summary>
    public IActor? SpawnActor(string description, Func<IActor?> spawn, IActor? source = null, string? name = null)
    {
        var seed = name ?? (source == null ? "Actor" : _actors.GetName(source));
        var bodyProfile = source is null ? null : _actors.CaptureBodyProfile(source);
        var actor = spawn();
        if (actor == null)
            return null;
        if (_actors.IsSpawned(actor))
            _actors.NameCreated(actor, seed);
        var slot = _slots.SlotFor(actor);
        slot.Respawn = spawn;
        slot.HasRespawn = true;
        slot.ApplyBodyProfile = bodyProfile;
        bodyProfile?.Invoke(actor, static () => { });
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return actor;
    }

    /// <summary>See <see cref="IActorLifecycle.WhenPosable"/>.</summary>
    public void WhenPosable(IActor actor, Action<IActor> act) =>
        _actors.WhenPosable(actor, a => act((IActor)a));

    /// <summary>
    /// Creates a duplicate, captures the source pose after refreshing its caches,
    /// and restores that pose after the copy's body profile is ready. The first
    /// restore freezes the copy. Later undo/redo uses the state captured at removal.
    /// </summary>
    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source)
    {
        var name = _actors.GetName(source);
        var bodyProfile = _actors.CaptureBodyProfile(source);
        // Read once, a tick or two after the click (see CapturePoseForCopy).
        // Redo uses the removal snapshot captured by the lifecycle slot.
        IActor? Posed() => spawn();
        var actor = Posed();
        if (actor == null)
            return null;
        if (_actors.IsSpawned(actor))
            _actors.NameCreated(actor, name);
        var slot = _slots.SlotFor(actor);
        slot.PosedDuplicate = true;
        slot.ApplyBodyProfile = bodyProfile;
        slot.Respawn = Posed;
        slot.HasRespawn = true;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        ActorState? posed = null;
        bool profileReady = bodyProfile is null;
        void RestoreWhenReady()
        {
            if (!profileReady || posed is not { } captured)
                return;
            if (ReferenceEquals(_slots.CurrentInstance(slot), actor))
                _actors.Restore(actor, captured, () => ReferenceEquals(_slots.CurrentInstance(slot), actor));
        }
        // Profile shaping establishes the target skeleton's basis. Its
        // completion is an apply-frame barrier, not merely IPC acceptance.
        bodyProfile?.Invoke(actor, () => { profileReady = true; RestoreWhenReady(); });
        _actors.CapturePoseForCopy(source, captured =>
        {
            if (captured is null)
            {
                _actors.Note($"Duplicate: the pose of '{name}' could not be read, so the copy keeps its own pose.");
                return;
            }
            posed = captured.Value with { FreezePoseOnRestore = true };
            RestoreWhenReady();
        });
        return actor;
    }

    /// <summary>Removes an owned actor through the shared state capture,
    /// regardless of whether it came from a scene file or its creation entry
    /// still exists. <paramref name="note"/> tells the user what the recorded
    /// undo cannot bring back.</summary>
    public bool DespawnActor(IActor actor, out string? note)
    {
        note = null;
        if (!_slots.TryGetSlot(actor, out var slot) || !slot.HasRespawn)
        {
            if (!_actors.IsSpawned(actor))
            {
                _actors.Note($"Despawning '{actor.Name}' cannot be undone: this actor is not owned by Poser.");
                return _actors.Destroy(actor);
            }
            slot = _slots.SlotFor(actor);
            slot.Respawn = () => _actors.Recreate(slot.Document);
            slot.HasRespawn = true;
        }
        string description = $"Despawn actor '{actor.Name}'";
        if (!_slots.CaptureAndRemove(slot))
            return false;
        if (slot.Document.Runtime?.Properties.Appearance.LookOmitted == true)
            note = $"Glamourer could not read the appearance of '{slot.Name}', so undoing this removal brings it back without that appearance.";
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.Restore(slot),
            () => _slots.CaptureAndRemove(slot)));
        return true;
    }

    private bool RemoveActor(ActorLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) is not { } actor)
            return false;
        // If another path already removed the actor, there is no native body
        // left to capture or destroy.
        if (_actors.IsSpawned(actor))
        {
            // Capture the latest editable state at removal, rather than creation.
            slot.Document = _actors.Read(actor);
            if (slot.PosedDuplicate && slot.Document.Runtime?.Paused == true)
                slot.Document = slot.Document with { FreezePoseOnRestore = true };
            slot.Name = _actors.GetName(actor);
            slot.HasDocument = true;
            if (!_actors.Destroy(actor))
                return false;
        }
        return true;
    }

    private bool RestoreActor(ActorLifecycleSlot slot)
    {
        if (_slots.CurrentInstance(slot) != null)
            return true;
        if (!slot.HasRespawn)
            return false;
        // A removal snapshot is independent of the original clone source.
        var actor = slot.HasDocument && slot.Document.Runtime is not null
            ? _actors.Recreate(slot.Document)
            : slot.Respawn();
        if (actor == null)
            return false;
        slot.Live = actor;
        if (slot.Name is { } name)
            _actors.SetName(actor, name);
        void RestoreState()
        {
            if (slot.HasDocument)
                _actors.Restore(actor, slot.Document, () => ReferenceEquals(_slots.CurrentInstance(slot), actor));
        }
        if (slot.ApplyBodyProfile is { } applyBodyProfile)
            applyBodyProfile(actor, RestoreState);
        else
            RestoreState();
        // History succeeds once the body is recreated. Pose and placement may
        // finish on later ticks; failures are reported by the runtime port.
        return true;
    }
}
