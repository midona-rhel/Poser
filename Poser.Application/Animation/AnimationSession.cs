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
///   held expression → released (unpin facial, Straight face, idle);
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
    private int _probeDepth;

    /// <summary>Tracks the scene physics hold.</summary>
    private bool _sceneOwnsPhysics;

    public AnimationSession(IAnimationRuntimePort port)
    {
        _port = port;
    }

    public IReadOnlyCollection<ActorId> OwnedActors => _overrides.Keys;

    public AnimationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var value) ? value : AnimationOverrides.None;

    public bool LoopWantedFor(ActorId actor, AnimationSlot slot) =>
        OverridesFor(actor).LoopWantedSlots.Contains(slot);

    public ActorAnimationReading? Read(ActorId actor) => _port.Read(actor);

    /// <summary>
    /// True while a multi-phase operation owns the actor's animation — a
    /// facial bake between its capture and apply phases. Every command
    /// that could change what the face is doing is refused, because the
    /// captured values would then describe a face that no longer exists.
    /// Reads stay available so surfaces can keep rendering.
    /// </summary>
    public bool CommandsSuspended { get; private set; }

    public void SuspendCommands()
    {
        CommandsSuspended = true;
        // Armed loops would replay animations into the settling baseline.
        _port.LoopsSuspended = true;
    }

    public void ResumeCommands()
    {
        CommandsSuspended = false;
        _port.LoopsSuspended = false;
    }

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
        return updated;
    }

    public AnimationResult StartSlotProbe(ActorId actor)
    {
        var result = _port.StartSlotProbe(actor);
        return result.Success
            ? new AnimationResult(true, result.Detail)
            : AnimationResult.Fail(result.Detail ?? "Slot probe failed.");
    }

    public AnimationResult StopSlotProbe(ActorId actor)
    {
        var result = _port.StopSlotProbe(actor);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Slot probe stop failed.");
    }

    private AnimationResult ObserveProbe(
        ActorId actor,
        AnimationProbeCommand command,
        Func<AnimationResult> action)
    {
        if (_probeDepth > 0)
            return action();
        _probeDepth++;
        _port.BeginSlotProbeCommand(actor, command);
        try
        {
            var result = action();
            _port.CompleteSlotProbeCommand(actor, command, result.Success);
            return result;
        }
        finally
        {
            _probeDepth--;
        }
    }

    // ── Base and blend ────────────────────────────────────────────────

    /// <summary>Plays the actor's full-body timeline.</summary>
    public AnimationResult PlayBase(ActorId actor, ushort timeline)
    {
        var before = OverridesFor(actor);
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("selection", AnimationSlot.Base, timeline),
            () => PlayBaseCore(
                actor, timeline, before, before.LoopWantedSlots.Contains(AnimationSlot.Base)));
    }

    private AnimationResult PlayBaseCore(
        ActorId actor, ushort timeline, AnimationOverrides before, bool loopWanted)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.PlayBase(actor, timeline, before.BaseCapture, out var captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base playback failed.");
        bool armRepeat = loopWanted && !HasLayerSelection(before);
        if (armRepeat)
        {
            var armed = _port.SetForceLoop(actor, timeline);
            if (!armed.Success)
                return AnimationResult.Fail(armed.Detail ?? "Repeat arm failed.");
        }
        if (captured is { } taken)
            Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            if (armRepeat)
                loops[AnimationSlot.Base] = timeline;
            else
                loops.Remove(AnimationSlot.Base);
            return o with
            {
                BaseTimeline = timeline,
                LoopedSlots = loops,
                BaseRepeatSuspended = loopWanted && !armRepeat,
            };
        });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a timeline through its native route.</summary>
    public AnimationResult Blend(ActorId actor, ushort timeline)
    {
        var landing = _port.TimelineSlot(timeline);
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("selection", landing, timeline),
            () => BlendCore(actor, timeline, landing));
    }

    private AnimationResult BlendCore(
        ActorId actor, ushort timeline, AnimationSlot? landing)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        bool suspendBaseRepeat = landing is { } target && target != AnimationSlot.Base &&
            current.LoopedSlots.ContainsKey(AnimationSlot.Base);

        // Capture each non-base slot before Poser changes it.
        bool captureSlot = landing is { } slot &&
            slot != AnimationSlot.Base &&
            !current.SlotCaptures.ContainsKey(slot);
        ushort incoming = 0;
        if (captureSlot && _port.Read(actor) is { } reading)
            incoming = reading.TimelineFor(landing!.Value);

        var result = _port.Blend(actor, timeline, current.BaseCapture, out var captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Blend failed.");
        if (captured is { } taken)
            Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        if (captureSlot)
        {
            var landed = landing!.Value;
            Mutate(actor, o =>
            {
                if (o.SlotCaptures.ContainsKey(landed))
                    return o;
                var slots = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures)
                {
                    [landed] = incoming,
                };
                return o with { SlotCaptures = slots };
            });
        }
        if (suspendBaseRepeat)
            Mutate(actor, o =>
            {
                var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                loops.Remove(AnimationSlot.Base);
                return o with { LoopedSlots = loops, BaseRepeatSuspended = true };
            });
        return AnimationResult.Ok();
    }

    private static bool HasLayerSelection(AnimationOverrides overrides) =>
        overrides.SlotCaptures.Keys.Any(slot => slot != AnimationSlot.Base);

    public AnimationResult PlayEmote(ActorId actor, uint emoteId)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("emote", AnimationSlot.Base),
            () => PlayEmoteCore(actor, emoteId));
    }

    private AnimationResult PlayEmoteCore(ActorId actor, uint emoteId)
    {
        if (Suspended() is { } blocked) return blocked;
        // The emote entry point drives the base slot too; its restore
        // point is captured exactly as a direct play's would be.
        var current = OverridesFor(actor);
        var captured = current.BaseCapture == null ? _port.CaptureBase(actor) : null;
        var result = _port.PlayEmote(actor, emoteId);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Emote failed.");
        if (captured is { } taken)
            Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        return AnimationResult.Ok();
    }

    /// <summary>Restores the captured base state.</summary>
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
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base restore failed.");
        // Clear ownership after restoration succeeds.
        Mutate(actor, o => o with { BaseTimeline = null, BaseCapture = null });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a catalog entry through its native route.</summary>
    public AnimationResult PlayEntry(
        ActorId actor, TimelineEntry entry, bool asBase, bool playFromStart)
    {
        var timeline = (ushort)entry.TimelineId;
        if (asBase)
            return PlayBase(actor, timeline);
        if (playFromStart && entry.CanPlayFromStart)
        {
            var result = PlayEmote(actor, entry.EmoteId);
            if (result.Success)
                return result;
        }
        return Blend(actor, timeline);
    }

    /// <summary>Sets repeat intent for one slot.</summary>
    public AnimationResult SetSlotLoop(ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("slot-loop", slot, timeline, on),
            () => SetSlotLoopCore(actor, slot, timeline, on));
    }

    private AnimationResult SetSlotLoopCore(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        if (Suspended() is { } blocked) return blocked;
        if (slot != AnimationSlot.Base)
            return AnimationResult.Fail(
                "Repeat is unavailable for this layer: exact replay is unverified.");
        if (!SupportsForceLoop)
            return AnimationResult.Fail("Full-body repeat is unavailable for this client layout.");
        var current = OverridesFor(actor);
        if (!on && current.LoopedSlots.ContainsKey(AnimationSlot.Base))
        {
            var cleared = _port.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return AnimationResult.Fail(cleared.Detail ?? "Repeat clear failed.");
        }
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            var wanted = new HashSet<AnimationSlot>(o.LoopWantedSlots);
            if (on)
                wanted.Add(slot);
            else
            {
                loops.Remove(slot);
                wanted.Remove(slot);
            }
            return o with
            {
                LoopedSlots = loops,
                LoopWantedSlots = wanted,
                BaseRepeatSuspended = on ? o.BaseRepeatSuspended : false,
            };
        });
        if (!on)
            return AnimationResult.Ok();

        ushort target = timeline != 0
            ? timeline
            : current.BaseTimeline ?? _port.Read(actor)?.TimelineFor(AnimationSlot.Base) ?? 0;
        if (target == 0)
            return AnimationResult.Ok();
        var captured = current.BaseCapture == null ? _port.CaptureBase(actor) : null;
        if (current.BaseRepeatSuspended || HasLayerSelection(current))
        {
            Mutate(actor, o => o with
            {
                BaseCapture = o.BaseCapture ?? captured,
                BaseRepeatSuspended = true,
            });
            return AnimationResult.Ok();
        }
        var armed = _port.SetForceLoop(actor, target);
        if (!armed.Success)
            return AnimationResult.Fail(armed.Detail ?? "Repeat arm failed.");
        Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            BaseRepeatSuspended = false,
            LoopedSlots = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
            {
                [AnimationSlot.Base] = target,
            },
        });
        return AnimationResult.Ok();
    }

    /// <summary>Whether full-body repeat is available.</summary>
    public bool SupportsForceLoop => _port.SupportsForceLoop;

    /// <summary>False when the client's stance-transition functions were
    /// not found; the stance controls render disabled.</summary>
    public bool SupportsStance => _port.SupportsStance;

    // ── Speed ─────────────────────────────────────────────────────────

    public AnimationResult SetSpeed(ActorId actor, float speed)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("overall-speed"),
            () => SetSpeedCore(actor, speed));
    }

    private AnimationResult SetSpeedCore(ActorId actor, float speed)
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
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("overall-speed-clear"),
            () => ClearSpeedCore(actor));
    }

    private AnimationResult ClearSpeedCore(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearOverallSpeed(actor);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed reset failed.");
        Mutate(actor, o => o with { OverallSpeed = null });
        return AnimationResult.Ok();
    }

    public bool IsPaused(ActorId actor) => OverridesFor(actor).IsPaused;

    public AnimationResult Pause(ActorId actor) => ObserveProbe(
        actor, new AnimationProbeCommand("pause"), () => SetSpeed(actor, 0f));

    /// <summary>Resume drops the override rather than writing 1, so an
    /// actor the game is driving at its own speed keeps it.</summary>
    public AnimationResult Resume(ActorId actor) => ObserveProbe(
        actor, new AnimationProbeCommand("resume"), () => ClearSpeed(actor));

    /// <summary>
    /// Replays a timeline from the start. Replay is explicitly a RESUMING
    /// act: a Poser-owned pause (zero speed) is released first, because a
    /// replay that kept the zero-speed owner would freeze the very
    /// animation it claims to restart and leave Poser owning a pause the
    /// user asked to play through. A non-zero owned speed survives — the
    /// user's chosen rate applies to the replayed timeline. A failed
    /// release keeps the pause owner and plays nothing, so ownership
    /// stays truthful. <paramref name="resumed"/> reports whether a pause
    /// was released so surfaces can SAY which semantic ran.
    /// </summary>
    public AnimationResult Replay(ActorId actor, ushort timeline, out bool resumed)
    {
        resumed = false;
        if (Suspended() is { } blocked) return blocked;
        if (IsPaused(actor))
        {
            var released = ClearSpeed(actor);
            if (!released.Success)
                return released;
            resumed = true;
        }
        return Blend(actor, timeline);
    }

    /// <summary>Rewinds paused animation controls.</summary>
    public AnimationResult RewindPausedControls(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.RewindPausedControls(actor);
        return result.Success
            ? AnimationResult.Ok()
            : AnimationResult.Fail(result.Detail ?? "Rewind failed.");
    }

    public AnimationResult SetSlotSpeed(ActorId actor, AnimationSlot slot, float speed)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("slot-speed", slot),
            () => SetSlotSpeedCore(actor, slot, speed));
    }

    private AnimationResult SetSlotSpeedCore(
        ActorId actor, AnimationSlot slot, float speed)
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
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("slot-speed-clear", slot),
            () => ClearSlotSpeedCore(actor, slot));
    }

    private AnimationResult ClearSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.ClearSlotSpeed(actor, slot);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed reset failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
            speeds.Remove(slot);
            return o with { SlotSpeeds = speeds };
        });
        return AnimationResult.Ok();
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

        // A stance change releases full-body repeat first.
        var owned = OverridesFor(actor);
        if (owned.LoopedSlots.Count > 0 || owned.LoopWantedSlots.Count > 0)
        {
            if (owned.LoopedSlots.ContainsKey(AnimationSlot.Base))
            {
                var cleared = _port.SetForceLoop(actor, 0);
                if (!cleared.Success)
                    return AnimationResult.Fail(cleared.Detail ?? "Repeat clear failed.");
            }
            _port.ClearLoops(actor);
            Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                LoopWantedSlots = new HashSet<AnimationSlot>(),
                BaseRepeatSuspended = false,
            });
            owned = OverridesFor(actor);
        }
        if (owned.BaseCapture != null || owned.BaseTimeline != null)
        {
            var released = StopBase(actor);
            if (!released.Success)
                return released;
        }

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

    // ── Physics (one global patch, held by the scene) ─────────────────

    /// <summary>
    /// The scene's request for the global freeze — the shell's physics
    /// switch, which stands over every selection and over none. The hold is
    /// recorded ONLY after the patch it implies has actually landed: a
    /// failed patch that had already recorded the hold would report the
    /// scene as frozen while it was still running, and the release would
    /// then try to undo a patch that was never applied.
    /// </summary>
    public AnimationResult SetScenePhysicsFrozen(bool frozen)
    {
        if (frozen == _sceneOwnsPhysics)
            return AnimationResult.Ok();

        if (frozen != _port.IsPhysicsFrozen)
        {
            var result = _port.SetPhysicsFrozen(frozen);
            if (!result.Success)
                // The fallback names the DIRECTION that failed: this call
                // both patches and unpatches, and "freeze failed" on a
                // release is a report of the opposite of what was attempted.
                return AnimationResult.Fail(
                    result.Detail ?? (frozen
                        ? "Physics freeze failed."
                        : "Physics release failed."));
        }

        _sceneOwnsPhysics = frozen;
        return AnimationResult.Ok();
    }

    /// <summary>Whether the scene holds the patch — distinct from
    /// <see cref="IsPhysicsFrozen"/>, which is the global state however it
    /// came to be true.</summary>
    public bool SceneOwnsPhysics => _sceneOwnsPhysics;

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

    /// <summary>Gets the control for a slot.</summary>
    public ScrubControlReading? FindSlotControl(ActorId actor, AnimationSlot slot) =>
        _port.FindSlotControl(actor, slot, out _);

    /// <summary>
    /// Freezes playback and captures the drag's whole mapping. Fails when
    /// the control is not present, so a scrub never starts against
    /// geometry that is already gone.
    /// </summary>
    public AnimationResult BeginScrub(ActorId actor, ScrubControlId control)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("scrub-start"),
            () => BeginScrubCore(actor, control));
    }

    private AnimationResult BeginScrubCore(ActorId actor, ScrubControlId control)
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
        return AnimationResult.Ok();
    }

    /// <summary>
    /// Writes a frame within the drag, clamped to the duration CAPTURED
    /// at Begin rather than a freshly read one — a duration that changes
    /// mid-drag would otherwise stretch or jump the mapping. A skeleton
    /// token mismatch ends the drag instead of writing through whatever
    /// now occupies that control position. The update names its actor and
    /// a mismatch with the gesture's actor is refused inside the session:
    /// a value from a newly selected actor can never land in the previous
    /// actor's gesture.
    /// </summary>
    public AnimationResult UpdateScrub(ActorId actor, float time)
    {
        return ObserveProbe(
            actor,
            new AnimationProbeCommand("scrub-update"),
            () => UpdateScrubCore(actor, time));
    }

    private AnimationResult UpdateScrubCore(ActorId actor, float time)
    {
        if (_scrub is not { } gesture)
            return AnimationResult.Fail("No scrub is active.");
        if (!gesture.Actor.Equals(actor))
            return AnimationResult.Fail(
                "The scrub in flight belongs to a different actor.");
        if (!float.IsFinite(time))
            return AnimationResult.Fail("Scrub time must be a finite number.");

        float clamped = Math.Clamp(time, 0f, gesture.Duration);
        var result = _port.SetControlTime(
            gesture.Actor, gesture.Control, clamped, gesture.Token);
        if (result.Success)
            return AnimationResult.Ok();

        _scrub = null;
        return AnimationResult.Fail(result.Detail ?? "Scrub cancelled.");
    }

    /// <summary>Ends the drag, leaving the actor paused on the released
    /// frame. That pause is an ordinary speed override, so Resume
    /// continues from exactly there.</summary>
    public void EndScrub()
    {
        if (_scrub is not { } gesture)
            return;
        ObserveProbe(
            gesture.Actor,
            new AnimationProbeCommand("scrub-end"),
            () =>
            {
                _scrub = null;
                return AnimationResult.Ok();
            });
    }

    // ── Expression hold ──────────────────────────────────────────────────

    /// <summary>Holds a facial expression.</summary>
    public AnimationResult HoldExpression(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var played = Blend(actor, timeline);
        if (!played.Success)
            return played;
        var pinned = SetSlotSpeed(actor, AnimationSlot.Facial, 0f);
        if (!pinned.Success)
            return pinned;
        Mutate(actor, o => o with { HeldExpression = timeline });
        return AnimationResult.Ok();
    }

    /// <summary>Releases a held facial expression.</summary>
    public AnimationResult ReleaseExpression(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        var unpin = ClearSlotSpeed(actor, AnimationSlot.Facial);
        var straight = Blend(actor, AnimationTimelines.StraightFace);
        var again = ClearSlotSpeed(actor, AnimationSlot.Facial);
        var idle = Blend(actor, AnimationTimelines.Idle);
        if (!unpin.Success || !straight.Success || !again.Success || !idle.Success)
        {
            // The face is still (partly) held; keeping HeldExpression is
            // what lets the next release or reset retry the whole
            // sequence instead of stranding a pinned layer.
            return AnimationResult.Fail(
                unpin.Detail ?? straight.Detail ?? again.Detail ?? idle.Detail ??
                "Expression release failed.");
        }
        Mutate(actor, o => o with { HeldExpression = null });
        return AnimationResult.Ok();
    }

    /// <summary>Restores the captured facial layer.</summary>
    public AnimationResult RestoreFacialLayer(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        // The KEY, not its value, is the record that Poser played here at all:
        // no entry means there is nothing of Poser's on this layer to take
        // off, while an entry of 0 means Poser played over a layer that was
        // showing nothing.
        bool played = OverridesFor(actor)
            .SlotCaptures.TryGetValue(AnimationSlot.Facial, out var captured);

        var unpin = ClearSlotSpeed(actor, AnimationSlot.Facial);
        if (!unpin.Success)
            return unpin;
        if (played)
        {
            var replayed = Blend(
                actor,
                captured != 0 ? captured : AnimationTimelines.StraightFace);
            if (!replayed.Success)
                // The layer is unpinned but still on Poser's timeline; the
                // hold stays owned so Reset or a retry runs the restore
                // again rather than stranding it.
                return replayed;
        }

        Mutate(actor, o =>
        {
            var slots = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
            slots.Remove(AnimationSlot.Facial);
            return o with { HeldExpression = null, SlotCaptures = slots };
        });
        return AnimationResult.Ok();
    }

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) =>
        OverridesFor(actor).HeldExpression;

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
            // Nothing is owned for this actor. Physics is not among the
            // things that could be: the freeze is held by the scene, not by
            // any actor, so no actor's reset can retire it.
            _port.ClearLoops(actor);
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

        // Loops first: a still-armed loop would replay the animation the
        // very restore below is removing.
        if (owned.LoopedSlots.Count > 0 || owned.LoopWantedSlots.Count > 0)
        {
            bool cleared = !owned.LoopedSlots.ContainsKey(AnimationSlot.Base) ||
                Try(_port.SetForceLoop(actor, 0));
            if (cleared)
            {
                _port.ClearLoops(actor);
                remaining = remaining with
                {
                    LoopedSlots = new Dictionary<AnimationSlot, ushort>(),
                    LoopWantedSlots = new HashSet<AnimationSlot>(),
                    BaseRepeatSuspended = false,
                };
            }
        }

        if (owned.OverallSpeed != null && Try(_port.ClearOverallSpeed(actor)))
            remaining = remaining with { OverallSpeed = null };

        // A held expression is released BEFORE the speed loop clears the
        // facial pin, so the face visibly leaves the expression instead of
        // resuming mid-timeline from an unpinned frame. Ownership is
        // released only when the WHOLE sequence landed; a partial failure
        // keeps HeldExpression so the next reset reruns it. The release
        // plays pass the existing capture so a reset never re-captures
        // state Poser itself produced.
        if (owned.HeldExpression != null &&
            Try(_port.ClearSlotSpeed(actor, AnimationSlot.Facial)) &&
            Try(_port.Blend(actor, AnimationTimelines.StraightFace, remaining.BaseCapture, out _)) &&
            Try(_port.ClearSlotSpeed(actor, AnimationSlot.Facial)) &&
            Try(_port.Blend(actor, AnimationTimelines.Idle, remaining.BaseCapture, out _)))
        {
            remaining = remaining with { HeldExpression = null };
            if (remaining.SlotSpeeds.ContainsKey(AnimationSlot.Facial))
            {
                var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
                speeds.Remove(AnimationSlot.Facial);
                remaining = remaining with { SlotSpeeds = speeds };
            }
        }

        // Restore captured non-base timelines.
        if (owned.SlotCaptures.Count > 0)
        {
            var liveRead = _port.Read(actor);
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
                cancelled = Try(_port.CancelActiveTimeline(actor));
            }

            // A failed cancellation processes NOTHING: replaying would
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
                    else if (Try(_port.Blend(actor, incoming, remaining.BaseCapture, out _)))
                        slots.Remove(slot);
                }
            }
            remaining = remaining with { SlotCaptures = slots };
        }

        // Base restoration runs AFTER the expression release and slot
        // replays: those go through the mode dance, which would overwrite
        // the just-restored mode and parameter if the base went back
        // first. The base is restored on EVERY attempt, but its capture is
        // released only once every mode-mutating dependency — expression
        // release, cancellation, slot replays — has resolved: a retry of
        // any of those alters or cancels the base again, and would
        // otherwise find its restoration point already gone.
        if (owned.BaseCapture is { } capture && Try(_port.RestoreBase(actor, capture)) &&
            remaining.HeldExpression == null && remaining.SlotCaptures.Count == 0)
        {
            remaining = remaining with { BaseCapture = null, BaseTimeline = null };
        }

        if (owned.SlotSpeeds.Count > 0)
        {
            var speeds = new Dictionary<AnimationSlot, float>(remaining.SlotSpeeds);
            foreach (var slot in owned.SlotSpeeds.Keys.ToList())
                if (Try(_port.ClearSlotSpeed(actor, slot)))
                    speeds.Remove(slot);
            remaining = remaining with { SlotSpeeds = speeds };
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
        }
        else
        {
            _overrides[actor] = remaining;
        }

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
        // The scene's hold holds no override entry, so the loop above never
        // saw it — and no reconcile will ever retire it, because the scene
        // is not something that can depart. This is the one place it is
        // released, and a failed unpatch keeps it on record rather than
        // clearing it over a still-patched site.
        var scene = SetScenePhysicsFrozen(false);
        if (!scene.Success && scene.Detail is { } sceneDetail)
            failures.Add($"scene: {sceneDetail}");
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
        // Physics is deliberately absent here: the freeze is held by the
        // scene, which cannot depart, so no actor leaving can retire it.
        var departed = _overrides.Keys.Where(id => !present.Contains(id)).ToList();
        foreach (var id in departed)
        {
            // Attempt the native restore; an actor that no longer resolves
            // simply has nothing left to restore into, and the entry is
            // dropped either way so it can never be re-applied.
            ResetActor(id);
        }
    }
}
