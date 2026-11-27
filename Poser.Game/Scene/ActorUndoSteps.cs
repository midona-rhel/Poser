using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Application.Posing;
using Poser.Game.Entities;

namespace Poser.Game.Scene;

/// <summary>The actor's pose, placement and settings when it was removed, so undo can bring it back without the actor it was first copied from.</summary>
internal readonly record struct ActorState(
    Transform Placement,
    bool Visible,
    PoseFile? Pose)
{
    public ActorRuntimeState? Runtime { get; init; }
    public IReadOnlyList<PoseImportWrite>? CopyBones { get; init; }
    public bool FreezePoseOnRestore { get; init; }
    /// <summary>Whether the actor comes back paused: a duplicate is frozen on
    /// its copied pose, and an actor that was paused when it was read stays
    /// paused. A running actor comes back running.</summary>
    public bool RestoresPaused => FreezePoseOnRestore || Runtime?.Paused == true;
    /// <summary>The scale of the root bone of each model part (body, face, hair
    /// and so on), keyed by "partial:bone". A pose file cannot tell apart root
    /// bones that share a name, so these scales are set before the pose import.</summary>
    public IReadOnlyDictionary<string, System.Numerics.Vector3>? PartialRootScales { get; init; }

    /// <summary>Physics-bone positions and scales relative to their parents,
    /// keyed by "partial:bone". Rotation is left to the simulation.</summary>
    public IReadOnlyDictionary<string, (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale)>? PhysicsDeltas { get; init; }
}

/// <summary>
/// What undo and redo use to read, remove and recreate actors. Restoring an
/// actor waits until the game has built its body and skeleton; callers do not
/// wait for that.
/// </summary>
internal interface IActorRemoveRestore
{
    string GetName(object actor);
    void SetName(object actor, string name);
    void NameCreated(object actor, string seed);

    bool IsSpawned(object actor);

    bool Destroy(object actor);

    ActorState Read(object actor);

    /// <summary>Reads <paramref name="actor"/>'s pose for a copy and hands
    /// it to <paramref name="done"/>, or null when it could not be read. In the
    /// game it answers a frame or two later, once the source actor's bone
    /// positions have been read fresh for the copy's pose import.</summary>
    void CapturePoseForCopy(object actor, Action<ActorState?> done);

    /// <summary>Reads <paramref name="source"/>'s Customize+ profile so it can be
    /// put on a copy, or null when the source has none.</summary>
    BodyProfileCopy? CaptureBodyProfile(IActor source);

    IActor? Recreate(ActorState state);

    /// <summary>Runs <paramref name="act"/> once the actor's body can be
    /// posed, with the same wait a restore gets, or reports that it never
    /// could be.</summary>
    void WhenPosable(object actor, Action<object> act);

    /// <summary>Puts the saved state back onto a newly spawned actor once it is
    /// ready. If the pose or placement fails later, that is reported through
    /// <see cref="Note"/>; the undo step that brought the body back still counts
    /// as done. Until the state is back, <see cref="IsRestoring"/> is true.</summary>
    void Restore(object actor, ActorState state, Func<bool>? stillCurrent = null);

    /// <summary>True while a restore started by <see cref="Restore"/>, or held
    /// with <see cref="HoldRestore"/>, has not finished. The action gate reads
    /// it, so another undo cannot run while the restore would still write over
    /// it.</summary>
    bool IsRestoring { get; }

    /// <summary>Counts as a restore in progress until the returned action is
    /// called; calling it again does nothing. For the wait before
    /// <see cref="Restore"/> starts, such as a copied body profile.</summary>
    Action HoldRestore();

    /// <summary>Forgets every restore in progress. Called when the undo history
    /// is cleared, such as on leaving GPose.</summary>
    void ForgetRestores();

    /// <summary>Where problems are reported. An action that cannot be undone
    /// says so here, instead of passing for one that can.</summary>
    void Note(string detail);
}

/// <summary>
/// Keeps the snapshot taken the last time the actor was removed. In the game, undo
/// builds a new body from that snapshot; the first spawn is only run again when there is none.
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
    public BodyProfileCopy? ApplyBodyProfile;
}

/// <summary>Puts a copied Customize+ profile on <paramref name="target"/> once
/// its body can be posed. <paramref name="applied"/> runs when the profile is
/// on, and also when putting it on failed, which is reported as a note.
/// <paramref name="gaveUp"/> runs instead when the actor is gone or never
/// becomes posable.</summary>
internal delegate void BodyProfileCopy(IActor target, Action applied, Action gaveUp);

/// <summary>Records actor spawns and removals as undo steps. Bringing an actor
/// back builds the body from the snapshot taken at removal, or runs the first
/// spawn again when there is none, and then puts its saved state back.</summary>
internal sealed class ActorUndoSteps
{
    private readonly EditHistory _history;
    private readonly IActorRemoveRestore _actors;
    private readonly LifecycleSlotOwner<IActor, ActorLifecycleSlot> _slots;

    /// <param name="actorTarget">The actor's move target, so the undo history
    /// follows a removed actor to the actor undo brings back, as it does for
    /// lights, props, collider overlays and world objects.</param>
    public ActorUndoSteps(EditHistory history, IActorRemoveRestore actors,
        Func<IActor, TransformTargetId?>? actorTarget)
    {
        _history = history;
        _actors = actors;
        _slots = new(
            actor => new ActorLifecycleSlot { Live = actor },
            slot => slot.Live, (slot, live) => slot.Live = live,
            RemoveActor, RestoreActor, retainAliases: true,
            history: history, transformTarget: actorTarget);
    }

    public IActorRemoveRestore Port => _actors;

    public void Clear()
    {
        _slots.Clear();
        _actors.ForgetRestores();
    }

    public IActor? Resolve(IActor actor) => _slots.Resolve(actor);

    public void BindReplacement(IActor original, IActor replacement) =>
        _slots.BindReplacement(original, replacement);

    /// <summary>Records one actor spawn. <paramref name="spawn"/> must work
    /// when run again, because redo runs it.</summary>
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
        bodyProfile?.Invoke(actor, static () => { }, static () => { });
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
        return actor;
    }

    /// <summary>See <see cref="IActorRemoveRestore.WhenPosable"/>.</summary>
    public void WhenPosable(IActor actor, Action<IActor> act) =>
        _actors.WhenPosable(actor, a => act((IActor)a));

    /// <summary>
    /// Creates a duplicate. The source actor's pose is read once its bone positions
    /// are fresh, and applied once the copy's Customize+ profile is in place. The
    /// first time, the copy is frozen. Later undo and redo use the state read when
    /// the copy was removed.
    /// </summary>
    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source)
    {
        var name = _actors.GetName(source);
        var bodyProfile = _actors.CaptureBodyProfile(source);
        // The source pose is read once, a frame or two after the click.
        // Redo uses the snapshot taken when the copy was removed.
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
        // Apply the pose after the Customize+ profile has changed the skeleton.
        bodyProfile?.Invoke(actor, () => { profileReady = true; RestoreWhenReady(); }, static () => { });
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

    /// <summary>Removes an actor Poser spawned and saves its state for undo, in
    /// the same way whether it came from a scene file or its spawn is still in
    /// the undo history. <paramref name="note"/> says what undo cannot bring
    /// back.</summary>
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
        // If something else already removed the actor, there is no body in the
        // game left to read or destroy.
        if (_actors.IsSpawned(actor))
        {
            // Save the actor as it is now, not as it was when spawned.
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
        // A snapshot taken at removal does not need the actor this one was copied from.
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
        {
            // The copied body profile goes on first. Hold the gate across that
            // wait too, so no undo runs before the restore has even started.
            // If the body never becomes posable, nothing is restored.
            var release = _actors.HoldRestore();
            applyBodyProfile(actor, () =>
            {
                try { RestoreState(); }
                finally { release(); }
            }, release);
        }
        else
            RestoreState();
        // The undo step counts as done once the body is back. Pose and placement
        // may finish on later frames; their failures are reported through Note.
        // Until they have, IsRestoring holds the action gate, so a second undo
        // waits instead of being written over.
        return true;
    }
}
