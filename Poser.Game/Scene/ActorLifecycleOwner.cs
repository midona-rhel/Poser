using System;
using System.Collections.Generic;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Domain.Transforms;
using Poser.Documents.Files;

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
    string GetName(object actor);
    void SetName(object actor, string name);
    void NameCreated(object actor, string seed);

    bool IsSpawned(object actor);

    bool Destroy(object actor);

    ActorState Read(object actor);

    ActorState ReadPoseForCopy(object actor) => Read(actor) with { Runtime = null };

    void CopyBodyProfile(IActor source, IActor target) { }

    void DetachGaze(object actor);

    IActor? Recreate(ActorState state) => null;

    /// <summary>Runs <paramref name="act"/> once the actor's body is
    /// posable — the same wait a restore gets — or reports that it never
    /// became so.</summary>
    void WhenPosable(object actor, Action<object> act);

    /// <summary>Puts <paramref name="state"/> back onto a JUST-RESPAWNED
    /// actor. Placement and pose land once the body is posable, so this
    /// returns long before the actor looks right; the restore is complete or
    /// it says why, and never fails the entry that asked for it — the actor
    /// is back either way.</summary>
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
}

/// <summary>Owns actor lifecycle entries. An actor's document cannot rebuild
/// it, so a restore re-runs the call that made it and the document then puts
/// back placement, visibility and pose; a despawn takes an entry only where
/// this owner recorded the spawn or can recreate the actor.</summary>
internal sealed class ActorLifecycleOwner
{
    private readonly TransformHistory _history;
    private readonly IActorLifecycle _actors;
    private readonly LifecycleSlotOwner<IActor, ActorLifecycleSlot> _slots;

    public ActorLifecycleOwner(TransformHistory history, IActorLifecycle actors)
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
        var actor = spawn();
        if (actor == null)
            return null;
        if (_actors.IsSpawned(actor))
            _actors.NameCreated(actor, seed);
        var slot = _slots.SlotFor(actor);
        slot.Respawn = spawn;
        slot.HasRespawn = true;
        if (source is not null)
            _actors.CopyBodyProfile(source, actor);
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
    /// A duplicate WITH the source's pose and placement: the source is read
    /// the way a despawn reads it (placement, visibility, whole-skeleton
    /// pose), the copy is spawned, and that state is restored onto it once
    /// its body is posable — the same waiting restore a respawn gets. The
    /// live animation is not carried: duplication is a pose snapshot, by
    /// decision (2026-09-02); the caller freezes the copy. Redo replays the
    /// snapshot, so the copy comes back posed, not idling.
    /// </summary>
    public IActor? SpawnActorWithPose(
        string description, Func<IActor?> spawn, IActor source)
    {
        var name = _actors.GetName(source);
        var state = _actors.ReadPoseForCopy(source);
        IActor? Posed()
        {
            var copy = spawn();
            if (copy != null)
            {
                _actors.DetachGaze(copy);
                _actors.Restore(copy, state);
            }
            return copy;
        }
        var actor = Posed();
        if (actor == null)
            return null;
        if (_actors.IsSpawned(actor))
            _actors.NameCreated(actor, name);
        var slot = _slots.SlotFor(actor);
        slot.PosedDuplicate = true;
        slot.Respawn = Posed;
        slot.HasRespawn = true;
        _history.Append(new SceneLifecyclePatch(
            description,
            () => _slots.CaptureAndRemove(slot),
            () => _slots.Restore(slot)));
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
        // Despawned by the actor menu already: the removal this undo names
        // has happened, so it reports the truth rather than failing on a
        // corpse and pinning every older entry behind it.
        if (_actors.IsSpawned(actor))
        {
            // Captured HERE, not at spawn: the actor comes back where the
            // user left it, in the pose they gave it — the same rule the
            // light and the prop follow.
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
        // The body is back; the placement and the pose land on it over the
        // next few ticks, because the skeleton they need is built with a draw
        // object the spawn deliberately defers. The entry has landed either
        // way: the actor IS restored, and a pose that cannot follow says so
        // rather than leaving the step un-consumed and unrepeatable.
        if (slot.HasDocument)
            _actors.Restore(actor, slot.Document, () => ReferenceEquals(_slots.CurrentInstance(slot), actor));
        return true;
    }
}
