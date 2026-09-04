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
/// Owns Poser's session animation changes by exact actor generation.
/// Each native write records its first restore point. Ownership is cleared
/// only after restoration succeeds, so a live actor can retry a failed reset.
/// Selection, repeat, and speed state remain separate from pose history.
/// </summary>
public sealed class AnimationSession
{
    private readonly IAnimationRuntimePort _port;
    private readonly Dictionary<ActorId, AnimationOverrides> _overrides = new();
    /// <summary>Tracks the scene physics hold.</summary>
    private bool _sceneOwnsPhysics;

    /// <summary>Diagnostic tap for the pause/play path — wired to the
    /// plugin log at composition; every verb that can start or stop
    /// motion reports through it.</summary>
    public Action<string>? Trace { get; set; }

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

    // ── Base and blend ────────────────────────────────────────────────

    public ushort? SelectedFor(ActorId actor, AnimationSlot slot)
    {
        var owned = OverridesFor(actor);
        return owned.SelectedSlots.TryGetValue(slot, out var timeline)
            ? timeline
            : null;
    }

    /// <summary>Stages a selection without reading or writing native state.</summary>
    public AnimationResult ChooseSlot(
        ActorId actor, AnimationSlot slot, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        if (!AnimationSlots.Selectable.Contains(slot))
            return AnimationResult.Fail("This animation layer is not selectable.");
        if (timeline == 0)
            return AnimationResult.Fail("Choose an animation first.");
        if (slot is not AnimationSlot.Base and not AnimationSlot.Lips &&
            _port.TimelineSlot(timeline) != slot)
            return AnimationResult.Fail(
                $"Timeline {timeline} does not route to {AnimationSlots.DisplayName(slot)}.");

        Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots)
            {
                [slot] = timeline,
            };
            return o with { SelectedSlots = selected };
        });
        return AnimationResult.Ok();
    }

    /// <summary>Plays the actor's full-body timeline.</summary>
    public AnimationResult PlayBase(ActorId actor, ushort timeline)
    {
        var chosen = ChooseSlot(actor, AnimationSlot.Base, timeline);
        return chosen.Success ? ApplySelectedSlotCore(actor, AnimationSlot.Base) : chosen;
    }

    private AnimationResult PlayBaseCore(
        ActorId actor,
        ushort timeline,
        AnimationOverrides before,
        bool loopWanted)
    {
        if (Suspended() is { } blocked) return blocked;
        bool armRepeat = loopWanted;
        // A retarget needs the immediate native state, not the session's
        // original restore point, if repeat arming has to be rolled back.
        var rollbackCapture = armRepeat && before.BaseCapture != null
            ? _port.CaptureBase(actor)
            : null;
        var result = _port.PlayBase(actor, timeline, before.BaseCapture, out var captured);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base playback failed.");
        if (armRepeat)
        {
            var armed = _port.SetForceLoop(actor, timeline);
            if (!armed.Success)
            {
                var baseline = rollbackCapture ?? captured ?? before.BaseCapture;
                var rolledBack = baseline is { } restore
                    ? _port.RestoreBase(actor, restore)
                    : AnimationPortResult.Fail("The base restore point is unavailable.");
                if (rolledBack.Success)
                    return AnimationResult.Fail(armed.Detail ?? "Repeat arm failed.");

                // The play landed but rollback did not. Keep the original
                // restore point so Reset can retry instead of abandoning it.
                var ownedCapture = before.BaseCapture ?? captured ?? rollbackCapture;
                Mutate(actor, o =>
                {
                    var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                    loops.Remove(AnimationSlot.Base);
                    return o with
                    {
                        BaseCapture = o.BaseCapture ?? ownedCapture,
                        BaseTimeline = timeline,
                        LoopedSlots = loops,
                    };
                });
                return AnimationResult.Fail(
                    $"{armed.Detail ?? "Repeat arm failed."} " +
                    $"Rollback failed: {rolledBack.Detail ?? "base restore failed."}");
            }
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
            };
        });
        return AnimationResult.Ok();
    }

    private AnimationResult BlendCore(
        ActorId actor, ushort timeline, AnimationSlot? landing)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        bool suspendBaseRepeat = landing is { } target && target != AnimationSlot.Base &&
            current.LoopedSlots.ContainsKey(AnimationSlot.Base);
        ushort suspendedTimeline = suspendBaseRepeat
            ? current.LoopedSlots[AnimationSlot.Base]
            : (ushort)0;

        // Read the immutable slot baseline before any force-clear or play write.
        bool captureSlot = landing is { } slot &&
            slot != AnimationSlot.Base &&
            !current.SlotCaptures.ContainsKey(slot);
        ushort incoming = 0;
        if (captureSlot)
        {
            var reading = _port.Read(actor);
            if (reading == null)
                return AnimationResult.Fail("The layer restore point is unavailable.");
            incoming = reading.TimelineFor(landing!.Value);
        }

        // SetTimelineId clears the global force while routing by native slot.
        // Release it for the layer write, then restore the same Base force.
        if (suspendBaseRepeat)
        {
            var cleared = _port.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return AnimationResult.Fail(
                    cleared.Detail ?? "Full-body repeat suspension failed.");
        }

        var result = _port.Blend(actor, timeline, current.BaseCapture, out var captured);
        if (!result.Success)
        {
            if (suspendBaseRepeat)
            {
                var restored = _port.SetForceLoop(actor, suspendedTimeline);
                if (!restored.Success)
                {
                    Mutate(actor, o =>
                    {
                        var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                        loops.Remove(AnimationSlot.Base);
                        return o with { LoopedSlots = loops };
                    });
                    return AnimationResult.Fail(
                        $"{result.Detail ?? "Blend failed."} Repeat restore failed: " +
                        (restored.Detail ?? "full-body repeat arm failed."));
                }
            }
            return AnimationResult.Fail(result.Detail ?? "Blend failed.");
        }
        // The layer write has landed. Record its restore points before the
        // independent Base-force rearm can fail.
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
        {
            var restored = _port.SetForceLoop(actor, suspendedTimeline);
            if (!restored.Success)
            {
                Mutate(actor, o =>
                {
                    var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                    loops.Remove(AnimationSlot.Base);
                    return o with { LoopedSlots = loops };
                });
                return AnimationResult.Fail(
                    "Layer playback landed, but full-body repeat could not be " +
                    $"restored: {restored.Detail ?? "repeat arm failed."}");
            }
        }
        return AnimationResult.Ok();
    }

    /// <summary>Sets repeat intent for one slot.</summary>
    public AnimationResult SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        SetSlotLoopCore(actor, slot, timeline, on);

    private AnimationResult SetSlotLoopCore(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        if (Suspended() is { } blocked) return blocked;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return AnimationResult.Fail(
                "Repeat is unavailable for this layer: exact replay is unverified.");
        var current = OverridesFor(actor);
        if (!on && current.LoopedSlots.ContainsKey(slot))
        {
            var cleared = slot == AnimationSlot.Base
                ? _port.SetForceLoop(actor, 0)
                : _port.ClearSlotLoop(actor, slot);
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
            };
        });
        if (!on)
            return AnimationResult.Ok();

        if (slot == AnimationSlot.UpperBody)
        {
            // The switch may resume ownership only when Apply's last target
            // is still live; it never starts or retargets Upper playback.
            ushort upperTarget = current.AppliedSlots.GetValueOrDefault(slot);
            ushort liveUpper = _port.Read(actor)?.TimelineFor(slot) ?? 0;
            if (upperTarget == 0 || liveUpper != upperTarget)
                return AnimationResult.Ok();
            var armedUpper = _port.SetSlotLoop(actor, slot, upperTarget);
            if (!armedUpper.Success)
                return AnimationResult.Fail(armedUpper.Detail ?? "Upper-body loop arm failed.");
            Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
                {
                    [slot] = upperTarget,
                },
            });
            return AnimationResult.Ok();
        }

        // Zero means sticky intent. Only a Poser selection or an explicit
        // timeline may establish native base ownership.
        ushort target = timeline != 0 ? timeline : current.BaseTimeline ?? 0;
        if (target == 0)
            return AnimationResult.Ok();
        if (!SupportsForceLoop)
            return AnimationResult.Fail("Full-body repeat is unavailable for this client layout.");
        var captured = current.BaseCapture == null ? _port.CaptureBase(actor) : null;
        if (current.BaseCapture == null && captured == null)
            return AnimationResult.Fail("The base restore point is unavailable.");
        var armed = _port.SetForceLoop(actor, target);
        if (!armed.Success)
            return AnimationResult.Fail(armed.Detail ?? "Repeat arm failed.");
        Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
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

    public AnimationResult SetSpeed(ActorId actor, float speed) =>
        SetSpeedCore(actor, speed);

    private AnimationResult SetSpeedCore(ActorId actor, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Speed failed.");
        Mutate(actor, o => o with { OverallSpeed = speed });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSpeed(ActorId actor) => ClearSpeedCore(actor);

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

    public AnimationResult Pause(ActorId actor)
    {
        Trace?.Invoke($"Pause(all) {actor}");
        return SetSpeed(actor, 0f);
    }

    /// <summary>Resume drops the override rather than writing 1, so an
    /// actor the game is driving at its own speed keeps it.</summary>
    /// <summary>Play-all: releases every hold — the per-slot pauses the
    /// layer-play conversion parked, then the whole-actor speed. The
    /// sidebar's play is the ONLY verb that releases everything (ruled
    /// 2026-09-01).</summary>
    public AnimationResult Resume(ActorId actor)
    {
        Trace?.Invoke($"Resume(all) {actor}");
        foreach (var (slot, speed) in OverridesFor(actor).SlotSpeeds)
        {
            if (speed == 0f)
                ResumeSlotSpeedCore(actor, slot);
        }
        return ClearSpeed(actor);
    }

    /// <summary>Whether ANYTHING on the actor is paused — the whole-actor
    /// hold or any per-slot hold.</summary>
    public bool AnyPaused(ActorId actor)
    {
        if (IsPaused(actor))
            return true;
        foreach (var (_, speed) in OverridesFor(actor).SlotSpeeds)
        {
            if (speed == 0f)
                return true;
        }
        return false;
    }

    /// <summary>Whether any live layer is actually MOVING. The sidebar
    /// button offers Pause while this is true and Resume otherwise
    /// (ruled 2026-09-01): pause stops the stack, play overrides every
    /// individual hold.</summary>
    public bool AnyPlaying(ActorId actor)
    {
        if (IsPaused(actor))
            return false;
        if (Read(actor) is not { } reading)
            return false;
        var owned = OverridesFor(actor);
        foreach (var slotReading in reading.Slots)
        {
            if (slotReading.TimelineId != 0
                && owned.SlotSpeeds.GetValueOrDefault(slotReading.Slot, 1f) != 0f)
                return true;
        }
        return false;
    }

    /// <summary>Playing ONE layer must not resurrect the rest (ruled
    /// 2026-09-01): the whole-actor pause converts into per-slot holds on
    /// every OTHER live layer, then the overall speed lifts so the played
    /// layer can move.</summary>
    private AnimationResult ResumeForLayerPlay(ActorId actor, AnimationSlot playing)
    {
        var current = OverridesFor(actor);
        if (Read(actor) is { } reading)
        {
            foreach (var slotReading in reading.Slots)
            {
                if (slotReading.TimelineId == 0
                    || slotReading.Slot == playing
                    || current.SlotSpeeds.ContainsKey(slotReading.Slot))
                    continue;
                var held = SetSlotSpeedCore(actor, slotReading.Slot, 0f);
                Trace?.Invoke(
                    $"  hold slot={slotReading.Slot} (tl {slotReading.TimelineId})"
                    + $" -> {(held.Success ? "ok" : held.Detail)}");
                if (!held.Success)
                    return held;
            }
        }
        Trace?.Invoke($"  lift overall for {playing}");
        return ClearSpeedCore(actor);
    }

    /// <summary>
    /// Replays from the start after releasing a Poser-owned pause. A nonzero
    /// owned speed remains active. <paramref name="resumed"/> reports whether
    /// the pause was released.
    /// </summary>
    public AnimationResult Replay(ActorId actor, ushort timeline, out bool resumed)
    {
        resumed = false;
        if (Suspended() is { } blocked) return blocked;
        if (IsPaused(actor))
        {
            var released = ResumeForLayerPlay(
                actor, _port.TimelineSlot(timeline) ?? AnimationSlot.Base);
            if (!released.Success)
                return released;
            resumed = true;
        }
        return BlendCore(actor, timeline, _port.TimelineSlot(timeline));
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

    public AnimationResult SetSlotSpeed(
        ActorId actor, AnimationSlot slot, float speed)
    {
        var set = SetSlotSpeedCore(actor, slot, speed);
        if (set.Success && speed == 0f)
            CollapseWhenNothingPlays(actor);
        return set;
    }

    /// <summary>A slot reaching speed zero IS a pause, however it got
    /// there — slider or button — and when the last moving layer stops,
    /// the actor collapses into the one canonical "truly paused" shape:
    /// overall zero (ruled 2026-09-01).</summary>
    private void CollapseWhenNothingPlays(ActorId actor)
    {
        if (IsPaused(actor) || AnyPlaying(actor))
            return;
        Trace?.Invoke($"collapse: every layer held on {actor}");
        SetSpeed(actor, 0f);
    }

    private AnimationResult SetSlotSpeedCore(
        ActorId actor, AnimationSlot slot, float speed, float? firstCapture = null)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        float live;
        if (current.SlotSpeeds.TryGetValue(slot, out var ownedSpeed))
            live = ownedSpeed;
        else
        {
            var reading = _port.Read(actor);
            if (reading == null)
                return AnimationResult.Fail("The layer speed restore point is unavailable.");
            live = reading.SpeedFor(slot);
        }
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds) { [slot] = speed };
            var captures = new Dictionary<AnimationSlot, float>(o.SlotSpeedCaptures);
            if (!captures.ContainsKey(slot))
                captures[slot] = firstCapture is { } original && float.IsFinite(original)
                    ? original
                    : float.IsFinite(live) ? live : 1f;
            var resume = new Dictionary<AnimationSlot, float>(o.SlotResumeSpeeds);
            if (speed > 0f)
                resume[slot] = speed;
            else if (live > 0f && float.IsFinite(live))
                resume[slot] = live;
            return o with
            {
                SlotSpeeds = speeds,
                SlotSpeedCaptures = captures,
                SlotResumeSpeeds = resume,
            };
        });
        return AnimationResult.Ok();
    }

    public AnimationResult ClearSlotSpeed(ActorId actor, AnimationSlot slot) =>
        ClearSlotSpeedCore(actor, slot);

    private AnimationResult ClearSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        float restore = current.SlotSpeedCaptures.TryGetValue(slot, out var captured)
            ? captured
            : 1f;
        var result = _port.ClearSlotSpeed(actor, slot, restore);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed reset failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
            speeds.Remove(slot);
            var captures = new Dictionary<AnimationSlot, float>(o.SlotSpeedCaptures);
            captures.Remove(slot);
            var resume = new Dictionary<AnimationSlot, float>(o.SlotResumeSpeeds);
            resume.Remove(slot);
            return o with
            {
                SlotSpeeds = speeds,
                SlotSpeedCaptures = captures,
                SlotResumeSpeeds = resume,
            };
        });
        return AnimationResult.Ok();
    }

    public AnimationResult PauseSlot(ActorId actor, AnimationSlot slot)
    {
        var held = SetSlotSpeedCore(actor, slot, 0f);
        if (!held.Success)
            return held;
        CollapseWhenNothingPlays(actor);
        return held;
    }

    /// <summary>Applies Selected; only Base may use the emote lifecycle.</summary>
    /// <summary>APPLY stages, PLAY plays (ruled 2026-09-01): with
    /// <paramref name="resume"/> false, a paused actor takes the animation
    /// frozen at its start and nothing moves — the layer's Play button (or
    /// the sidebar's play-all) is what starts it.</summary>
    public AnimationResult PlaySelectedSlot(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry,
        bool playFromStart, bool resume = true)
    {
        var outcome = PlaySelectedSlotTraced(actor, slot, entry, playFromStart, resume);
        Trace?.Invoke(outcome.Success
            ? $"  PlaySelectedSlot {slot} -> ok"
            : $"  PlaySelectedSlot {slot} -> FAIL: {outcome.Detail}");
        return outcome;
    }

    private AnimationResult PlaySelectedSlotTraced(
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
                return AnimationResult.Fail("The chosen animation identity changed.");
            // RESUME, don't replay: a slot already live on the selected
            // timeline keeps its position — re-blending spawned a crossfade
            // control and restarted the clip from zero (the pause→play
            // scrub reset, sampler 19:50:52).
            bool alreadyLive = Read(actor)?.TimelineFor(slot) == selected;
            if (!alreadyLive)
            {
                var played = ApplySelectedSlotCore(
                    actor, slot, playFromStart && entry != null ? entry : null);
                if (!played.Success)
                    return played;
            }
        }
        if (!resume && IsPaused(actor))
        {
            Trace?.Invoke("  staged only (paused, resume=false)");
            return AnimationResult.Ok();
        }
        if (IsPaused(actor))
        {
            var resumed = ResumeForLayerPlay(actor, slot);
            if (!resumed.Success)
                return resumed;
            resumedOverall = true;
        }
        if (OverridesFor(actor).SlotSpeeds.TryGetValue(slot, out var speed) && speed == 0f)
            return resume
                ? ResumeSlotSpeedCore(actor, slot)
                : AnimationResult.Ok();
        return SelectedFor(actor, slot) != null || resumedOverall
            ? AnimationResult.Ok()
            : AnimationResult.Fail("Choose an animation first.");
    }

    private AnimationResult ApplySelectedSlotCore(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry = null)
    {
        var current = OverridesFor(actor);
        if (!current.SelectedSlots.TryGetValue(slot, out var selected))
            return AnimationResult.Fail("Choose an animation first.");
        if (slot == AnimationSlot.Base &&
            entry is { CanPlayFromStart: true } && entry.TimelineId == selected &&
            entry.Slot == slot)
            return PlayBaseEmoteCore(actor, entry, current);
        if (slot == AnimationSlot.Base)
        {
            return PlayBaseCore(
                actor,
                selected,
                current,
                current.LoopWantedSlots.Contains(AnimationSlot.Base));
        }
        if (slot == AnimationSlot.Lips)
            return SetLipsCore(actor, selected);

        var result = BlendCore(actor, selected, slot);
        if (!result.Success)
            return result;

        Mutate(actor, o =>
        {
            var applied = new Dictionary<AnimationSlot, ushort>(o.AppliedSlots)
            {
                [slot] = selected,
            };
            return o with { AppliedSlots = applied };
        });
        if (slot != AnimationSlot.UpperBody ||
            !OverridesFor(actor).LoopWantedSlots.Contains(slot))
            return AnimationResult.Ok();

        var armed = _port.SetSlotLoop(actor, slot, selected);
        if (!armed.Success)
            return AnimationResult.Fail(armed.Detail ?? "Upper-body loop arm failed.");
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
            {
                [slot] = selected,
            };
            return o with { LoopedSlots = loops };
        });
        return AnimationResult.Ok();
    }

    private AnimationResult PlayBaseEmoteCore(
        ActorId actor, TimelineEntry entry, AnimationOverrides before)
    {
        if (Suspended() is { } blocked) return blocked;
        bool armRepeat = before.LoopWantedSlots.Contains(AnimationSlot.Base);
        var firstCapture = before.BaseCapture ?? _port.CaptureBase(actor);
        if (firstCapture == null)
            return AnimationResult.Fail("The base restore point is unavailable.");
        var rollbackCapture = before.BaseCapture != null
            ? _port.CaptureBase(actor)
            : firstCapture;
        var played = _port.PlayEmote(actor, entry.EmoteId);
        if (!played.Success)
            return AnimationResult.Fail(played.Detail ?? "Emote playback failed.");
        if (armRepeat)
        {
            var armed = _port.SetForceLoop(actor, (ushort)entry.TimelineId);
            if (!armed.Success)
            {
                var baseline = rollbackCapture ?? before.BaseCapture;
                var rolledBack = baseline is { } restore
                    ? _port.RestoreBase(actor, restore)
                    : AnimationPortResult.Fail("The base restore point is unavailable.");
                if (rolledBack.Success)
                    return AnimationResult.Fail(armed.Detail ?? "Repeat arm failed.");
                Mutate(actor, o => o with
                {
                    BaseTimeline = (ushort)entry.TimelineId,
                    BaseCapture = o.BaseCapture ?? baseline,
                });
                return AnimationResult.Fail(
                    $"{armed.Detail ?? "Repeat arm failed."} " +
                    $"Rollback failed: {rolledBack.Detail ?? "base restore failed."}");
            }
        }
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            if (armRepeat)
                loops[AnimationSlot.Base] = (ushort)entry.TimelineId;
            else
                loops.Remove(AnimationSlot.Base);
            return o with
            {
                BaseTimeline = (ushort)entry.TimelineId,
                BaseCapture = o.BaseCapture ?? firstCapture,
                LoopedSlots = loops,
            };
        });
        return AnimationResult.Ok();
    }

    private AnimationResult ResumeSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        var current = OverridesFor(actor);
        if (!current.SlotSpeeds.TryGetValue(slot, out var speed) || speed != 0f)
            return AnimationResult.Ok();
        // A hold parked by the layer-play conversion may never have seen
        // a nonzero speed: fall back to the captured original, then 1.
        if (!current.SlotResumeSpeeds.TryGetValue(slot, out var resume) ||
            !float.IsFinite(resume) || resume <= 0f)
            resume = current.SlotSpeedCaptures.TryGetValue(slot, out var captured)
                && float.IsFinite(captured) && captured > 0f
                ? captured
                : 1f;
        return SetSlotSpeedCore(actor, slot, resume);
    }

    public bool OwnsSlot(ActorId actor, AnimationSlot slot)
    {
        var owned = OverridesFor(actor);
        return SelectedFor(actor, slot) != null ||
            owned.SlotSpeedCaptures.ContainsKey(slot);
    }

    /// <summary>Restores one selectable layer and clears its selection.</summary>
    public AnimationResult ResetSlot(ActorId actor, AnimationSlot slot)
    {
        if (Suspended() is { } blocked) return blocked;
        if (!AnimationSlots.Selectable.Contains(slot))
            return AnimationResult.Fail("This animation layer cannot be reset.");
        if (slot == AnimationSlot.Facial)
        {
            var facial = OverridesFor(actor);
            if (facial.HeldExpression != null ||
                facial.SelectedSlots.ContainsKey(AnimationSlot.Facial) ||
                facial.SlotCaptures.ContainsKey(AnimationSlot.Facial))
                return ReleaseExpressionCore(actor);
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
            var speed = ClearSlotSpeedCore(actor, slot);
            if (!speed.Success)
                return speed;
        }
        AnimationResult selection = slot switch
        {
            AnimationSlot.Base => ResetBaseSelection(actor),
            AnimationSlot.Lips => SelectedFor(actor, slot) != null
                ? ResetLipsSelection(actor)
                : AnimationResult.Ok(),
            _ => ResetBlendSelection(actor, slot),
        };
        if (!selection.Success)
            failures.Add(selection.Detail ?? "Layer restore failed.");
        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
    }

    private AnimationResult ResetBaseSelection(
        ActorId actor, bool preserveLoopIntent = false)
    {
        var current = OverridesFor(actor);
        if (current.BaseTimeline == null &&
            !current.SelectedSlots.ContainsKey(AnimationSlot.Base))
            return AnimationResult.Ok();
        if (current.LoopedSlots.ContainsKey(AnimationSlot.Base))
        {
            var cleared = _port.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return AnimationResult.Fail(cleared.Detail ?? "Repeat clear failed.");
        }
        if (current.BaseCapture is { } capture)
        {
            var restored = _port.RestoreBase(actor, capture);
            if (!restored.Success)
                return AnimationResult.Fail(restored.Detail ?? "Base restore failed.");
        }
        Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            loops.Remove(AnimationSlot.Base);
            var wanted = new HashSet<AnimationSlot>(o.LoopWantedSlots);
            if (!preserveLoopIntent)
                wanted.Remove(AnimationSlot.Base);
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
            selected.Remove(AnimationSlot.Base);
            bool baseStillNeeded = selected.Count > 0 || o.SlotCaptures.Count > 0;
            return o with
            {
                SelectedSlots = selected,
                BaseTimeline = null,
                BaseCapture = baseStillNeeded ? o.BaseCapture : null,
                LoopedSlots = loops,
                LoopWantedSlots = wanted,
            };
        });
        return AnimationResult.Ok();
    }

    private AnimationResult ResetBlendSelection(ActorId actor, AnimationSlot slot)
    {
        var current = OverridesFor(actor);
        if (!current.SelectedSlots.ContainsKey(slot))
            return AnimationResult.Ok();
        if (!current.SlotCaptures.TryGetValue(slot, out var incoming))
        {
            // Choose is staging-only, so an unapplied row has nothing native to undo.
            Mutate(actor, o =>
            {
                var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
                selected.Remove(slot);
                var wanted = new HashSet<AnimationSlot>(o.LoopWantedSlots);
                wanted.Remove(slot);
                return o with { SelectedSlots = selected, LoopWantedSlots = wanted };
            });
            return AnimationResult.Ok();
        }

        if (current.LoopedSlots.ContainsKey(slot))
        {
            var loopCleared = _port.ClearSlotLoop(actor, slot);
            if (!loopCleared.Success)
                return AnimationResult.Fail(loopCleared.Detail ?? "Layer loop clear failed.");
        }

        bool preserveRepeat = current.LoopedSlots.TryGetValue(
            AnimationSlot.Base, out var repeated);
        if (preserveRepeat)
        {
            var cleared = _port.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return AnimationResult.Fail(
                    cleared.Detail ?? "Full-body repeat suspension failed.");
        }

        var restored = incoming != 0
            ? _port.Blend(actor, incoming, current.BaseCapture, out _)
            : RestoreEmptySlot(actor, slot, current);

        // A blend restore uses the mode-changing sequencer route. Put the
        // captured base back when no explicit Base selection should remain.
        AnimationPortResult? baseRestored = null;
        if (restored.Success && current.BaseTimeline == null &&
            current.BaseCapture is { } capture)
        {
            baseRestored = _port.RestoreBase(actor, capture);
        }

        AnimationPortResult? repeatRestored = null;
        if (preserveRepeat)
            repeatRestored = _port.SetForceLoop(actor, repeated);
        if (repeatRestored is { Success: false } repeatFailure)
        {
            Mutate(actor, o =>
            {
                var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                loops.Remove(AnimationSlot.Base);
                return o with { LoopedSlots = loops };
            });
            return AnimationResult.Fail(
                $"Layer restore could not rearm full-body repeat: " +
                (repeatFailure.Detail ?? "repeat arm failed."));
        }
        if (!restored.Success)
            return AnimationResult.Fail(restored.Detail ?? "Layer restore failed.");
        if (baseRestored is { Success: false } baseFailure)
            return AnimationResult.Fail(
                baseFailure.Detail ?? "Base restore failed.");

        Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
            selected.Remove(slot);
            var captures = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
            captures.Remove(slot);
            var applied = new Dictionary<AnimationSlot, ushort>(o.AppliedSlots);
            applied.Remove(slot);
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
            loops.Remove(slot);
            var wanted = new HashSet<AnimationSlot>(o.LoopWantedSlots);
            wanted.Remove(slot);
            return o with
            {
                SelectedSlots = selected,
                AppliedSlots = applied,
                SlotCaptures = captures,
                LoopedSlots = loops,
                LoopWantedSlots = wanted,
                HeldExpression = slot == AnimationSlot.Facial ? null : o.HeldExpression,
                BaseCapture = o.BaseTimeline == null && selected.Count == 0 && captures.Count == 0
                    ? null
                    : o.BaseCapture,
            };
        });
        return AnimationResult.Ok();
    }

    private AnimationPortResult RestoreEmptySlot(
        ActorId actor, AnimationSlot slot, AnimationOverrides current)
    {
        var reading = _port.Read(actor);
        if (reading == null)
            return AnimationPortResult.Fail("The actor is no longer available.");
        var immediateBase = _port.CaptureBase(actor);
        // Zero the slot's own id entries first: a bare cancel left them and
        // the base restore below re-scheduled the layer from them.
        var cancelled = _port.ClearSlotTimeline(actor, slot);
        if (!cancelled.Success)
            return cancelled;

        var failures = new List<string>();
        var retrySlots = new Dictionary<AnimationSlot, ushort>();
        foreach (var survivor in reading.Slots)
        {
            if (survivor.Slot is AnimationSlot.Base || survivor.Slot == slot ||
                survivor.TimelineId == 0)
                continue;
            var replayed = survivor.Slot == AnimationSlot.Lips && reading.LipsOverride != 0
                ? _port.SetLips(actor, reading.LipsOverride)
                : _port.Blend(actor, survivor.TimelineId, current.BaseCapture, out _);
            if (!replayed.Success)
            {
                failures.Add(replayed.Detail ?? $"{survivor.Slot} replay failed.");
                retrySlots[survivor.Slot] = survivor.TimelineId;
            }
        }
        if (immediateBase is { } baseline)
        {
            var baseRestored = _port.RestoreBase(actor, baseline);
            if (!baseRestored.Success)
                failures.Add(baseRestored.Detail ?? "Base rollback failed.");
        }
        if (failures.Count > 0)
            Mutate(actor, o =>
            {
                var captures = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
                foreach (var (failedSlot, timeline) in retrySlots)
                    if (!captures.ContainsKey(failedSlot))
                        captures[failedSlot] = timeline;
                return o with
                {
                    SlotCaptures = captures,
                    BaseCapture = o.BaseCapture ?? immediateBase,
                };
            });
        return failures.Count == 0
            ? AnimationPortResult.Ok()
            : AnimationPortResult.Fail(string.Join("; ", failures));
    }

    // ── Lips, stance, weapon, position ────────────────────────────────

    /// <summary>
    /// Sets the lip override. Selecting None restores the captured incoming
    /// timeline because zero is a native "no speech timeline" value.
    /// </summary>
    public AnimationResult SetLips(ActorId actor, ushort timeline)
    {
        if (timeline == 0)
            return ResetLipsSelection(actor);
        var chosen = ChooseSlot(actor, AnimationSlot.Lips, timeline);
        return chosen.Success ? SetLipsCore(actor, timeline) : chosen;
    }

    private AnimationResult SetLipsCore(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        ushort? captured = null;
        if (current.LipsCapture == null)
        {
            var reading = _port.Read(actor);
            if (reading == null)
                return AnimationResult.Fail("The lips restore point is unavailable.");
            captured = reading.LipsOverride;
        }
        var result = _port.SetLips(actor, timeline);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Lips failed.");

        Mutate(actor, o => o with
        {
            Lips = timeline,
            LipsCapture = o.LipsCapture ?? captured,
        });
        return AnimationResult.Ok();
    }

    private AnimationResult ResetLipsSelection(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (current.Lips == null && current.LipsCapture == null)
        {
            Mutate(actor, o =>
            {
                var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
                selected.Remove(AnimationSlot.Lips);
                return o with { SelectedSlots = selected };
            });
            return AnimationResult.Ok();
        }
        ushort target = current.LipsCapture ?? 0;
        var result = _port.SetLips(actor, target);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Lips restore failed.");
        Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
            selected.Remove(AnimationSlot.Lips);
            return o with
            {
                SelectedSlots = selected,
                Lips = null,
                LipsCapture = null,
            };
        });
        return AnimationResult.Ok();
    }

    public AnimationResult SetStance(ActorId actor, AnimationStance stance, int pose)
    {
        if (Suspended() is { } blocked) return blocked;
        var capture = OverridesFor(actor).StanceCaptureValue;
        if (capture == null && _port.Read(actor) is { } reading)
            capture = new StanceCapture(reading.Stance, reading.Pose);

        // Stance playback stops repeat arms but keeps General repeat intent.
        var owned = OverridesFor(actor);
        bool wantsBaseLoop = owned.LoopWantedSlots.Contains(AnimationSlot.Base);
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
                LoopWantedSlots = wantsBaseLoop
                    ? new HashSet<AnimationSlot> { AnimationSlot.Base }
                    : new HashSet<AnimationSlot>(),
            });
            owned = OverridesFor(actor);
        }
        if (owned.BaseCapture != null || owned.BaseTimeline != null)
        {
            var released = ResetBaseSelection(actor, preserveLoopIntent: true);
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
    /// Records the scene hold only after the global patch state matches the
    /// request.
    /// </summary>
    public AnimationResult SetScenePhysicsFrozen(bool frozen)
    {
        if (frozen == _sceneOwnsPhysics)
            return AnimationResult.Ok();

        if (frozen != _port.IsPhysicsFrozen)
        {
            var result = _port.SetPhysicsFrozen(frozen);
            if (!result.Success)
                // Name the failed direction when the runtime gives no detail.
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
    public AnimationResult BeginScrub(ActorId actor, ScrubControlId control) =>
        BeginScrubCore(actor, control);

    private AnimationResult BeginScrubCore(ActorId actor, ScrubControlId control)
    {
        var controls = _port.EnumerateControls(actor, out var token);
        ScrubControlReading? target = null;
        foreach (var reading in controls)
            if (reading.Id == control)
                target = reading;
        if (target == null)
            return AnimationResult.Fail("That animation control is no longer present.");

        // A scrub never retargets to a different actor.
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
    /// Writes a frame clamped to the duration captured at Begin. Actor and
    /// skeleton mismatches end the drag instead of retargeting the write.
    /// </summary>
    public AnimationResult UpdateScrub(ActorId actor, float time) =>
        UpdateScrubCore(actor, time);

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
        if (_scrub == null)
            return;
        _scrub = null;
    }

    // ── Held expression ──────────────────────────────────────────────────

    /// <summary>Applies an expression and immediately pins its facial frame.</summary>
    public AnimationResult HoldExpression(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var chosen = ChooseSlot(actor, AnimationSlot.Facial, timeline);
        if (!chosen.Success)
            return chosen;

        // A replacement must run with Facial unpinned before it is held again.
        var current = OverridesFor(actor);
        float? speedCapture = null;
        if (!current.SlotSpeedCaptures.ContainsKey(AnimationSlot.Facial))
        {
            var reading = _port.Read(actor);
            if (reading == null)
                return AnimationResult.Fail("The facial speed restore point is unavailable.");
            speedCapture = reading.SpeedFor(AnimationSlot.Facial);
        }
        if (current.SlotSpeeds.ContainsKey(AnimationSlot.Facial))
        {
            float restore = current.SlotSpeedCaptures.TryGetValue(
                AnimationSlot.Facial, out var captured) ? captured : 1f;
            var unpinned = _port.ClearSlotSpeed(
                actor, AnimationSlot.Facial, restore);
            if (!unpinned.Success)
                return AnimationResult.Fail(
                    unpinned.Detail ?? "Expression release failed.");
            Mutate(actor, o =>
            {
                var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
                speeds.Remove(AnimationSlot.Facial);
                var resumes = new Dictionary<AnimationSlot, float>(o.SlotResumeSpeeds);
                resumes.Remove(AnimationSlot.Facial);
                return o with
                {
                    SlotSpeeds = speeds,
                    SlotResumeSpeeds = resumes,
                    HeldExpression = null,
                };
            });
        }

        var played = ApplySelectedSlotCore(actor, AnimationSlot.Facial);
        if (!played.Success)
            return played;

        var held = SetSlotSpeedCore(
            actor, AnimationSlot.Facial, 0f, speedCapture);
        if (!held.Success)
        {
            var rollback = ResetSlot(actor, AnimationSlot.Facial);
            return rollback.Success
                ? held
                : AnimationResult.Fail(
                    $"{held.Detail ?? "Expression hold failed."} " +
                    $"Restore failed: {rollback.Detail}");
        }
        Mutate(actor, o => o with { HeldExpression = timeline });
        return AnimationResult.Ok();
    }

    /// <summary>Releases a held facial expression.</summary>
    public AnimationResult ReleaseExpression(ActorId actor)
    {
        if (Suspended() is { } blocked) return blocked;
        return ReleaseExpressionCore(actor);
    }

    private AnimationResult ReleaseExpressionCore(ActorId actor)
    {
        var current = OverridesFor(actor);
        ushort? active = current.HeldExpression;
        if (active == null &&
            current.SelectedSlots.TryGetValue(AnimationSlot.Facial, out var selected))
            active = selected;
        if (active is not { } held)
            return ResetSlotWithoutExpressionBridge(actor, AnimationSlot.Facial);
        if (!current.SlotCaptures.ContainsKey(AnimationSlot.Facial))
            return ResetBlendSelection(actor, AnimationSlot.Facial);

        float restoreSpeed = current.SlotSpeedCaptures.TryGetValue(
            AnimationSlot.Facial, out var capturedSpeed) ? capturedSpeed : 1f;
        var unpinned = _port.ClearSlotSpeed(
            actor, AnimationSlot.Facial, restoreSpeed);
        if (!unpinned.Success)
            return AnimationResult.Fail(
                unpinned.Detail ?? "Expression speed release failed.");

        // Release clears Facial speed, plays Straight Face, clears speed
        // again, then restores the captured facial slot.
        var straight = BlendCore(
            actor, AnimationTimelines.StraightFace, AnimationSlot.Facial);
        var again = straight.Success
            ? _port.ClearSlotSpeed(actor, AnimationSlot.Facial, restoreSpeed)
            : AnimationPortResult.Fail(straight.Detail ?? "Straight Face failed.");
        var restored = straight.Success && again.Success
            ? ResetBlendSelection(actor, AnimationSlot.Facial)
            : AnimationResult.Fail(
                straight.Detail ?? again.Detail ?? "Expression release failed.");
        if (!restored.Success)
        {
            // Session ownership has not been cleared. Put the held expression
            // back when possible so Reset remains a truthful retry.
            var replayed = BlendCore(actor, held, AnimationSlot.Facial);
            var repinned = replayed.Success
                ? _port.SetSlotSpeed(actor, AnimationSlot.Facial, 0f)
                : AnimationPortResult.Fail(replayed.Detail ?? "Expression replay failed.");
            return AnimationResult.Fail(
                (restored.Detail ?? "Expression release failed.") +
                (replayed.Success && repinned.Success
                    ? string.Empty
                    : $" Hold rollback failed: " +
                      (replayed.Detail ?? repinned.Detail ?? "facial hold failed.")));
        }

        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds);
            speeds.Remove(AnimationSlot.Facial);
            var speedCaptures = new Dictionary<AnimationSlot, float>(o.SlotSpeedCaptures);
            speedCaptures.Remove(AnimationSlot.Facial);
            var resumes = new Dictionary<AnimationSlot, float>(o.SlotResumeSpeeds);
            resumes.Remove(AnimationSlot.Facial);
            return o with
            {
                SlotSpeeds = speeds,
                SlotSpeedCaptures = speedCaptures,
                SlotResumeSpeeds = resumes,
                HeldExpression = null,
            };
        });
        return AnimationResult.Ok();
    }

    private AnimationResult ResetSlotWithoutExpressionBridge(
        ActorId actor, AnimationSlot slot)
    {
        if (OverridesFor(actor).SlotSpeedCaptures.ContainsKey(slot))
        {
            var speed = ClearSlotSpeedCore(actor, slot);
            if (!speed.Success)
                return speed;
        }
        return ResetBlendSelection(actor, slot);
    }

    /// <summary>Restores the captured facial layer.</summary>
    public AnimationResult RestoreFacialLayer(ActorId actor)
        => ReleaseExpression(actor);

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

        // Each aspect is released only when its restore succeeded. What
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
                };
            }
        }

        if (owned.OverallSpeed != null && Try(_port.ClearOverallSpeed(actor)))
            remaining = remaining with { OverallSpeed = null };

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
                    else if (Try(_port.Blend(actor, incoming, remaining.BaseCapture, out _)))
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
        if (owned.BaseCapture is { } capture && Try(_port.RestoreBase(actor, capture)) &&
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
                if (Try(_port.ClearSlotSpeed(actor, slot, restore)))
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
            Try(_port.SetStance(actor, stance.Stance, stance.Pose)))
            remaining = remaining with { StanceCaptureValue = null };
        if (owned.WeaponCapture is { } weapon &&
            Try(_port.SetWeaponDrawn(actor, weapon)))
            remaining = remaining with { WeaponCapture = null };
        if (owned.LipsCapture is { } lips && Try(_port.SetLips(actor, lips)))
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
        if (owned.PositionLock && Try(_port.SetPositionLock(actor, false)))
            remaining = remaining with { PositionLock = false };

        if (actorGone || !remaining.HasAny)
        {
            if (actorGone)
                _port.ClearLoops(actor);
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
