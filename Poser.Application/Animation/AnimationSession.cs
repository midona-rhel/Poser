using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Animation;

public readonly record struct AnimationResult(bool Success, string? Detail = null)
{
    public static AnimationResult Ok() => new(true);
    public static AnimationResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// The single authority for Poser-owned animation state.
///
/// Keyed by exact-generation <see cref="ActorId"/>: a redraw or
/// replacement produces a new generation, so the old entry can never
/// govern the new actor. Nothing here holds an address, pointer, or
/// legacy entity — every native effect goes through
/// <see cref="IAnimationRuntimePort"/>.
///
/// RESTORATION CONTRACT. An actor's entry records only what Poser
/// authored, and each authored thing has exactly one undo:
///   base override   → the mode/mode-param/base-timeline captured before
///                     the FIRST override;
///   overall speed   → stop enforcing, so the game's own per-frame
///                     recalculation wins again (there is no remembered
///                     value to write back, by design);
///   slot speeds     → stop enforcing and hand each touched slot back;
///   slot timelines  → the timeline each slot held before Poser replaced
///                     it; also how a facial preview is removed without
///                     disturbing base or upper body;
///   lips            → the captured timeline (NOT 0, which merely means
///                     "no speech timeline");
///   stance and pose → the family and index captured before the first
///                     stance change;
///   weapon          → the drawn state captured before the first change;
///   position lock   → released;
///   physics         → released when the last owner lets go.
///
/// Every capture is taken once, before the first change of its kind, so
/// restore targets what Poser FOUND rather than an intermediate state it
/// created. Each aspect is released only when its own restore succeeded:
/// a failure on a still-live actor stays owned and the next Reset retries
/// it. An actor that no longer resolves is dropped without a native
/// write — there is nothing left to restore into.
///
/// Animation state is session-only: it is not transform history, not
/// pose-file payload, and not a named pose layer.
/// </summary>
public sealed class AnimationSession
{
    private readonly IAnimationRuntimePort _port;
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();
    private readonly Dictionary<ActorId, AnimationSelection> _selections = new();
    private readonly HashSet<ActorId> _physicsOwners = new();

    public AnimationSession(IAnimationRuntimePort port)
    {
        _port = port;
    }

    /// <summary>Raised after any owned state changes, so surfaces can
    /// re-read without polling.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<ActorId> OwnedActors => _overrides.Keys;

    public AnimationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var value) ? value : AnimationOverrides.None;

    public AnimationSelection SelectionFor(ActorId actor) =>
        _selections.TryGetValue(actor, out var value) ? value : AnimationSelection.Default;

    public void SetSelection(ActorId actor, AnimationSelection selection)
    {
        _selections[actor] = selection;
        Changed?.Invoke();
    }

    public ActorAnimationReading? Read(ActorId actor) => _port.Read(actor);

    /// <summary>
    /// True while a multi-phase operation owns the actor's animation — a
    /// facial bake between its capture and apply phases. Every command
    /// that could change what the face is doing is refused, because the
    /// captured values would then describe a face that no longer exists.
    /// Reads stay available so surfaces can keep rendering.
    /// </summary>
    public bool CommandsSuspended { get; private set; }

    public void SuspendCommands() { CommandsSuspended = true; Changed?.Invoke(); }
    public void ResumeCommands() { CommandsSuspended = false; Changed?.Invoke(); }

    private AnimationResult? Suspended() => CommandsSuspended
        ? AnimationResult.Fail("A face capture is in progress.")
        : null;

    public bool IsSupported(ActorId actor) => _port.IsSupported(actor);

    public bool IsPhysicsFrozen => _port.IsPhysicsFrozen;

    private AnimationOverrides Mutate(ActorId actor, Func<AnimationOverrides, AnimationOverrides> change)
    {
        var updated = change(OverridesFor(actor));
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
        return updated;
    }

    // ── Base and blend ────────────────────────────────────────────────

    /// <summary>
    /// Latches a base animation. The pre-override native state is captured
    /// on the first call only, so repeated base changes still restore to
    /// the state Poser found.
    /// </summary>
    public AnimationResult PlayBase(ActorId actor, ushort timeline, bool interrupt)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        var result = _port.ApplyBase(
            actor, timeline, interrupt, current.BaseCapture, out var captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base animation failed.");

        Mutate(actor, o => o with
        {
            BaseTimeline = timeline,
            BaseInterrupt = interrupt,
            BaseCapture = o.BaseCapture ?? captured,
        });
        return AnimationResult.Ok();
    }

    /// <summary>Blend rides the game's sequencer and owns nothing, so it
    /// records no override and needs no restoration.</summary>
    public AnimationResult Blend(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.Blend(actor, timeline);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Blend failed.");
    }

    public AnimationResult PlayEmote(ActorId actor, uint emoteId)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.PlayEmote(actor, emoteId);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Emote failed.");
    }

    /// <summary>Restores the captured base state and clears the selection;
    /// speed, lips, and the rest are untouched.</summary>
    public AnimationResult StopBase(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        if (current.BaseCapture is not { } capture)
        {
            Mutate(actor, o => o with { BaseTimeline = null });
            return AnimationResult.Ok();
        }

        var result = _port.RestoreBase(actor, capture);
        Mutate(actor, o => o with { BaseTimeline = null, BaseCapture = null });
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Base restore failed.");
    }

    /// <summary>
    /// Plays a catalog entry the way the references do, choosing the
    /// native route from the entry rather than from a UI flag:
    ///
    /// Base latches the timeline so the game re-drives it as the actor's
    /// idle; Blend hands the timeline to the sequencer, which picks the
    /// slot and performs the engine's own blend. An emote asked to play
    /// from the start goes through the game's emote entry point, the only
    /// route that plays intro-then-loop; anything else, and any emote
    /// that has no intro, falls back to the sequencer.
    ///
    /// Force loop is applied last so it wraps whichever route ran.
    /// </summary>
    public AnimationResult PlayEntry(
        ActorId actor, TimelineEntry entry, bool asBase, bool interrupt,
        bool playFromStart, bool forceLoop)
    {
        var timeline = (ushort)entry.TimelineId;
        AnimationResult result;

        if (asBase)
        {
            result = PlayBase(actor, timeline, interrupt);
        }
        else if (playFromStart && entry.CanPlayFromStart)
        {
            result = PlayEmote(actor, entry.EmoteId);
            if (!result.Success)
                result = Blend(actor, timeline);
        }
        else
        {
            result = Blend(actor, timeline);
        }

        if (!result.Success)
            return result;

        return forceLoop && _port.SupportsForceLoop
            ? SetForceLoop(actor, timeline)
            : AnimationResult.Ok();
    }

    /// <summary>False when the running client does not expose the game's
    /// forced-timeline field; surfaces hide the control rather than offer
    /// one that cannot work.</summary>
    public bool SupportsForceLoop => _port.SupportsForceLoop;

    /// <summary>
    /// Forces a timeline to repeat. Owns no state: on every client where
    /// <see cref="SupportsForceLoop"/> is false this cannot take effect,
    /// and recording an override for a write that did not happen would put
    /// a phantom entry into the restoration list.
    /// </summary>
    public AnimationResult SetForceLoop(ActorId actor, ushort timeline)
    {
        var result = _port.SetForceLoop(actor, timeline);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Loop failed.");
    }

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationResult SetSpeed(ActorId actor, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed failed.");
        Mutate(actor, o => o with { OverallSpeed = speed });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSpeed(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearOverallSpeed(actor);
        Mutate(actor, o => o with { OverallSpeed = null });
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Speed reset failed.");
    }

    public bool IsPaused(ActorId actor) => OverridesFor(actor).IsPaused;

    public AnimationResult Pause(ActorId actor) => SetSpeed(actor, 0f);

    /// <summary>Resume drops the override rather than writing 1, so an
    /// actor the game is driving at its own speed keeps it.</summary>
    public AnimationResult Resume(ActorId actor) => ClearSpeed(actor);

    public AnimationResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds) { [slot] = speed };
            return o with { SlotSpeeds = speeds };
        });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSlotSpeed(ActorId actor, AnimationSlot slot)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearSlotSpeed(actor, slot);
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
            speeds.Remove(slot);
            return o with { SlotSpeeds = speeds };
        });
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Slot speed reset failed.");
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    /// <summary>
    /// Sets the lip override. Selecting None (0) RESTORES the captured
    /// incoming timeline rather than writing 0: 0 means "no speech
    /// timeline", which is not necessarily what the actor arrived with,
    /// and writing it would discard the only record of that.
    /// </summary>
    public AnimationResult SetLips(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        ushort? capture = current.LipsCapture;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = reading.LipsOverride;

        bool clearing = timeline == 0;
        ushort target = clearing ? capture ?? 0 : timeline;

        var result = _port.SetLips(actor, target);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Lips failed.");

        Mutate(actor, o => o with
        {
            Lips = clearing ? null : timeline,
            // The capture is released only once it has been restored.
            LipsCapture = clearing ? null : (o.LipsCapture ?? capture),
        });
        return AnimationResult.Ok();
    }

    public AnimationResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        if (Suspended() is { } blocked) return blocked;
        var capture = OverridesFor(actor).StanceCaptureValue;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = new StanceCapture(reading.Stance, reading.Pose);

        var result = _port.SetStance(actor, stance, pose);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Stance failed.");
        Mutate(actor, o => o with { StanceCaptureValue = o.StanceCaptureValue ?? capture });
        return AnimationResult.Ok();
    }

    public AnimationResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        if (Suspended() is { } blocked) return blocked;
        var capture = OverridesFor(actor).WeaponCapture;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = reading.WeaponDrawn;

        var result = _port.SetWeaponDrawn(actor, drawn);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Weapon state failed.");
        Mutate(actor, o => o with { WeaponCapture = o.WeaponCapture ?? capture });
        return AnimationResult.Ok();
    }

    public AnimationResult SetPositionLock(ActorId actor, bool locked)
    {
        var result = _port.SetPositionLock(actor, locked);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Position lock failed.");
        Mutate(actor, o => o with { PositionLock = locked });
        return AnimationResult.Ok();
    }

    // ── Physics (global patch, reference-counted by actor) ────────────

    /// <summary>
    /// Physics is one global code patch shared by every actor, so the
    /// session reference-counts who asked for it. The ownership set is
    /// mutated ONLY after the patch itself succeeded: a failed patch that
    /// had already registered an owner would report the scene as frozen
    /// while it was still running, and the last release would then try to
    /// undo a patch that was never applied.
    /// </summary>
    public AnimationResult SetPhysicsFrozen(ActorId actor, bool frozen)
    {
        bool alreadyOwned = _physicsOwners.Contains(actor);
        if (frozen == alreadyOwned)
            return AnimationResult.Ok();

        int othersOwning = _physicsOwners.Count - (alreadyOwned ? 1 : 0);
        bool shouldFreeze = frozen || othersOwning > 0;

        if (shouldFreeze != _port.IsPhysicsFrozen)
        {
            var result = _port.SetPhysicsFrozen(shouldFreeze);
            if (!result.Success)
                return AnimationResult.Fail(result.Detail ?? "Physics freeze failed.");
        }

        if (frozen)
            _physicsOwners.Add(actor);
        else
            _physicsOwners.Remove(actor);
        Changed?.Invoke();
        return AnimationResult.Ok();
    }

    public bool OwnsPhysics(ActorId actor) => _physicsOwners.Contains(actor);

    // ── Scrubbing ─────────────────────────────────────────────────────

    /// <summary>
    /// One scrub drag. Everything that could move under the drag freezes
    /// at Begin: playback (so the game cannot advance the frame out from
    /// under the pointer), the control identity, its duration, and the
    /// skeleton token. Release leaves the actor paused on the frame the
    /// user chose — resuming is a separate, deliberate act.
    /// </summary>
    private sealed record ScrubGesture(
        ActorId Actor,
        ScrubControlId Control,
        float Duration,
        ulong Token,
        bool WasPaused);

    private ScrubGesture? _scrub;

    public bool IsScrubbing => _scrub != null;
    public float? ScrubDuration => _scrub?.Duration;

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token) =>
        _port.EnumerateControls(actor, out token);

    /// <summary>The control that drives a slot, by the reference lookup.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _port.FindSlotControl(actor, slot, out _);

    /// <summary>The actor whose scrub is in flight, if any. Surfaces must
    /// compare against this before feeding a slider value into an update:
    /// a value from a newly selected actor must never land in the previous
    /// actor's gesture.</summary>
    public ActorId? ScrubActor => _scrub?.Actor;
    public ScrubControlId? ScrubControl => _scrub?.Control;

    /// <summary>
    /// Freezes playback and captures the drag's whole mapping. Fails when
    /// the control is not present, so a scrub never starts against
    /// geometry that is already gone.
    /// </summary>
    public AnimationResult BeginScrub(ActorId actor, ScrubControlId control)
    {
        var controls = _port.EnumerateControls(actor, out var token);
        ScrubControlReading? target = null;
        foreach (var reading in controls)
            if (reading.Id == control)
                target = reading;
        if (target == null)
            return AnimationResult.Fail("That animation control is no longer present.");

        // A scrub in flight for a DIFFERENT actor ends here rather than
        // being silently retargeted.
        if (_scrub is { } existing && !existing.Actor.Equals(actor))
            EndScrub();

        bool wasPaused = IsPaused(actor);
        if (!wasPaused)
        {
            var freeze = SetSpeed(actor, 0f);
            if (!freeze.Success)
                return freeze;
        }

        _scrub = new ScrubGesture(actor, control, target.Duration, token, wasPaused);
        Changed?.Invoke();
        return AnimationResult.Ok();
    }

    /// <summary>
    /// Writes a frame within the drag, clamped to the duration CAPTURED
    /// at Begin rather than a freshly read one — a duration that changes
    /// mid-drag would otherwise stretch or jump the mapping. A skeleton
    /// token mismatch ends the drag instead of writing through whatever
    /// now occupies that control position.
    /// </summary>
    public AnimationResult UpdateScrub(float time)
    {
        if (_scrub is not { } gesture)
            return AnimationResult.Fail("No scrub is active.");
        if (!float.IsFinite(time))
            return AnimationResult.Fail("Scrub time must be a finite number.");

        float clamped = Math.Clamp(time, 0f, gesture.Duration);
        var result = _port.SetControlTime(
            gesture.Actor, gesture.Control, clamped, gesture.Token);
        if (result.Success)
            return AnimationResult.Ok();

        _scrub = null;
        Changed?.Invoke();
        return AnimationResult.Fail(result.Detail ?? "Scrub cancelled.");
    }

    /// <summary>Ends the drag, leaving the actor paused on the released
    /// frame. That pause is an ordinary speed override, so Resume
    /// continues from exactly there.</summary>
    public void EndScrub()
    {
        _scrub = null;
        Changed?.Invoke();
    }

    // ── Slot replacement ──────────────────────────────────────────────

    /// <summary>
    /// Replaces one slot's timeline, leaving every other slot and every
    /// other override untouched. The slot's INCOMING timeline is captured
    /// on the first replacement, which is both the restore point and —
    /// for the Facial slot — the means of removing a preview without
    /// disturbing base or upper body.
    /// </summary>
    public AnimationResult SetSlotTimeline(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        ushort? capture = current.SlotTimelineCaptures.TryGetValue(slot, out var existing)
            ? existing
            : _port.Read(actor)?.TimelineFor(slot);

        var result = _port.SetSlotTimeline(actor, slot, timeline);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot playback failed.");

        if (capture is { } captured)
        {
            Mutate(actor, o =>
            {
                if (o.SlotTimelineCaptures.ContainsKey(slot))
                    return o;
                var captures = new Dictionary<AnimationSlot, ushort>(o.SlotTimelineCaptures)
                {
                    [slot] = captured,
                };
                return o with { SlotTimelineCaptures = captures };
            });
        }
        return AnimationResult.Ok();
    }

    /// <summary>The timeline a slot held before Poser first replaced it,
    /// if it has.</summary>
    public ushort? CapturedSlotTimeline(ActorId actor, AnimationSlot slot) =>
        OverridesFor(actor).SlotTimelineCaptures.TryGetValue(slot, out var value)
            ? value
            : null;

    /// <summary>
    /// Puts one slot back to its captured incoming timeline and releases
    /// the capture. This is how a facial preview is removed: it touches
    /// exactly that slot, so base and upper body keep playing.
    /// </summary>
    public AnimationResult RestoreSlotTimeline(ActorId actor, AnimationSlot slot)
    {
        if (CapturedSlotTimeline(actor, slot) is not { } captured)
            return AnimationResult.Ok();

        var result = _port.SetSlotTimeline(actor, slot, captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot restore failed.");

        Mutate(actor, o =>
        {
            var captures = new Dictionary<AnimationSlot, ushort>(o.SlotTimelineCaptures);
            captures.Remove(slot);
            return o with { SlotTimelineCaptures = captures };
        });
        return AnimationResult.Ok();
    }

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>
    /// Restores every override Poser owns for one actor and forgets it.
    /// Safe to call when nothing is owned. Individual failures are
    /// aggregated so one unreachable write cannot strand the rest.
    /// </summary>
    public AnimationResult ResetActor(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        if (!_overrides.TryGetValue(actor, out var owned))
        {
            if (_physicsOwners.Remove(actor))
                ReleasePhysicsIfUnowned();
            _selections.Remove(actor);
            return AnimationResult.Ok();
        }

        // Each aspect is released ONLY when its restore succeeded. What
        // fails stays owned, so a later Reset retries it instead of the
        // override being silently abandoned on a still-live actor. If the
        // actor no longer resolves there is nothing left to restore into,
        // and everything is dropped.
        var failures = new List<string>();
        var remaining = owned;
        bool actorGone = !_port.IsSupported(actor) && _port.Read(actor) == null;

        bool Try(AnimationPortResult result)
        {
            if (result.Success)
                return true;
            if (result.Detail is { } detail)
                failures.Add(detail);
            return false;
        }

        if (owned.BaseCapture is { } capture && Try(_port.RestoreBase(actor, capture)))
            remaining = remaining with { BaseCapture = null, BaseTimeline = null };
        if (owned.OverallSpeed != null && Try(_port.ClearOverallSpeed(actor)))
            remaining = remaining with { OverallSpeed = null };

        if (owned.SlotSpeeds.Count > 0)
        {
            var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
            foreach (var slot in owned.SlotSpeeds.Keys.ToList())
                if (Try(_port.ClearSlotSpeed(actor, slot)))
                    speeds.Remove(slot);
            remaining = remaining with { SlotSpeeds = speeds };
        }

        if (owned.SlotTimelineCaptures.Count > 0)
        {
            var captures = new Dictionary<AnimationSlot, ushort>(remaining.SlotTimelineCaptures);
            foreach (var (slot, timeline) in owned.SlotTimelineCaptures)
                if (Try(_port.SetSlotTimeline(actor, slot, timeline)))
                    captures.Remove(slot);
            remaining = remaining with { SlotTimelineCaptures = captures };
        }

        if (owned.StanceCaptureValue is { } stance &&
            Try(_port.SetStance(actor, stance.Stance, stance.Pose)))
            remaining = remaining with { StanceCaptureValue = null };
        if (owned.WeaponCapture is { } weapon &&
            Try(_port.SetWeaponDrawn(actor, weapon)))
            remaining = remaining with { WeaponCapture = null };
        if (owned.LipsCapture is { } lips && Try(_port.SetLips(actor, lips)))
            remaining = remaining with { LipsCapture = null, Lips = null };
        if (owned.PositionLock && Try(_port.SetPositionLock(actor, false)))
            remaining = remaining with { PositionLock = false };

        if (actorGone || !remaining.HasAny)
        {
            _overrides.Remove(actor);
            _selections.Remove(actor);
        }
        else
        {
            _overrides[actor] = remaining;
        }

        if (_physicsOwners.Remove(actor))
            ReleasePhysicsIfUnowned();
        Changed?.Invoke();

        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>Restores every owned actor. Used by GPose exit, plugin
    /// disposal, and Stop/Restore All.</summary>
    public AnimationResult ResetAll()
    {
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        _physicsOwners.Clear();
        ReleasePhysicsIfUnowned();
        _selections.Clear();
        Changed?.Invoke();
        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
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
        var departed = _overrides.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in _selections.Keys.Where(id => !present.Contains(id)).ToList())
            _selections.Remove(id);

        if (departed.Count == 0)
        {
            if (_physicsOwners.RemoveWhere(id => !present.Contains(id)) > 0)
                ReleasePhysicsIfUnowned();
            return;
        }

        foreach (var id in departed)
        {
            // Attempt the native restore; an actor that no longer resolves
            // simply has nothing left to restore into, and the entry is
            // dropped either way so it can never be re-applied.
            ResetActor(id);
        }
    }

    private void ReleasePhysicsIfUnowned()
    {
        if (_physicsOwners.Count == 0 && _port.IsPhysicsFrozen)
            _port.SetPhysicsFrozen(false);
    }
}
