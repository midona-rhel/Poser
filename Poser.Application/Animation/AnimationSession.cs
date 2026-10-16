using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.World;

namespace Poser.Application.Animation;

/// <summary>
/// Owns Poser's session animation changes by exact actor generation.
/// Each native write records its first restore point. Ownership is cleared
/// only after restoration succeeds, so a live actor can retry a failed reset.
/// Selection, repeat, and speed state remain separate from pose history.
/// The verbs are split across <see cref="BaseAnimationSession"/>,
/// <see cref="SpeedAnimationSession"/>, <see cref="StanceAnimationSession"/>,
/// <see cref="ScrubAnimationSession"/> and <see cref="HeldExpressionSession"/>
/// over one <see cref="AnimationOverrideStore"/>; this class composes the
/// cross-cutting verbs (play, slot reset, actor reset) and the scene's
/// physics hold.
/// </summary>
public sealed class AnimationSession : IAnimationPlayback
{
    private readonly AnimationOverrideStore _store = new();
    private readonly IAnimationTimelinePort _timeline;
    private readonly IAnimationSpeedPort _speedPort;
    private readonly IAnimationStancePort _stancePort;
    private readonly IWorldRenderingRuntimePort _rendering;
    private readonly BaseAnimationSession _base;
    private readonly SpeedAnimationSession _speed;
    private readonly StanceAnimationSession _stance;
    private readonly ScrubAnimationSession _scrub;
    private readonly HeldExpressionSession _expression;
    private readonly HashSet<ActorId> _advancedActors = new();
    public bool IsAdvanced(ActorId actor) => _advancedActors.Contains(actor);
    internal void SetAdvanced(ActorId actor, bool enabled)
    {
        if (enabled) _advancedActors.Add(actor);
        else _advancedActors.Remove(actor);
    }
    /// <summary>Tracks the scene physics hold.</summary>
    private bool _sceneOwnsPhysics;

    /// <summary>Diagnostic tap for the pause/play path — wired to the
    /// plugin log at composition; every verb that can start or stop
    /// motion reports through it.</summary>
    public Action<string>? Trace
    {
        get => _store.Trace;
        set => _store.Trace = value;
    }

    public AnimationSession(
        IAnimationTimelinePort timeline,
        IAnimationSpeedPort speed,
        IAnimationStancePort stance,
        IAnimationScrubPort scrub,
        IWorldRenderingRuntimePort rendering)
    {
        _timeline = timeline;
        _speedPort = speed;
        _stancePort = stance;
        _rendering = rendering;
        _base = new BaseAnimationSession(_store, timeline);
        _speed = new SpeedAnimationSession(_store, timeline, speed);
        _stance = new StanceAnimationSession(_store, timeline, stance, _base);
        _scrub = new ScrubAnimationSession(scrub, _speed);
        _expression = new HeldExpressionSession(_store, timeline, speed, _base, _speed);
        _timeline.LayerEnded += OnLayerEnded;
    }

    /// <summary>
    /// A layer Poser applied ran out (not looped) and the game ended it. Its row
    /// stays (selection, speed, weight); its play or scrub plays it again.
    /// </summary>
    private void OnLayerEnded(ActorId actor, AnimationSlot slot, float seconds)
    {
        if (_store.CommandsSuspended || SelectedFor(actor, slot) == null)
            return;
        _store.Mutate(actor, o => o with
        {
            EndedSlots = new Dictionary<AnimationSlot, float>(o.EndedSlots) { [slot] = seconds },
        });
    }

    /// <summary>The length in seconds of a layer the game ended, or null while it
    /// plays (or was never applied).</summary>
    public float? EndedLength(ActorId actor, AnimationSlot slot) =>
        _store.For(actor).EndedSlots.TryGetValue(slot, out var seconds) ? seconds : null;

    /// <summary>A layer the game ended plays its animation again from the start.</summary>
    private Outcome RestartIfEnded(ActorId actor, AnimationSlot slot)
    {
        if (!_store.For(actor).EndedSlots.ContainsKey(slot))
            return Outcome.Ok();
        var replayed = _base.ApplySelectedSlotCore(actor, slot);
        if (!replayed.Success)
            return replayed;
        _timeline.SetLayerOwned(actor, slot, true);
        ForgetEnded(actor, slot);
        return replayed;
    }

    /// <summary>A scrub on a layer the game ended: it plays again, held, so the
    /// scrub can move it once it is loaded.</summary>
    private Outcome ReplayEndedForScrubUntracked(ActorId actor, AnimationSlot slot)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (!_store.For(actor).EndedSlots.ContainsKey(slot))
            return Outcome.Fail("This layer is still playing.");
        var replayed = RestartIfEnded(actor, slot);
        if (!replayed.Success || IsPaused(actor) || _speed.IsSlotHeld(actor, slot))
            return replayed;
        return _speed.PauseSlot(actor, slot);
    }

    private void ForgetEnded(ActorId actor, AnimationSlot slot) =>
        _store.Mutate(actor, o =>
        {
            if (!o.EndedSlots.ContainsKey(slot))
                return o;
            var ended = new Dictionary<AnimationSlot, float>(o.EndedSlots);
            ended.Remove(slot);
            return o with { EndedSlots = ended };
        });

    public AnimationOverrides OverridesFor(ActorId actor) => _store.For(actor);

    // ── Tracked actions (each a step) ───────────────────────────────────

    public Outcome ReplayEndedForScrub(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Scrub {AnimationSlots.DisplayName(slot)}", () => ReplayEndedForScrubUntracked(actor, slot));

    public Outcome SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        Tracked(actor, $"Loop {AnimationSlots.DisplayName(slot)}", () => SetSlotLoopUntracked(actor, slot, timeline, on));

    public Outcome SetSpeed(ActorId actor, float speed) =>
        Tracked(actor, "Animation speed", () => SetSpeedUntracked(actor, speed));

    public Outcome ClearSpeed(ActorId actor) =>
        Tracked(actor, "Animation speed", () => ClearSpeedUntracked(actor));

    public Outcome Pause(ActorId actor) =>
        Tracked(actor, "Pause animation", () => PauseUntracked(actor));

    public Outcome Resume(ActorId actor) =>
        Tracked(actor, "Play animation", () => ResumeUntracked(actor));

    public Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} speed", () => SetSlotSpeedUntracked(actor, slot, speed));

    public Outcome ClearSlotSpeed(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} speed", () => ClearSlotSpeedUntracked(actor, slot));

    public Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} weight", () => SetSlotWeightUntracked(actor, slot, weight));

    public Outcome PauseSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Pause {AnimationSlots.DisplayName(slot)}", () => PauseSlotUntracked(actor, slot));

    public Outcome ResumeSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Play {AnimationSlots.DisplayName(slot)}", () => ResumeSlotUntracked(actor, slot));

    public Outcome ResetSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Reset {AnimationSlots.DisplayName(slot)}", () => ResetSlotUntracked(actor, slot));

    public Outcome SetLips(ActorId actor, ushort timeline) =>
        Tracked(actor, "Lips", () => SetLipsUntracked(actor, timeline));

    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose) =>
        Tracked(actor, "Stance", () => SetStanceUntracked(actor, stance, pose));

    public Outcome SetWeaponDrawn(ActorId actor, bool drawn) =>
        Tracked(actor, "Weapon", () => SetWeaponDrawnUntracked(actor, drawn));

    public Outcome SetPositionLock(ActorId actor, bool locked) =>
        Tracked(actor, "Position lock", () => SetPositionLockUntracked(actor, locked));

    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null) =>
        Tracked(actor, $"Scrub {(slot is { } s ? AnimationSlots.DisplayName(s) : "animation")}", () => BeginScrubUntracked(actor, control, owner, slot));

    public Outcome UpdateScrub(ActorId actor, float time, Guid owner) =>
        Tracked(actor, ScrubDescription(), () => UpdateScrubUntracked(actor, time, owner));

    public Outcome SeekLayer(ActorId actor, AnimationSlot slot, float seconds) =>
        Tracked(actor, $"Scrub {AnimationSlots.DisplayName(slot)}", () => SeekLayerUntracked(actor, slot, seconds));

    public Outcome HoldExpression(ActorId actor, ushort timeline) =>
        Tracked(actor, "Hold expression", () => HoldExpressionUntracked(actor, timeline));

    public Outcome ReleaseExpression(ActorId actor) =>
        Tracked(actor, "Release expression", () => ReleaseExpressionUntracked(actor));

    public Outcome ResetActor(ActorId actor) =>
        Tracked(actor, "Reset animation", () => ResetActorUntracked(actor));

    // ── Undo / redo ───────────────────────────────────────────────────

    /// <summary>
    /// Set by the history owner (AnimationSteps): runs one user action and records
    /// it as one step, the actor's snapshot before and after. Every changing call
    /// below goes through <see cref="Tracked"/>; only the outermost one records,
    /// so an action that runs others is still one step.
    /// </summary>
    internal Func<ActorId, string, Func<Outcome>, Outcome>? Journal { get; set; }
    /// <summary>Set by the history owner: runs several actors' actions as one step.</summary>
    internal Action<Action>? Group { get; set; }
    private int _trackDepth;
    private int _untracked;

    /// <summary>One user action, recorded as one step (unless nested or untracked).</summary>
    public Outcome Tracked(ActorId actor, string description, Func<Outcome> action)
    {
        if (_trackDepth == 0 && _untracked == 0 && !description.StartsWith("Scrub", StringComparison.Ordinal))
            ForgetSeekTargets(actor);
        if (Journal is not { } journal || _trackDepth > 0 || _untracked > 0)
            return action();
        _trackDepth++;
        try { return journal(actor, description, action); }
        finally { _trackDepth--; }
    }

    private void ForgetSeekTargets(ActorId actor)
    {
        if (_seekTargets.Count == 0)
            return;
        foreach (var key in _seekTargets.Keys.Where(key => key.Actor.Equals(actor)).ToList())
            _seekTargets.Remove(key);
    }

    /// <summary>Several actors' actions as one step (the scene's play/pause all).</summary>
    public void Grouped(Action action)
    {
        if (Group is { } group && _trackDepth == 0 && _untracked == 0)
            group(action);
        else
            action();
    }

    /// <summary>While held, nothing records: internal flows (a pose import's own
    /// pause) and undo/redo themselves.</summary>
    public IDisposable Untracked()
    {
        _untracked++;
        return new UntrackedScope(this);
    }

    private sealed class UntrackedScope(AnimationSession owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._untracked--;
        }
    }

    /// <summary>The actor's animation state as an undo step stores it.</summary>
    public AnimationSnapshot Capture(ActorId actor)
    {
        var owned = _store.For(actor);
        var reading = _timeline.Read(actor) ?? ActorAnimationReading.Empty;
        bool allHeld = owned.OverallSpeed == 0f;
        LayerSnapshot Layer(AnimationSlot slot)
        {
            ushort? applied = slot == AnimationSlot.Base
                ? owned.BaseTimeline
                : owned.AppliedSlots.TryGetValue(slot, out var a) ? a : null;
            float? ended = owned.EndedSlots.TryGetValue(slot, out var e) ? e : null;
            bool live = applied is { } timeline && ended == null && reading.TimelineFor(slot) == timeline;
            float? speed = owned.SlotSpeeds.TryGetValue(slot, out var sp) ? sp : null;
            bool held = live && (allHeld || speed == 0f);
            float? frame = null;
            if (held)
            {
                var control = _scrub.FindSlotControl(actor, slot);
                frame = _seekTargets.TryGetValue((actor, slot), out var target)
                    ? (control is { Duration: > 0f } c ? Math.Min(target, c.Duration) : target)
                    : control?.Time;
            }
            return new LayerSnapshot(applied, live, owned.LoopWantedSlots.Contains(slot), speed,
                owned.SlotWeights.GetValueOrDefault(slot, 1f), frame, ended);
        }
        return new AnimationSnapshot(
            Layer(AnimationSlot.Base), Layer(AnimationSlot.UpperBody), Layer(AnimationSlot.Facial), Layer(AnimationSlot.Additive),
            owned.Lips, owned.OverallSpeed, owned.HeldExpression,
            reading.Stance, reading.Pose, reading.WeaponDrawn, owned.PositionLock, IsAdvanced(actor));
    }

    /// <summary>
    /// <see cref="Capture"/> for a scene file: a paused slot still playing the
    /// game's own animation becomes a held layer on its frame, so a load plays
    /// that animation and holds it there. Undo keeps those slots the game's.
    /// </summary>
    public AnimationSnapshot CaptureForScene(ActorId actor)
    {
        var snapshot = Capture(actor);
        var owned = _store.For(actor);
        var reading = _timeline.Read(actor) ?? ActorAnimationReading.Empty;
        LayerSnapshot Layer(AnimationSlot slot, LayerSnapshot layer)
        {
            if (layer.Applied is not null || layer.EndedSeconds is not null)
                return layer;
            // A Pose-tab expression is saved as itself.
            if (slot == AnimationSlot.Facial && owned.HeldExpression is not null)
                return layer;
            bool paused = owned.OverallSpeed == 0f || owned.SlotSpeeds.GetValueOrDefault(slot, 1f) == 0f;
            ushort native = reading.TimelineFor(slot);
            if (!paused || native == 0 || _scrub.FindSlotControl(actor, slot) is not { } control)
                return layer;
            // The game's idles loop.
            return new LayerSnapshot(native, true, true, layer.Speed, layer.Weight, control.Time, null);
        }
        return snapshot with
        {
            Base = Layer(AnimationSlot.Base, snapshot.Base),
            UpperBody = Layer(AnimationSlot.UpperBody, snapshot.UpperBody),
            Facial = Layer(AnimationSlot.Facial, snapshot.Facial),
            Additive = Layer(AnimationSlot.Additive, snapshot.Additive),
        };
    }

    /// <summary>
    /// Puts the actor back into <paramref name="target"/>: each layer plays its
    /// animation again (from its start when it was running), held layers at their
    /// exact frame, ended ones ended; then speeds, weights, loops, the whole-actor
    /// speed, lips, stance, weapon, position lock and the held expression. Nothing
    /// in here records.
    /// </summary>
    public Outcome Restore(ActorId actor, AnimationSnapshot target)
    {
        using var untracked = Untracked();
        if (_store.Suspended() is { } blocked) return blocked;
        var now = Capture(actor);
        if (now == target)
            return Outcome.Ok();
        var failures = new List<string>();
        void Note(Outcome result, string what)
        {
            if (!result.Success) failures.Add($"{what}: {result.Detail ?? "failed"}");
        }
        // Plays below land moving, so a whole-actor pause is lifted first and put
        // back after.
        if (now.OverallSpeed is not null)
            Note(ClearSpeed(actor), "speed");
        // The stance first: it resets the Full body layer and plays its own
        // animation, so a layer put back before it would be lost.
        if (now.Stance != target.Stance || now.Pose != target.Pose)
        {
            Note(SetStance(actor, target.Stance, target.Pose), "stance");
            now = Capture(actor);
        }
        foreach (var slot in AnimationSnapshot.Layers)
            RestoreLayer(actor, slot, now.For(slot), target.For(slot), Note);
        if (now.Lips != target.Lips)
            Note(SetLips(actor, target.Lips ?? 0), "lips");
        if (now.WeaponDrawn != target.WeaponDrawn)
            Note(SetWeaponDrawn(actor, target.WeaponDrawn), "weapon");
        if (now.PositionLock != target.PositionLock)
            Note(SetPositionLock(actor, target.PositionLock), "position lock");
        if (target.OverallSpeed is { } overall)
            Note(overall == 0f ? Pause(actor) : SetSpeed(actor, overall), "speed");
        // Held layers go to their exact frame once everything holds still.
        foreach (var slot in AnimationSnapshot.Layers)
            if (target.For(slot) is { HeldSeconds: { } seconds, Live: true })
                Note(SeekLayer(actor, slot, seconds), $"{AnimationSlots.DisplayName(slot)} frame");
        if (now.HeldExpression != target.HeldExpression)
            Note(target.HeldExpression is { } expression ? HoldExpression(actor, expression) : ReleaseExpression(actor),
                "expression");
        if (now.Advanced != target.Advanced)
            SetAdvanced(actor, target.Advanced);
        return failures.Count == 0 ? Outcome.Ok() : Outcome.Fail(string.Join("; ", failures));
    }

    private void RestoreLayer(ActorId actor, AnimationSlot slot, LayerSnapshot now, LayerSnapshot target,
        Action<Outcome, string> note)
    {
        string name = AnimationSlots.DisplayName(slot);
        if (target.Applied is not { } timeline)
        {
            if (now.Applied is not null || now.Speed is not null || now.Weight != 1f)
                note(ResetSlot(actor, slot), $"reset {name}");
            return;
        }
        bool sameAnimation = now.Applied == timeline;
        if (target.EndedSeconds is { } length)
        {
            // Ended: its row names it, nothing plays.
            note(ChooseSlot(actor, slot, timeline), name);
            if (now.Live)
                note(_timeline.ClearSlotTimeline(actor, slot), $"end {name}");
            _timeline.SetLayerOwned(actor, slot, false);
            _store.Mutate(actor, o => o with
            {
                EndedSlots = new Dictionary<AnimationSlot, float>(o.EndedSlots) { [slot] = length },
            });
        }
        else if (!now.Live || !sameAnimation)
        {
            note(ChooseSlot(actor, slot, timeline), name);
            note(PlaySelectedSlot(actor, slot, null, false), $"play {name}");
        }
        else if (target.HeldSeconds == null && now.HeldSeconds != null)
        {
            // Back to running: from its start (a running layer stores no frame).
            note(SeekLayer(actor, slot, 0f), $"restart {name}");
        }
        var after = _store.For(actor);
        if (after.LoopWantedSlots.Contains(slot) != target.Loop)
            note(_base.SetSlotLoop(actor, slot, timeline, target.Loop), $"{name} loop");
        if (after.SlotWeights.GetValueOrDefault(slot, 1f) != target.Weight)
            note(SetSlotWeight(actor, slot, target.Weight), $"{name} weight");
        float? speed = after.SlotSpeeds.TryGetValue(slot, out var sp) ? sp : null;
        if (speed != target.Speed)
            note(target.Speed is { } wanted ? SetSlotSpeed(actor, slot, wanted) : ClearSlotSpeed(actor, slot), $"{name} speed");
    }

    public bool LoopWantedFor(ActorId actor, AnimationSlot slot) =>
        OverridesFor(actor).LoopWantedSlots.Contains(slot);

    public ActorAnimationReading? Read(ActorId actor) => _timeline.Read(actor);

    /// <summary>
    /// True while a multi-phase operation owns the actor's animation — a
    /// facial bake between its capture and apply phases. Every command
    /// that could change what the face is doing is refused, because the
    /// captured values would then describe a face that no longer exists.
    /// Reads stay available so surfaces can keep rendering.
    /// </summary>
    public bool CommandsSuspended => _store.CommandsSuspended;

    public void SuspendCommands()
    {
        _store.CommandsSuspended = true;
        // Armed loops would replay animations into the settling baseline.
        _timeline.LoopsSuspended = true;
    }

    public void ResumeCommands()
    {
        _store.CommandsSuspended = false;
        _timeline.LoopsSuspended = false;
    }

    public bool IsSupported(ActorId actor) => _timeline.IsSupported(actor);

    public bool IsPhysicsFrozen => _rendering.IsPhysicsFrozen;

    // ── Base and blend ────────────────────────────────────────────────

    public ushort? SelectedFor(ActorId actor, AnimationSlot slot) =>
        _base.SelectedFor(actor, slot);

    /// <summary>
    /// The General Apply's emote for how the actor is now: the emote's version for
    /// its posture (sitting, dozing, mounted, ...), usually on another layer, as the
    /// game would play it. Anything but an emote's standing pick plays as picked.
    /// Null, with the reason, where the game has no version for the posture.
    /// </summary>
    public TimelineEntry? PostureEntry(ActorId actor, TimelineEntry entry, out string? refusal)
    {
        refusal = null;
        if (entry.Kind != AnimationKind.Emote || entry.EmoteId == 0 || entry.EmoteIndex != 0)
            return entry;
        if (_timeline.EmotePostureRow(actor, entry.EmoteId) is not { } row || row == entry.TimelineId)
            return entry;
        if (row == 0)
        {
            refusal = $"{entry.Name} has no version the game plays for how the actor is now (sitting, dozing, mounted, swimming or diving).";
            return null;
        }
        return entry with { TimelineId = row, Slot = _timeline.TimelineSlot(row) ?? AnimationSlot.Base };
    }

    /// <summary>Stages a selection without reading or writing native state.</summary>
    public Outcome ChooseSlot(ActorId actor, AnimationSlot slot, ushort timeline) =>
        _base.ChooseSlot(actor, slot, timeline);

    /// <summary>Sets repeat intent for one slot.</summary>
    private Outcome SetSlotLoopUntracked(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        _base.SetSlotLoop(actor, slot, timeline, on);

    /// <summary>Whether full-body repeat is available.</summary>
    public bool SupportsForceLoop => _base.SupportsForceLoop;

    /// <summary>False when the client's stance-transition functions were
    /// not found; the stance controls render disabled.</summary>
    public bool SupportsStance => _stance.SupportsStance;

    // ── Speed ─────────────────────────────────────────────────────────

    private Outcome SetSpeedUntracked(ActorId actor, float speed) => _speed.SetSpeed(actor, speed);

    private Outcome ClearSpeedUntracked(ActorId actor) => _speed.ClearSpeed(actor);

    public bool IsPaused(ActorId actor) => _speed.IsPaused(actor);

    private Outcome PauseUntracked(ActorId actor) => _speed.Pause(actor);

    /// <summary>Play-all: releases every hold.</summary>
    private Outcome ResumeUntracked(ActorId actor)
    {
        foreach (var slot in _store.For(actor).EndedSlots.Keys.ToList())
            RestartIfEnded(actor, slot);
        return _speed.Resume(actor);
    }

    /// <summary>Whether any live layer is actually moving.</summary>
    public bool AnyPlaying(ActorId actor) => _speed.AnyPlaying(actor);

    /// <summary>
    /// Replays from the start after releasing a Poser-owned pause. A nonzero
    /// owned speed remains active. <paramref name="resumed"/> reports whether
    /// the pause was released.
    /// </summary>
    public Outcome Replay(ActorId actor, ushort timeline, out bool resumed)
    {
        bool lifted = false;
        var result = Tracked(actor, "Play animation", () =>
        {
            var played = ReplayUntracked(actor, timeline, out var r);
            lifted = r;
            return played;
        });
        resumed = lifted;
        return result;
    }

    private Outcome ReplayUntracked(ActorId actor, ushort timeline, out bool resumed)
    {
        resumed = false;
        if (_store.Suspended() is { } blocked) return blocked;
        if (IsPaused(actor))
        {
            var released = _speed.ResumeForLayerPlay(
                actor, _timeline.TimelineSlot(timeline) ?? AnimationSlot.Base);
            if (!released.Success)
                return released;
            resumed = true;
        }
        return _base.BlendCore(actor, timeline, _timeline.TimelineSlot(timeline));
    }

    /// <summary>Rewinds paused animation controls.</summary>
    public Outcome RewindPausedControls(ActorId actor) => _speed.RewindPausedControls(actor);

    private Outcome SetSlotSpeedUntracked(ActorId actor, AnimationSlot slot, float speed) =>
        _speed.SetSlotSpeed(actor, slot, speed);

    private Outcome ClearSlotSpeedUntracked(ActorId actor, AnimationSlot slot) =>
        _speed.ClearSlotSpeed(actor, slot);

    /// <summary>The layer's held blend weight, or 1 when Poser holds none.</summary>
    public float WeightFor(ActorId actor, AnimationSlot slot) =>
        OverridesFor(actor).SlotWeights.GetValueOrDefault(slot, 1f);

    /// <summary>Holds a layer's blend weight in [0, 1]; 1 releases it.</summary>
    private Outcome SetSlotWeightUntracked(ActorId actor, AnimationSlot slot, float weight)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (!float.IsFinite(weight))
            return Outcome.Fail("Weight must be a finite number.");
        weight = Math.Clamp(weight, 0f, 1f);
        var result = weight >= 1f
            ? _speedPort.ClearSlotWeight(actor, slot)
            : _speedPort.SetSlotWeight(actor, slot, weight);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o =>
        {
            var weights = new Dictionary<AnimationSlot, float>(o.SlotWeights);
            if (weight >= 1f) weights.Remove(slot); else weights[slot] = weight;
            return o with { SlotWeights = weights };
        });
        return Outcome.Ok();
    }

    private Outcome PauseSlotUntracked(ActorId actor, AnimationSlot slot) =>
        _speed.PauseSlot(actor, slot);

    /// <summary>The layer's play icon: lifts the whole-actor pause for this layer
    /// only (the others keep holding), then its own hold. Apply is what changes
    /// the animation; this only changes whether it moves.</summary>
    private Outcome ResumeSlotUntracked(ActorId actor, AnimationSlot slot)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var restarted = RestartIfEnded(actor, slot);
        if (!restarted.Success)
            return restarted;
        if (IsPaused(actor))
        {
            var lifted = _speed.ResumeForLayerPlay(actor, slot);
            if (!lifted.Success)
                return lifted;
        }
        var resumed = _speed.ResumeSlotSpeedCore(actor, slot);
        // Playing the Facial layer turns a held expression into that layer.
        if (resumed.Success && slot == AnimationSlot.Facial)
            _store.Mutate(actor, o => o with { HeldExpression = null });
        return resumed;
    }

    /// <summary>Applies Selected; only Base may use the emote lifecycle.</summary>
    /// <summary>APPLY stages, PLAY plays (ruled 2026-09-01): with
    /// <paramref name="resume"/> false, a paused actor takes the animation
    /// frozen at its start and nothing moves — the layer's Play button (or
    /// the sidebar's play-all) is what starts it.</summary>
    public Outcome PlaySelectedSlot(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume = true) =>
        Tracked(actor, $"Play {AnimationSlots.DisplayName(slot)}",
            () => PlaySelectedSlotUntracked(actor, slot, entry, playFromStart, resume));

    private Outcome PlaySelectedSlotUntracked(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume)
    {
        var before = _timeline.Read(actor)?.TimelineFor(slot);
        // Playing the animation that ran out plays it again from its start.
        if (resume && before != null && SelectedFor(actor, slot) == before)
            RestartIfEnded(actor, slot);
        var outcome = PlaySelectedSlotTraced(actor, slot, entry, playFromStart, resume);
        if (outcome.Success)
        {
            // A Poser layer from here on: the game ends it only at its end. A new
            // animation is no longer the one that ended.
            if (slot != AnimationSlot.Lips)
                _timeline.SetLayerOwned(actor, slot, true);
            if (_timeline.Read(actor)?.TimelineFor(slot) != before)
                ForgetEnded(actor, slot);
        }
        Trace?.Invoke(outcome.Success
            ? $"  PlaySelectedSlot {slot} -> ok"
            : $"  PlaySelectedSlot {slot} -> FAIL: {outcome.Detail}");
        return outcome;
    }

    private Outcome PlaySelectedSlotTraced(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume)
    {
        if (slot == AnimationSlot.Facial)
            return PlayFacialLayer(actor, entry, playFromStart, resume);
        return PlaySelectedSlotCore(actor, slot, entry, playFromStart, resume);
    }

    /// <summary>
    /// The Animation tab's Facial layer: a normal layer that loops. A held
    /// expression (the Pose tab's static face) is released into it first, so the
    /// layer owns the face from here on.
    /// </summary>
    private Outcome PlayFacialLayer(
        ActorId actor, TimelineEntry? entry, bool playFromStart, bool resume)
    {
        var current = _store.For(actor);
        if (current.HeldExpression != null)
        {
            var released = _speed.ClearSlotSpeedCore(actor, AnimationSlot.Facial);
            if (!released.Success)
                return released;
            _store.Mutate(actor, o => o with { HeldExpression = null });
        }
        var played = PlaySelectedSlotCore(actor, AnimationSlot.Facial, entry, playFromStart, resume);
        if (!played.Success || _store.For(actor).LoopWantedSlots.Contains(AnimationSlot.Facial))
            return played;
        return _base.SetSlotLoop(actor, AnimationSlot.Facial, 0, true);
    }

    private Outcome PlaySelectedSlotCore(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume)
    {
        bool resumedOverall = false;
        Trace?.Invoke(
            $"PlaySelectedSlot {actor} slot={slot} resume={resume} "
            + $"paused={IsPaused(actor)} "
            + $"slotSpeed={OverridesFor(actor).SlotSpeeds.GetValueOrDefault(slot, float.NaN)}");
        if (SelectedFor(actor, slot) is { } selected)
        {
            // A null entry plays the session's own selection as-is: state
            // set outside this pane (a clone's transferred layers) has no
            // pane-local pick, and refusing it stranded the clone
            // ("the chosen animation identity changed").
            if (entry != null && (entry.TimelineId != selected || entry.Slot != slot))
                return Outcome.Fail("The chosen animation identity changed.");
            // RESUME, don't replay: a slot already live on the selected
            // timeline keeps its position — re-blending spawned a crossfade
            // control and restarted the clip from zero (the pause→play
            // scrub reset, sampler 19:50:52).
            bool alreadyLive = Read(actor)?.TimelineFor(slot) == selected;
            if (!alreadyLive)
            {
                var played = _base.ApplySelectedSlotCore(
                    actor, slot, playFromStart && entry != null ? entry : null);
                if (!played.Success)
                    return played;
            }
        }
        if (!resume && IsPaused(actor))
        {
            Trace?.Invoke("  staged only (paused, resume=false)");
            return Outcome.Ok();
        }
        if (IsPaused(actor))
        {
            var resumed = _speed.ResumeForLayerPlay(actor, slot);
            if (!resumed.Success)
                return resumed;
            resumedOverall = true;
        }
        if (OverridesFor(actor).SlotSpeeds.TryGetValue(slot, out var speed) && speed == 0f)
            return resume
                ? _speed.ResumeSlotSpeedCore(actor, slot)
                : Outcome.Ok();
        return SelectedFor(actor, slot) != null || resumedOverall
            ? Outcome.Ok()
            : Outcome.Fail("Choose an animation first.");
    }

    public bool OwnsSlot(ActorId actor, AnimationSlot slot)
    {
        var owned = OverridesFor(actor);
        return SelectedFor(actor, slot) != null ||
            owned.SlotSpeedCaptures.ContainsKey(slot);
    }

    /// <summary>Restores one selectable layer and clears its selection.</summary>
    private Outcome ResetSlotUntracked(ActorId actor, AnimationSlot slot)
    {
        var reset = ResetSlotCore(actor, slot);
        if (reset.Success)
        {
            _timeline.SetLayerOwned(actor, slot, false);
            ForgetEnded(actor, slot);
        }
        return reset;
    }

    private Outcome ResetSlotCore(ActorId actor, AnimationSlot slot)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (!AnimationSlots.Selectable.Contains(slot))
            return Outcome.Fail("This animation layer cannot be reset.");
        if (slot == AnimationSlot.Facial)
        {
            var facial = OverridesFor(actor);
            if (facial.HeldExpression != null ||
                facial.SelectedSlots.ContainsKey(AnimationSlot.Facial) ||
                facial.SlotCaptures.ContainsKey(AnimationSlot.Facial))
            {
                if (facial.SlotWeights.ContainsKey(AnimationSlot.Facial) &&
                    SetSlotWeight(actor, AnimationSlot.Facial, 1f) is { Success: false } weight)
                    return weight;
                if (facial.LoopWantedSlots.Contains(AnimationSlot.Facial) &&
                    _base.SetSlotLoop(actor, AnimationSlot.Facial, 0, false) is { Success: false } loop)
                    return loop;
                return _expression.ReleaseExpressionCore(actor);
            }
        }

        var failures = new List<string>();
        // Restore speed first. A failed unpin must not clear a selection
        // whose paused native state still belongs to Poser. A ZERO speed
        // is a pause, and resets never unpause — only the play verbs do
        // (ruled 2026-09-01); the hold stays parked.
        var beforeReset = OverridesFor(actor);
        if (beforeReset.SlotWeights.ContainsKey(slot))
        {
            var weight = SetSlotWeight(actor, slot, 1f);
            if (!weight.Success)
                return weight;
        }
        if (beforeReset.SlotSpeedCaptures.ContainsKey(slot)
            && beforeReset.SlotSpeeds.GetValueOrDefault(slot) != 0f)
        {
            var speed = _speed.ClearSlotSpeedCore(actor, slot);
            if (!speed.Success)
                return speed;
        }
        Outcome selection = slot switch
        {
            AnimationSlot.Base => _base.ResetBaseSelection(actor),
            AnimationSlot.Lips => SelectedFor(actor, slot) != null
                ? _base.ResetLipsSelection(actor)
                : Outcome.Ok(),
            _ => _base.ResetBlendSelection(actor, slot),
        };
        if (!selection.Success)
            failures.Add(selection.Detail ?? "Layer restore failed.");
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    /// <summary>
    /// Sets the lip override. Selecting None restores the captured incoming
    /// timeline because zero is a native "no speech timeline" value.
    /// </summary>
    private Outcome SetLipsUntracked(ActorId actor, ushort timeline) => _base.SetLips(actor, timeline);

    private Outcome SetStanceUntracked(ActorId actor, AnimationStance stance, int pose) =>
        _stance.SetStance(actor, stance, pose);

    private Outcome SetWeaponDrawnUntracked(ActorId actor, bool drawn) =>
        _stance.SetWeaponDrawn(actor, drawn);

    private Outcome SetPositionLockUntracked(ActorId actor, bool locked) =>
        _stance.SetPositionLock(actor, locked);

    // ── Physics (one global patch, held by the scene) ─────────────────

    /// <summary>
    /// Records the scene hold only after the global patch state matches the
    /// request.
    /// </summary>
    public Outcome SetScenePhysicsFrozen(bool frozen)
    {
        if (frozen == _sceneOwnsPhysics)
            return Outcome.Ok();

        if (frozen != _rendering.IsPhysicsFrozen)
        {
            var result = _rendering.SetPhysicsFrozen(frozen);
            if (!result.Success)
                return result;
        }

        _sceneOwnsPhysics = frozen;
        return Outcome.Ok();
    }

    /// <summary>Whether the scene holds the patch — distinct from
    /// <see cref="IsPhysicsFrozen"/>, which is the global state however it
    /// came to be true.</summary>
    public bool SceneOwnsPhysics => _sceneOwnsPhysics;

    // ── Scrubbing ─────────────────────────────────────────────────────

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _scrub.FindSlotControl(actor, slot);

    /// <summary>Freezes playback and captures the drag's whole mapping.</summary>
    private Outcome BeginScrubUntracked(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null)
    {
        _scrubSlot = slot;
        return _scrub.BeginScrub(actor, control, owner, slot);
    }

    private AnimationSlot? _scrubSlot;
    private string ScrubDescription() =>
        $"Scrub {(_scrubSlot is { } s ? AnimationSlots.DisplayName(s) : "animation")}";

    /// <summary>Writes a frame clamped to the duration captured at Begin.</summary>
    private Outcome UpdateScrubUntracked(ActorId actor, float time, Guid owner)
    {
        var result = _scrub.UpdateScrub(actor, time, owner);
        if (result.Success && _scrubSlot is { } slot)
            _seekTargets[(actor, slot)] = Math.Max(time, 0f);
        return result;
    }

    // The frame a held layer was last asked to seek to, in seconds. The seek lands
    // on the next tick, so a snapshot taken right after the request reads the
    // target, not the old frame. Any other action clears the actor's entries.
    private readonly Dictionary<(ActorId Actor, AnimationSlot Slot), float> _seekTargets = new();

    /// <summary>Ends the drag, leaving the actor paused on the released frame.</summary>
    public void EndScrub(Guid owner) => _scrub.EndScrub(owner);

    /// <summary>Seeks a layer by its own timeline clock (seconds from its begin).</summary>
    private Outcome SeekLayerUntracked(ActorId actor, AnimationSlot slot, float seconds)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var result = _scrub.SeekLayer(actor, slot, seconds);
        if (result.Success)
            _seekTargets[(actor, slot)] = Math.Max(seconds, 0f);
        return result;
    }

    // ── Held expression ──────────────────────────────────────────────────

    /// <summary>Applies an expression and immediately pins its facial frame.</summary>
    private Outcome HoldExpressionUntracked(ActorId actor, ushort timeline) =>
        _expression.HoldExpression(actor, timeline);

    /// <summary>Releases a held facial expression.</summary>
    private Outcome ReleaseExpressionUntracked(ActorId actor) => _expression.ReleaseExpression(actor);

    /// <summary>Restores the captured facial layer.</summary>
    public Outcome RestoreFacialLayer(ActorId actor) => _expression.RestoreFacialLayer(actor);

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) => _expression.HeldExpressionFor(actor);

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>
    /// Restores every override Poser owns for one actor and forgets it.
    /// Safe to call when nothing is owned. Individual failures are
    /// aggregated so one unreachable write cannot strand the rest.
    /// </summary>
    private Outcome ResetActorUntracked(ActorId actor)
    {
        var reset = ResetActorCore(actor);
        if (reset.Success)
        {
            foreach (var slot in AnimationSlots.Selectable)
                _timeline.SetLayerOwned(actor, slot, false);
            _store.Mutate(actor, o => o with { EndedSlots = new Dictionary<AnimationSlot, float>() });
        }
        return reset;
    }

    private Outcome ResetActorCore(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (!_store.TryGet(actor, out var owned))
        {
            // Nothing is owned for this actor. Physics is not among the
            // things that could be: the freeze is held by the scene, not by
            // any actor, so no actor's reset can retire it.
            _timeline.ClearLoops(actor);
            return Outcome.Ok();
        }

        // A body that is gone has nothing left to restore into: retire what
        // Poser owned for it without native writes. Its clears could only
        // fail, and reporting those as unrestored state was false (#495;
        // the game's own GPose actors are often gone at the exit edge).
        if (!_timeline.IsSupported(actor) && _timeline.Read(actor) == null)
        {
            _timeline.ClearLoops(actor);
            _speedPort.Forget(actor);
            _store.Remove(actor);
            return Outcome.Ok();
        }

        // Each aspect is released only when its restore succeeded. What
        // fails stays owned, so a later Reset retries it instead of the
        // override being silently abandoned on a still-live actor.
        var failures = new List<string>();
        var remaining = owned;

        bool Try(Outcome result)
        {
            if (result.Success)
                return true;
            if (result.Detail is { } detail)
                failures.Add(detail);
            return false;
        }

        // Loops first: a still-armed loop would replay the animation the
        // very restore below is removing.
        if (owned.LoopedSlots.Count > 0 || owned.LoopWantedSlots.Count > 0)
        {
            bool cleared = !owned.LoopedSlots.ContainsKey(AnimationSlot.Base) ||
                Try(_timeline.SetForceLoop(actor, 0));
            if (cleared)
            {
                _timeline.ClearLoops(actor);
                remaining = remaining with
                {
                    LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                    LoopWantedSlots = new HashSet<AnimationSlot>(),
                };
            }
        }

        if (owned.OverallSpeed != null && Try(_speedPort.ClearOverallSpeed(actor)))
            remaining = remaining with { OverallSpeed = null };

        // Restore captured non-base timelines.
        if (owned.SlotCaptures.Count > 0)
        {
            var liveRead = _timeline.Read(actor);
            bool cancelNeeded = owned.SlotCaptures.Any(entry =>
                entry.Value == 0 && liveRead?.TimelineFor(entry.Key) is > 0);

            var slots = new Dictionary<AnimationSlot, ushort>(remaining.SlotCaptures);
            bool cancelled = true;
            if (cancelNeeded)
            {
                // Only Poser's layers on slots that were empty go: the slot cancel
                // takes just its own layer. A container cancel took every layer,
                // and the game's own were then replayed from frame 0.
                foreach (var (slot, incoming) in owned.SlotCaptures)
                    if (incoming == 0 && liveRead?.TimelineFor(slot) is > 0)
                        cancelled &= Try(_timeline.ClearSlotTimeline(actor, slot));
            }

            // A failed cancellation processes no slot: replaying would
            // restart layers over a state the cancel never cleared, and
            // releasing any entry would shrink the plan the retry still
            // needs. The complete plan is preserved unchanged, the base
            // restore below still runs for this attempt, and the cancel
            // failure returns with the result.
            if (cancelled)
            {
                foreach (var (slot, incoming) in slots.ToList())
                {
                    if (incoming == 0)
                        slots.Remove(slot);
                    else if (Try(_timeline.Blend(actor, incoming, remaining.BaseCapture, out _)))
                        slots.Remove(slot);
                }
            }
            var selected = new Dictionary<AnimationSlot, ushort>(remaining.SelectedSlots);
            var applied = new Dictionary<AnimationSlot, ushort>(remaining.AppliedSlots);
            foreach (var selectedSlot in selected.Keys.ToList())
                if (!slots.ContainsKey(selectedSlot))
                {
                    selected.Remove(selectedSlot);
                    applied.Remove(selectedSlot);
                }
            remaining = remaining with
            {
                SlotCaptures = slots,
                SelectedSlots = selected,
                AppliedSlots = applied,
                HeldExpression = slots.ContainsKey(AnimationSlot.Facial)
                    ? remaining.HeldExpression
                    : null,
            };
        }

        // Base restoration runs after the expression release and slot
        // replays: those go through the mode dance, which would overwrite
        // the just-restored mode and parameter if the base went back
        // first. The base is restored on every attempt, but its capture is
        // released only once every mode-mutating dependency — expression
        // release, cancellation, slot replays — has resolved: a retry of
        // any of those alters or cancels the base again, and would
        // otherwise find its restoration point already gone.
        if (owned.BaseCapture is { } capture && Try(_timeline.RestoreBase(actor, capture)) &&
            remaining.HeldExpression == null && remaining.SlotCaptures.Count == 0)
        {
            var selected = new Dictionary<AnimationSlot, ushort>(remaining.SelectedSlots);
            selected.Remove(AnimationSlot.Base);
            remaining = remaining with
            {
                SelectedSlots = selected,
                BaseCapture = null,
                BaseTimeline = null,
            };
        }

        if (owned.SlotWeights.Count > 0)
        {
            var weights = new Dictionary<AnimationSlot, float>(remaining.SlotWeights);
            foreach (var slot in owned.SlotWeights.Keys)
                if (Try(_speedPort.ClearSlotWeight(actor, slot)))
                    weights.Remove(slot);
            remaining = remaining with { SlotWeights = weights };
        }

        if (owned.SlotSpeedCaptures.Count > 0)
        {
            var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
            var captures = new Dictionary<AnimationSlot, float>(remaining.SlotSpeedCaptures);
            var resume = new Dictionary<AnimationSlot, float>(remaining.SlotResumeSpeeds);
            foreach (var (slot, restore) in owned.SlotSpeedCaptures)
                if (Try(_speedPort.ClearSlotSpeed(actor, slot, restore)))
                {
                    speeds.Remove(slot);
                    captures.Remove(slot);
                    resume.Remove(slot);
                }
            remaining = remaining with
            {
                SlotSpeeds = speeds,
                SlotSpeedCaptures = captures,
                SlotResumeSpeeds = resume,
            };
        }

        if (owned.StanceCaptureValue is { } stance &&
            Try(_stancePort.SetStance(actor, stance.Stance, stance.Pose)))
            remaining = remaining with { StanceCaptureValue = null };
        if (owned.WeaponCapture is { } weapon &&
            Try(_stancePort.SetWeaponDrawn(actor, weapon)))
            remaining = remaining with { WeaponCapture = null };
        if (owned.LipsCapture is { } lips && Try(_timeline.SetLips(actor, lips)))
        {
            var selected = new Dictionary<AnimationSlot, ushort>(remaining.SelectedSlots);
            selected.Remove(AnimationSlot.Lips);
            remaining = remaining with
            {
                SelectedSlots = selected,
                LipsCapture = null,
                Lips = null,
            };
        }
        // A staged selection with no readable restore point made no native
        // write, so reset can simply forget that intent.
        if (remaining.BaseCapture == null && remaining.BaseTimeline == null ||
            remaining.LipsCapture == null && remaining.Lips == null)
        {
            var selected = new Dictionary<AnimationSlot, ushort>(remaining.SelectedSlots);
            if (remaining.BaseCapture == null && remaining.BaseTimeline == null)
                selected.Remove(AnimationSlot.Base);
            if (remaining.LipsCapture == null && remaining.Lips == null)
                selected.Remove(AnimationSlot.Lips);
            remaining = remaining with { SelectedSlots = selected };
        }
        if (owned.PositionLock && Try(_stancePort.SetPositionLock(actor, false)))
            remaining = remaining with { PositionLock = false };

        if (!remaining.HasAny)
        {
            _store.Remove(actor);
        }
        else
        {
            _store.Set(actor, remaining);
        }

        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>Restores every owned actor. Used by GPose exit, plugin
    /// disposal, and Stop/Restore All.</summary>
    public Outcome ResetAll()
    {
        // A session-wide cleanup (leaving GPose), no user step.
        using var untracked = Untracked();
        var failures = new List<string>();
        foreach (var actor in _store.Actors())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        // The scene's hold holds no override entry, so the loop above never
        // saw it — and no reconcile will ever retire it, because the scene
        // is not something that can depart. This is the one place it is
        // released, and a failed unpatch keeps it on record rather than
        // clearing it over a still-patched site.
        var scene = SetScenePhysicsFrozen(false);
        if (!scene.Success && scene.Detail is { } sceneDetail)
            failures.Add($"scene: {sceneDetail}");
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Drops state for actors the scene no longer contains at that exact
    /// generation. A replaced actor's old generation is released without
    /// touching the new one; a genuinely removed actor is restored first
    /// when it still resolves, and dropped regardless. Called once per
    /// structural scene change.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        // Departed actors are released, not undone.
        using var untracked = Untracked();
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        _advancedActors.RemoveWhere(actor => !present.Contains(actor));
        _scrub.Forget(present);
        // Physics is deliberately absent here: the freeze is held by the
        // scene, which cannot depart, so no actor leaving can retire it.
        var departed = _store.Actors().Where(id => !present.Contains(id)).ToList();
        foreach (var id in departed)
        {
            // Attempt the native restore; an actor that no longer resolves
            // simply has nothing left to restore into, and the entry is
            // dropped either way so it can never be re-applied.
            ResetActor(id);
        }
    }
}
