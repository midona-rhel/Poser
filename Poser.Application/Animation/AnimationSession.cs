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
///   base override  → the mode/mode-param/base-timeline captured before
///                    the FIRST override, replayed once, then dropped;
///   overall speed  → stop enforcing, so the game's own per-frame
///                    recalculation wins again (there is no remembered
///                    value to write back, by design);
///   slot speeds    → stop enforcing and hand each touched slot back;
///   lips           → the timeline captured before the first override;
///   force loop     → cleared to 0;
///   position lock  → released;
///   physics        → released when the last owner lets go.
/// Restore runs exactly once per entry because the entry is removed in
/// the same step. Actors that no longer resolve are dropped without a
/// native write — there is nothing left to restore into.
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
        var result = _port.Blend(actor, timeline);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Blend failed.");
    }

    public AnimationResult PlayEmote(ActorId actor, uint emoteId)
    {
        var result = _port.PlayEmote(actor, emoteId);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Emote failed.");
    }

    /// <summary>Restores the captured base state and clears the selection;
    /// speed, lips, and the rest are untouched.</summary>
    public AnimationResult StopBase(ActorId actor)
    {
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

        return forceLoop
            ? SetForceLoop(actor, timeline)
            : AnimationResult.Ok();
    }

    public AnimationResult SetForceLoop(ActorId actor, ushort timeline)
    {
        var result = _port.SetForceLoop(actor, timeline);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Loop failed.");
        Mutate(actor, o => o with { ForceLoop = timeline == 0 ? null : timeline });
        return AnimationResult.Ok();
    }

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationResult SetSpeed(ActorId actor, float speed)
    {
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed failed.");
        Mutate(actor, o => o with { OverallSpeed = speed });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSpeed(ActorId actor)
    {
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

    public AnimationResult SetLips(ActorId actor, ushort timeline)
    {
        var current = OverridesFor(actor);
        ushort? capture = current.LipsCapture;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = reading.LipsOverride;

        var result = _port.SetLips(actor, timeline);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Lips failed.");

        Mutate(actor, o => o with
        {
            Lips = timeline == 0 ? null : timeline,
            // Keep the capture while an override is live so clearing it
            // later still knows what the actor arrived with.
            LipsCapture = timeline == 0 ? null : (o.LipsCapture ?? capture),
        });
        return AnimationResult.Ok();
    }

    public AnimationResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        var result = _port.SetStance(actor, stance, pose);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Stance failed.");
    }

    public AnimationResult SetWeaponDrawn(ActorId actor, bool drawn)
    {
        var result = _port.SetWeaponDrawn(actor, drawn);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Weapon state failed.");
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

    public AnimationResult SetPhysicsFrozen(ActorId actor, bool frozen)
    {
        if (frozen)
            _physicsOwners.Add(actor);
        else
            _physicsOwners.Remove(actor);

        bool shouldFreeze = _physicsOwners.Count > 0;
        if (shouldFreeze == _port.IsPhysicsFrozen)
        {
            Changed?.Invoke();
            return AnimationResult.Ok();
        }

        var result = _port.SetPhysicsFrozen(shouldFreeze);
        Changed?.Invoke();
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Physics freeze failed.");
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

    /// <summary>Replaces one slot's timeline, leaving every other slot and
    /// every other override untouched.</summary>
    public AnimationResult SetSlotTimeline(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        var result = _port.SetSlotTimeline(actor, slot, timeline);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Slot playback failed.");
    }

    // ── Restoration ───────────────────────────────────────────────────

    /// <summary>
    /// Restores every override Poser owns for one actor and forgets it.
    /// Safe to call when nothing is owned. Individual failures are
    /// aggregated so one unreachable write cannot strand the rest.
    /// </summary>
    public AnimationResult ResetActor(ActorId actor)
    {
        if (!_overrides.TryGetValue(actor, out var owned))
        {
            if (_physicsOwners.Remove(actor))
                ReleasePhysicsIfUnowned();
            _selections.Remove(actor);
            return AnimationResult.Ok();
        }

        var failures = new List<string>();
        void Record(AnimationPortResult result)
        {
            if (!result.Success && result.Detail is { } detail)
                failures.Add(detail);
        }

        if (owned.BaseCapture is { } capture)
            Record(_port.RestoreBase(actor, capture));
        if (owned.ForceLoop != null)
            Record(_port.SetForceLoop(actor, 0));
        if (owned.OverallSpeed != null)
            Record(_port.ClearOverallSpeed(actor));
        foreach (var slot in owned.SlotSpeeds.Keys.ToList())
            Record(_port.ClearSlotSpeed(actor, slot));
        if (owned.LipsCapture is { } lips)
            Record(_port.SetLips(actor, lips));
        if (owned.PositionLock)
            Record(_port.SetPositionLock(actor, false));

        _overrides.Remove(actor);
        _selections.Remove(actor);
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
