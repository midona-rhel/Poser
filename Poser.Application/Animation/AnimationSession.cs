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
    }

    public AnimationOverrides OverridesFor(ActorId actor) => _store.For(actor);

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

    /// <summary>Stages a selection without reading or writing native state.</summary>
    public Outcome ChooseSlot(ActorId actor, AnimationSlot slot, ushort timeline) =>
        _base.ChooseSlot(actor, slot, timeline);

    /// <summary>Sets repeat intent for one slot.</summary>
    public Outcome SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        _base.SetSlotLoop(actor, slot, timeline, on);

    /// <summary>Whether full-body repeat is available.</summary>
    public bool SupportsForceLoop => _base.SupportsForceLoop;

    /// <summary>False when the client's stance-transition functions were
    /// not found; the stance controls render disabled.</summary>
    public bool SupportsStance => _stance.SupportsStance;

    // ── Speed ─────────────────────────────────────────────────────────

    public Outcome SetSpeed(ActorId actor, float speed) => _speed.SetSpeed(actor, speed);

    public Outcome ClearSpeed(ActorId actor) => _speed.ClearSpeed(actor);

    public bool IsPaused(ActorId actor) => _speed.IsPaused(actor);

    public Outcome Pause(ActorId actor) => _speed.Pause(actor);

    /// <summary>Play-all: releases every hold.</summary>
    public Outcome Resume(ActorId actor) => _speed.Resume(actor);

    /// <summary>Whether any live layer is actually moving.</summary>
    public bool AnyPlaying(ActorId actor) => _speed.AnyPlaying(actor);

    /// <summary>
    /// Replays from the start after releasing a Poser-owned pause. A nonzero
    /// owned speed remains active. <paramref name="resumed"/> reports whether
    /// the pause was released.
    /// </summary>
    public Outcome Replay(ActorId actor, ushort timeline, out bool resumed)
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

    public Outcome SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed) =>
        _speed.SetSlotSpeed(actor, slot, speed);

    public Outcome ClearSlotSpeed(ActorId actor, AnimationSlot slot) =>
        _speed.ClearSlotSpeed(actor, slot);

    public Outcome PauseSlot(ActorId actor, AnimationSlot slot) =>
        _speed.PauseSlot(actor, slot);

    /// <summary>Applies Selected; only Base may use the emote lifecycle.</summary>
    /// <summary>APPLY stages, PLAY plays (ruled 2026-09-01): with
    /// <paramref name="resume"/> false, a paused actor takes the animation
    /// frozen at its start and nothing moves — the layer's Play button (or
    /// the sidebar's play-all) is what starts it.</summary>
    public Outcome PlaySelectedSlot(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume = true)
    {
        var outcome = PlaySelectedSlotTraced(actor, slot, entry, playFromStart, resume);
        Trace?.Invoke(outcome.Success
            ? $"  PlaySelectedSlot {slot} -> ok"
            : $"  PlaySelectedSlot {slot} -> FAIL: {outcome.Detail}");
        return outcome;
    }

    private Outcome PlaySelectedSlotTraced(
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
    public Outcome ResetSlot(ActorId actor, AnimationSlot slot)
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
                return _expression.ReleaseExpressionCore(actor);
        }

        var failures = new List<string>();
        // Restore speed first. A failed unpin must not clear a selection
        // whose paused native state still belongs to Poser. A ZERO speed
        // is a pause, and resets never unpause — only the play verbs do
        // (ruled 2026-09-01); the hold stays parked.
        var beforeReset = OverridesFor(actor);
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
    public Outcome SetLips(ActorId actor, ushort timeline) => _base.SetLips(actor, timeline);

    public Outcome SetStance(ActorId actor, AnimationStance stance, int pose) =>
        _stance.SetStance(actor, stance, pose);

    public Outcome SetWeaponDrawn(ActorId actor, bool drawn) =>
        _stance.SetWeaponDrawn(actor, drawn);

    public Outcome SetPositionLock(ActorId actor, bool locked) =>
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
    public Outcome BeginScrub(ActorId actor, ScrubControlId control, Guid owner) =>
        _scrub.BeginScrub(actor, control, owner);

    /// <summary>Writes a frame clamped to the duration captured at Begin.</summary>
    public Outcome UpdateScrub(ActorId actor, float time, Guid owner) =>
        _scrub.UpdateScrub(actor, time, owner);

    /// <summary>Ends the drag, leaving the actor paused on the released frame.</summary>
    public void EndScrub(Guid owner) => _scrub.EndScrub(owner);

    // ── Held expression ──────────────────────────────────────────────────

    /// <summary>Applies an expression and immediately pins its facial frame.</summary>
    public Outcome HoldExpression(ActorId actor, ushort timeline) =>
        _expression.HoldExpression(actor, timeline);

    /// <summary>Releases a held facial expression.</summary>
    public Outcome ReleaseExpression(ActorId actor) => _expression.ReleaseExpression(actor);

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
    public Outcome ResetActor(ActorId actor)
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

        // Each aspect is released only when its restore succeeded. What
        // fails stays owned, so a later Reset retries it instead of the
        // override being silently abandoned on a still-live actor. If the
        // actor no longer resolves there is nothing left to restore into,
        // and everything is dropped.
        var failures = new List<string>();
        var remaining = owned;
        bool actorGone = !_timeline.IsSupported(actor) && _timeline.Read(actor) == null;

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
                if (liveRead != null)
                    foreach (var slotReading in liveRead.Slots)
                        if (slotReading.Slot != AnimationSlot.Base &&
                            slotReading.TimelineId != 0 &&
                            !slots.ContainsKey(slotReading.Slot))
                            slots[slotReading.Slot] = slotReading.TimelineId;
                cancelled = Try(_timeline.CancelActiveTimeline(actor));
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

        if (actorGone || !remaining.HasAny)
        {
            if (actorGone)
                _timeline.ClearLoops(actor);
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
