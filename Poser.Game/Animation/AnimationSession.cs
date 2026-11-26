using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Collections;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.World;

using Poser.Application.Animation;

namespace Poser.Game.Animation;

/// <summary>
/// Handles each actor's animations: which one is chosen for each layer, playing,
/// resetting, and undo. The helper classes for base, speed, stance, scrubbing and
/// expression share one store of choices and of values to restore. Scene physics
/// is kept apart because it affects every actor.
/// </summary>
public sealed class AnimationSession : IAnimationPlayback
{
    private readonly AnimationOverrideStore _store;
    private readonly AnimationTimelineRuntimePort _timeline;
    private readonly IWorldRenderingRuntimePort _rendering;
    private readonly BaseAnimationSession _base;
    private readonly AnimationSpeedRuntimePort _speed;
    private readonly StanceControl _stance;
    private readonly AnimationScrubRuntimePort _scrub;
    private readonly HeldExpressionSession _expression;
    private readonly HashSet<ActorId> _advancedActors = new();
    public bool IsAdvanced(ActorId actor) => _advancedActors.Contains(actor);
    internal void SetAdvanced(ActorId actor, bool enabled)
    {
        if (enabled) _advancedActors.Add(actor);
        else _advancedActors.Remove(actor);
    }
    /// <summary>Whether the scene asked for physics to be frozen.</summary>
    private bool _sceneOwnsPhysics;

    /// <summary>
    /// Optional log callback used by playback and speed operations.
    /// </summary>
    public Action<string>? Trace
    {
        get => _store.Trace;
        set => _store.Trace = value;
    }

    public AnimationSession(
        AnimationOverrideStore store,
        AnimationTimelineRuntimePort timeline,
        AnimationSpeedRuntimePort speed,
        AnimationNativeState native,
        PosingService posing,
        AnimationScrubRuntimePort scrub,
        IWorldRenderingRuntimePort rendering)
    {
        _store = store;
        _timeline = timeline;
        _speed = speed;
        _rendering = rendering;
        _base = new BaseAnimationSession(_store, timeline);
        _stance = new StanceControl(_store, timeline, _base, native, posing);
        _scrub = scrub;
        _expression = new HeldExpressionSession(_store, timeline, _base, _speed);
        _timeline.LayerEnded += OnLayerEnded;
    }

    /// <summary>
    /// A layer Poser applied ran out (not looped) and the game ended it. Its row
    /// stays (selection, speed, weight); its play or scrub plays it again.
    /// </summary>
    private void OnLayerEnded(ActorId actor, AnimationSlot slot, float seconds)
    {
        if (SelectedFor(actor, slot) == null)
            return;
        _store.Mutate(actor, o => o with { EndedSlots = o.EndedSlots.With(slot, seconds) });
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
        var replayed = RestartIfEnded(actor, slot);
        if (!replayed.Success || IsPaused(actor) || _speed.IsSlotHeld(actor, slot))
            return replayed;
        return _speed.PauseSlot(actor, slot);
    }

    private void ForgetEnded(ActorId actor, AnimationSlot slot) =>
        _store.Mutate(actor, o => o.EndedSlots.ContainsKey(slot)
            ? o with { EndedSlots = o.EndedSlots.Without(slot) }
            : o);

    public AnimationOverrides OverridesFor(ActorId actor) => _store.For(actor);

    // ── Actions that each make one undo step ────────────────────────────

    public Outcome ReplayEndedForScrub(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Scrub {AnimationSlots.DisplayName(slot)}", () => ReplayEndedForScrubUntracked(actor, slot));

    public Outcome SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        Tracked(actor, $"Loop {AnimationSlots.DisplayName(slot)}", () => SetSlotLoopUntracked(actor, slot, timeline, on));

    public Outcome SetSpeed(ActorId actor, float speed) =>
        Tracked(actor, "Animation speed", () => _speed.SetSpeed(actor, speed));

    public Outcome ClearSpeed(ActorId actor) =>
        Tracked(actor, "Animation speed", () => _speed.ClearSpeed(actor));

    public Outcome Pause(ActorId actor) =>
        Tracked(actor, "Pause animation", () => _speed.Pause(actor));

    public Outcome Resume(ActorId actor) =>
        Tracked(actor, "Play animation", () => ResumeUntracked(actor));

    public Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} speed", () => _speed.SetSlotSpeed(actor, slot, speed));

    public Outcome ClearSlotSpeed(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} speed", () => _speed.ClearSlotSpeed(actor, slot));

    public Outcome SetSlotWeight(ActorId actor, AnimationSlot slot, float weight) =>
        Tracked(actor, $"{AnimationSlots.DisplayName(slot)} weight", () => SetSlotWeightUntracked(actor, slot, weight));

    public Outcome PauseSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Pause {AnimationSlots.DisplayName(slot)}", () => _speed.PauseSlot(actor, slot));

    public Outcome ResumeSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Play {AnimationSlots.DisplayName(slot)}", () => ResumeSlotUntracked(actor, slot));

    public Outcome ResetSlot(ActorId actor, AnimationSlot slot) =>
        Tracked(actor, $"Reset {AnimationSlots.DisplayName(slot)}", () => ResetSlotUntracked(actor, slot));

    public Outcome SetLips(ActorId actor, ushort timeline) =>
        Tracked(actor, "Lips", () => _base.SetLips(actor, timeline));

    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose) =>
        Tracked(actor, "Stance", () => _stance.SetStance(actor, stance, pose));

    public Outcome SetWeaponDrawn(ActorId actor, bool drawn) =>
        Tracked(actor, "Weapon", () => _stance.SetWeaponDrawn(actor, drawn));

    public Outcome SetPositionLock(ActorId actor, bool locked) =>
        Tracked(actor, "Position lock", () => _stance.SetPositionLock(actor, locked));

    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null) =>
        Tracked(actor, $"Scrub {(slot is { } s ? AnimationSlots.DisplayName(s) : "animation")}", () => BeginScrubUntracked(actor, control, owner, slot));

    public Outcome UpdateScrub(ActorId actor, float time, Guid owner) =>
        Tracked(actor, ScrubDescription(), () => UpdateScrubUntracked(actor, time, owner));

    public Outcome SeekLayer(ActorId actor, AnimationSlot slot, float seconds) =>
        Tracked(actor, $"Scrub {AnimationSlots.DisplayName(slot)}", () => SeekLayerUntracked(actor, slot, seconds));

    public Outcome HoldExpression(ActorId actor, ushort timeline) =>
        Tracked(actor, "Hold expression", () => _expression.HoldExpression(actor, timeline));

    public Outcome ReleaseExpression(ActorId actor) =>
        Tracked(actor, "Release expression", () => _expression.ReleaseExpression(actor));

    public Outcome ResetActor(ActorId actor) =>
        Tracked(actor, "Reset animation", () => ResetActorUntracked(actor));

    // ── Undo / redo ───────────────────────────────────────────────────

    /// <summary>Installed by AnimationSteps: records one command as one undo step.</summary>
    internal Func<ActorId, string, Func<Outcome>, Outcome>? Journal { get; set; }
    /// <summary>Installed by AnimationSteps: runs several actors' actions as one step.</summary>
    internal Action<Action>? Group { get; set; }
    /// <summary>How many commands are running, one inside another.</summary>
    private int _commandDepth;
    private int _untracked;

    /// <summary>
    /// Runs one animation command. It becomes one undo step, unless an Untracked
    /// scope is open. A command that another command runs is
    /// part of that one: it is not checked again and makes no step of its own.
    /// </summary>
    public Outcome Tracked(ActorId actor, string description, Func<Outcome> action)
    {
        if (_commandDepth > 0)
            return action();
        bool record = _untracked == 0;
        if (record && !description.StartsWith("Scrub", StringComparison.Ordinal))
            ForgetSeekTargets(actor);
        _commandDepth++;
        try
        {
            return record && Journal is { } journal
                ? journal(actor, description, action)
                : action();
        }
        finally { _commandDepth--; }
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
        if (Group is { } group && _commandDepth == 0 && _untracked == 0)
            group(action);
        else
            action();
    }

    /// <summary>
    /// Suppresses animation history recording until the returned scope is disposed.
    /// </summary>
    public IDisposable Untracked()
    {
        _untracked++;
        return new UntrackedScope(this);
    }

    private sealed class UntrackedScope(AnimationSession owner) : IDisposable
    {
        public void Dispose() => owner._untracked--;
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
    /// Also captures paused game animations that Poser did not select, and how
    /// far each running animation has played. Scene loading can then recreate
    /// those layers where they were; ordinary undo capture leaves them as they are.
    /// </summary>
    public AnimationSnapshot CaptureForScene(ActorId actor)
    {
        var snapshot = Capture(actor);
        var owned = _store.For(actor);
        var reading = _timeline.Read(actor) ?? ActorAnimationReading.Empty;
        LayerSnapshot Layer(AnimationSlot slot, LayerSnapshot layer)
        {
            if (layer is { Applied: not null, Live: true, HeldSeconds: null }
                && _scrub.FindSlotControl(actor, slot) is { } running)
                return layer with { PlayedSeconds = running.Time };
            if (layer.Applied is not null || layer.EndedSeconds is not null)
                return layer;
            // A Pose-tab expression is saved as itself.
            if (slot == AnimationSlot.Facial && owned.HeldExpression is not null)
                return layer;
            bool paused = owned.OverallSpeed == 0f || owned.SlotSpeeds.GetValueOrDefault(slot, 1f) == 0f;
            ushort native = reading.TimelineFor(slot);
            if (!paused || native == 0 || _scrub.FindSlotControl(actor, slot) is not { } control)
                return layer;
            // Scene capture asks for repeat on every paused game layer.
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
    /// Restores the saved layers and controls without recording another undo step.
    /// Paused layers seek to their saved time. A running layer goes back to how far
    /// it had played when the snapshot holds that (scene saves), else to its start.
    /// Failures are collected; successful changes are not rolled back.
    /// Undo, redo and scene loading call this.
    /// </summary>
    public Outcome Restore(ActorId actor, AnimationSnapshot target)
    {
        using var untracked = Untracked();
        return Tracked(actor, "Restore animation", () => RestoreUntracked(actor, target));
    }

    private Outcome RestoreUntracked(ActorId actor, AnimationSnapshot target)
    {
        var now = Capture(actor);
        if (now == target)
            return Outcome.Ok();
        var failures = new List<string>();
        void Note(Outcome result, string what)
        {
            if (!result.Success) failures.Add($"{what}: {result.Detail ?? "failed"}");
        }
        // Clear the overall speed override before replaying layers, then restore it.
        if (now.OverallSpeed is not null)
            Note(ClearSpeed(actor), "speed");
        // The stance first: it resets the Full body layer and plays its own
        // animation, so a layer restored before it would be lost.
        if (now.Stance != target.Stance || now.Pose != target.Pose)
        {
            Note(SetStance(actor, target.Stance, target.Pose), "stance");
            now = Capture(actor);
        }
        // Putting back a sitting or lying stance already sits the actor down.
        // The Full body animation is then played without standing it up again.
        bool keepPosture = target.Stance is AnimationStance.SitChair or AnimationStance.SitGround
                or AnimationStance.Sleeping
            && _timeline.Read(actor)?.Stance == target.Stance;
        foreach (var slot in AnimationSnapshot.Layers)
            RestoreLayer(actor, slot, now.For(slot), target.For(slot), Note,
                keepPosture && slot == AnimationSlot.Base);
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
            else if (target.For(slot) is { PlayedSeconds: { } played, Live: true })
                Note(SeekLayer(actor, slot, played), $"{AnimationSlots.DisplayName(slot)} position");
        if (now.HeldExpression != target.HeldExpression)
            Note(target.HeldExpression is { } expression ? HoldExpression(actor, expression) : ReleaseExpression(actor),
                "expression");
        if (now.Advanced != target.Advanced)
            SetAdvanced(actor, target.Advanced);
        return failures.Count == 0 ? Outcome.Ok() : Outcome.Fail(string.Join("; ", failures));
    }

    private void RestoreLayer(ActorId actor, AnimationSlot slot, LayerSnapshot now, LayerSnapshot target,
        Action<Outcome, string> note, bool keepPosture)
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
            note(_base.ChooseSlot(actor, slot, timeline), name);
            if (now.Live)
                note(_timeline.ClearSlotTimeline(actor, slot), $"end {name}");
            _timeline.SetLayerOwned(actor, slot, false);
            _store.Mutate(actor, o => o with { EndedSlots = o.EndedSlots.With(slot, length) });
        }
        else if (!now.Live || !sameAnimation)
        {
            note(_base.ChooseSlot(actor, slot, timeline), name);
            note(keepPosture
                ? _base.ReplayBaseInPosture(actor, timeline)
                : PlaySelectedSlot(actor, slot, null, false), $"play {name}");
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

    /// <summary>Holds back animations that are set to repeat, for as long as a face capture
    /// runs: a repeat starting again would play into the face while it settles.</summary>
    public void HoldLoops(bool hold) => _timeline.LoopsSuspended = hold;

    public bool IsSupported(ActorId actor) => _timeline.IsSupported(actor);

    public bool IsPhysicsFrozen => _rendering.IsPhysicsFrozen;

    // ── Base and blend ────────────────────────────────────────────────

    public ushort? SelectedFor(ActorId actor, AnimationSlot slot) =>
        _base.SelectedFor(actor, slot);

    /// <summary>
    /// Selects the standing emote's variant for the actor's current posture.
    /// Other entries are unchanged. Returns null with a reason if the game reports
    /// that the emote has no variant for this posture.
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

    /// <summary>Remembers the chosen animation for a layer without playing it.
    /// Not an undo step.</summary>
    public Outcome ChooseSlot(ActorId actor, AnimationSlot slot, ushort timeline) =>
        _base.ChooseSlot(actor, slot, timeline);

    // ── The General section ───────────────────────────────────────────

    private readonly Dictionary<ActorId, GeneralChoice> _general = new();

    /// <summary>The actor's General choice, or null when there is none or the
    /// layer it played has been reset since.</summary>
    public GeneralChoice? GeneralFor(ActorId actor)
    {
        if (!_general.TryGetValue(actor, out var choice))
            return null;
        if (choice.Played is { } played && SelectedFor(actor, played.Slot) == null)
        {
            _general.Remove(actor);
            return null;
        }
        return choice;
    }

    /// <summary>Remembers the animation chosen in the General section without
    /// playing it. What was played before is kept, so Apply can clear its layer.</summary>
    public void ChooseGeneral(ActorId actor, TimelineEntry entry) =>
        _general[actor] = new GeneralChoice(entry, GeneralFor(actor)?.Played);

    internal void SetGeneral(ActorId actor, GeneralChoice choice) => _general[actor] = choice;

    public void ForgetGeneral(ActorId actor) => _general.Remove(actor);

    /// <summary>Sets whether one layer repeats.</summary>
    private Outcome SetSlotLoopUntracked(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        _base.SetSlotLoop(actor, slot, timeline, on);

    /// <summary>False when the game functions that change stance were not
    /// found; the stance controls are then disabled.</summary>
    public bool SupportsStance => _stance.SupportsStance;

    /// <summary>The stance's pose count from the game's pose table.</summary>
    public int PoseCount(AnimationStance stance, bool weaponDrawn) => _stance.PoseCount(stance, weaponDrawn);

    // ── Speed ─────────────────────────────────────────────────────────

    public bool IsPaused(ActorId actor) => _speed.IsPaused(actor);

    /// <summary>
    /// Tries to restart ended layers, then resumes paused layers except a held expression.
    /// </summary>
    private Outcome ResumeUntracked(ActorId actor)
    {
        foreach (var slot in _store.For(actor).EndedSlots.Keys.ToList())
        {
            var restarted = RestartIfEnded(actor, slot);
            if (!restarted.Success)
                return restarted;
        }
        return _speed.Resume(actor);
    }

    /// <summary>
    /// True when the actor has a layer that Poser has not paused.
    /// </summary>
    public bool AnyPlaying(ActorId actor) => _speed.AnyPlaying(actor);

    /// <summary>Moves every paused animation back to its first frame. Not an undo step.</summary>
    public Outcome RewindPausedControls(ActorId actor) =>
        _speed.RewindPausedControls(actor);

    /// <summary>The layer's held blend weight, or 1 when Poser holds none.</summary>
    public float WeightFor(ActorId actor, AnimationSlot slot) =>
        OverridesFor(actor).SlotWeights.GetValueOrDefault(slot, 1f);

    /// <summary>Holds a layer's blend weight in [0, 1]; 1 releases it.</summary>
    private Outcome SetSlotWeightUntracked(ActorId actor, AnimationSlot slot, float weight)
    {
        return _speed.SetSlotWeight(actor, slot, weight);
    }

    /// <summary>
    /// Resumes this layer, restarting it if it ended. When the whole actor was
    /// paused, the other playing layers are first paused one by one, so only
    /// this layer resumes.
    /// </summary>
    private Outcome ResumeSlotUntracked(ActorId actor, AnimationSlot slot)
    {
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

    /// <summary>
    /// Applies the selected animation if it is not already playing on this layer.
    /// When resume is false, layers Poser paused stay paused. Only the Base layer
    /// can use an emote's introduction through playFromStart.
    /// </summary>
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
        {
            var restarted = RestartIfEnded(actor, slot);
            if (!restarted.Success)
                return restarted;
        }
        var outcome = PlaySelectedSlotTraced(actor, slot, entry, playFromStart, resume);
        if (outcome.Success)
        {
            // Track this layer for end notifications. Clear the old end marker
            // when playback changes to a different timeline.
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
    /// Releases a held expression before playing the selected Facial animation.
    /// Enables Facial repeat after successful playback.
    /// </summary>
    private Outcome PlayFacialLayer(
        ActorId actor, TimelineEntry? entry, bool playFromStart, bool resume)
    {
        var current = _store.For(actor);
        if (current.HeldExpression != null)
        {
            var released = _speed.ClearSlotSpeed(actor, AnimationSlot.Facial);
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
            // Reapplying an already-playing timeline starts a new blend and can
            // restart it. Keep the current control so Resume preserves its time.
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
        if (slot == AnimationSlot.Facial)
        {
            var facial = OverridesFor(actor);
            if (facial.HeldExpression != null ||
                facial.SelectedSlots.ContainsKey(AnimationSlot.Facial) ||
                facial.SlotCaptures.ContainsKey(AnimationSlot.Facial))
            {
                // Weight 1 and Loop off only release what Poser holds; neither can fail.
                if (facial.SlotWeights.ContainsKey(AnimationSlot.Facial))
                    SetSlotWeight(actor, AnimationSlot.Facial, 1f);
                if (facial.LoopWantedSlots.Contains(AnimationSlot.Facial))
                    _base.SetSlotLoop(actor, AnimationSlot.Facial, 0, false);
                return _expression.ReleaseExpression(actor);
            }
        }

        // Restore weight and nonzero speed overrides before the animation.
        // Keep a zero-speed override so resetting the layer does not resume it.
        // Weight 1 only releases Poser's hold, which cannot fail.
        var beforeReset = OverridesFor(actor);
        if (beforeReset.SlotWeights.ContainsKey(slot))
            SetSlotWeight(actor, slot, 1f);
        if (beforeReset.SlotSpeedCaptures.ContainsKey(slot)
            && beforeReset.SlotSpeeds.GetValueOrDefault(slot) != 0f)
        {
            var speed = _speed.ClearSlotSpeed(actor, slot);
            if (!speed.Success)
                return speed;
        }
        return slot switch
        {
            AnimationSlot.Base => _base.ResetBaseSelection(actor),
            AnimationSlot.Lips => SelectedFor(actor, slot) != null
                ? _base.ResetLipsSelection(actor)
                : Outcome.Ok(),
            _ => _base.ResetBlendSelection(actor, slot),
        };
    }

    // ── Physics (one game-wide setting, held by the scene) ────────────

    /// <summary>
    /// Changes global physics when the requested scene flag changes. Records the
    /// scene flag only after the game reports success.
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

    /// <summary>Whether the scene asked for physics to be frozen.
    /// <see cref="IsPhysicsFrozen"/> is different: it says whether physics is
    /// frozen at all, whatever froze it.</summary>
    public bool SceneOwnsPhysics => _sceneOwnsPhysics;

    // ── Scrubbing ─────────────────────────────────────────────────────

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _scrub.FindSlotControl(actor, slot);

    /// <summary>
    /// Remembers the selected layer and starts its paused scrub drag.
    /// </summary>
    private Outcome BeginScrubUntracked(ActorId actor, ScrubControlId control, Guid owner, AnimationSlot? slot = null)
    {
        _scrubSlot = slot;
        return _scrub.BeginScrub(actor, control, owner, slot);
    }

    private AnimationSlot? _scrubSlot;
    private string ScrubDescription() =>
        $"Scrub {(_scrubSlot is { } s ? AnimationSlots.DisplayName(s) : "animation")}";

    /// <summary>Moves to a frame, kept within the length read when the drag began.</summary>
    private Outcome UpdateScrubUntracked(ActorId actor, float time, Guid owner)
    {
        var result = _scrub.UpdateScrub(actor, time, owner);
        if (result.Success && _scrubSlot is { } slot)
            _seekTargets[(actor, slot)] = Math.Max(time, 0f);
        return result;
    }

    // The frame a held layer was last asked to seek to, in seconds. The seek lands
    // on the next tick, so a snapshot taken right after the request reads the
    // target, not the old frame. Any other action that makes an undo step clears them.
    private readonly Dictionary<(ActorId Actor, AnimationSlot Slot), float> _seekTargets = new();

    /// <summary>
    /// Ends the drag without releasing its pause override.
    /// </summary>
    public void EndScrub(Guid owner) => _scrub.EndScrub(owner);

    /// <summary>Seeks a layer by its own timeline clock (seconds from its begin).</summary>
    private Outcome SeekLayerUntracked(ActorId actor, AnimationSlot slot, float seconds)
    {
        var result = _scrub.SeekLayer(actor, slot, seconds);
        if (result.Success)
            _seekTargets[(actor, slot)] = Math.Max(seconds, 0f);
        return result;
    }

    // ── Held expression ──────────────────────────────────────────────────

    /// <summary>Lets go of the held expression and restores the face that played
    /// before Poser changed it. The face capture uses this itself, so it is not
    /// blocked while a capture runs and makes no undo step.</summary>
    public Outcome RestoreFacialLayer(ActorId actor) => _expression.ReleaseExpression(actor);

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) => _expression.HeldExpressionFor(actor);

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to restore the actor's saved animation values. Failed restorations
    /// remain recorded for a later retry; unavailable actors are forgotten.
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
        if (!_store.TryGet(actor, out var owned))
        {
            // Poser holds nothing for this actor. Physics is never held per
            // actor: the scene holds the freeze, so resetting an actor cannot
            // end it.
            _timeline.ClearLoops(actor);
            return Outcome.Ok();
        }

        // If the game side cannot find a supported, readable actor, discard its
        // saved values. Leaving GPose may already have removed the actor.
        if (!_timeline.IsSupported(actor) && _timeline.Read(actor) == null)
        {
            _timeline.ClearLoops(actor);
            _store.Remove(actor);
            return Outcome.Ok();
        }

        // The actor was found just above. Releases that can only fail when the
        // actor is missing (loops, speeds, weights, lips, position lock) are not
        // checked. Anything else is forgotten only when its restore worked; what
        // fails stays recorded, so a later Reset tries it again.
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
            if (owned.LoopedSlots.ContainsKey(AnimationSlot.Base))
                _timeline.SetForceLoop(actor, 0);
            _timeline.ClearLoops(actor);
            remaining = remaining with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                LoopWantedSlots = new HashSet<AnimationSlot>(),
            };
        }

        if (owned.OverallSpeed != null)
        {
            _speed.ClearSpeed(actor);
            remaining = remaining with { OverallSpeed = null };
        }

        // Restore the saved layers other than Base.
        if (owned.SlotCaptures.Count > 0)
        {
            var liveRead = _timeline.Read(actor);
            bool cancelNeeded = owned.SlotCaptures.Any(entry =>
                entry.Value == 0 && liveRead?.TimelineFor(entry.Key) is > 0);

            var slots = new Dictionary<AnimationSlot, ushort>(remaining.SlotCaptures);
            bool cancelled = true;
            if (cancelNeeded)
            {
                // Only Poser's layers on slots that were empty are removed:
                // cancelling a slot removes just its own layer. Cancelling the
                // whole animation container would remove every layer, and the
                // game's own would then restart from frame 0.
                foreach (var (slot, incoming) in owned.SlotCaptures)
                    if (incoming == 0 && liveRead?.TimelineFor(slot) is > 0)
                        cancelled &= Try(_timeline.ClearSlotTimeline(actor, slot));
            }

            // If cancelling fails, no layer is touched. Replaying would
            // restart layers on top of a state the cancel never cleared, and
            // forgetting any entry would lose part of what the retry still
            // needs. Everything is kept as it is, the Base restore below
            // still runs this time, and the cancel failure is returned with
            // the result.
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

        // Restoring other layers can change the base mode, so restore Base last.
        // Keep its original values while other layers still need a retry.
        if (owned.BaseCapture is { } capture && Try(_timeline.RestoreBase(actor, capture)) &&
            remaining.HeldExpression == null && remaining.SlotCaptures.Count == 0)
        {
            remaining = remaining with
            {
                SelectedSlots = remaining.SelectedSlots.Without(AnimationSlot.Base),
                BaseCapture = null,
                BaseTimeline = null,
            };
        }

        foreach (var slot in owned.SlotWeights.Keys)
        {
            _speed.SetSlotWeight(actor, slot, 1f);
            remaining = remaining with { SlotWeights = remaining.SlotWeights.Without(slot) };
        }

        foreach (var slot in owned.SlotSpeedCaptures.Keys)
        {
            _speed.ClearSlotSpeed(actor, slot);
            remaining = remaining.WithoutSlotSpeed(slot);
        }

        if (owned.StanceCaptureValue is { } stance &&
            Try(_stance.WriteStance(actor, stance.Stance, stance.Pose)))
            remaining = remaining with { StanceCaptureValue = null };
        if (owned.WeaponCapture is { } weapon &&
            Try(_stance.WriteWeaponDrawn(actor, weapon)))
            remaining = remaining with { WeaponCapture = null };
        if (owned.LipsCapture is { } lips)
        {
            _timeline.SetLips(actor, lips);
            remaining = remaining with
            {
                SelectedSlots = remaining.SelectedSlots.Without(AnimationSlot.Lips),
                LipsCapture = null,
                Lips = null,
            };
        }
        // A layer that was chosen but has no saved value to restore never
        // changed anything in the game, so reset can simply forget the choice.
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
        if (owned.PositionLock)
        {
            _stance.WritePositionLock(actor, false);
            remaining = remaining with { PositionLock = false };
        }

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

    /// <summary>Puts back every actor's animation when leaving GPose or unloading
    /// the plugin. Cleanup, not a command: it runs during a face capture too and
    /// makes no undo step.</summary>
    public Outcome ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _store.Actors())
        {
            var result = ResetActorUntracked(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        // Scene physics is not stored in any actor entry. Release it separately;
        // SetScenePhysicsFrozen retains the scene flag if that change fails.
        var scene = SetScenePhysicsFrozen(false);
        if (!scene.Success && scene.Detail is { } sceneDetail)
            failures.Add($"scene: {sceneDetail}");
        _general.Clear();
        return failures.Count == 0
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Releases the animation of actors that left the scene (despawned or
    /// redrawn as a new actor). Cleanup, not a command: it runs during a face
    /// capture too and makes no undo step.
    /// </summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(a => a.Id));
        _advancedActors.RemoveWhere(actor => !present.Contains(actor));
        foreach (var gone in _general.Keys.Where(id => !present.Contains(id)).ToList())
            _general.Remove(gone);
        _scrub.Forget(present);
        // Physics is left out on purpose: the scene holds the freeze, and the
        // scene cannot leave, so no actor leaving can end it.
        var departed = _store.Actors().Where(id => !present.Contains(id)).ToList();
        // An actor the game no longer has is forgotten; a restore that fails stays
        // recorded for a later reset.
        foreach (var id in departed)
            ResetActorUntracked(id);
    }
}
