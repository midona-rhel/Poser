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
        if (slot == AnimationSlot.Base)
            return owned.BaseTimeline;
        if (slot == AnimationSlot.Lips)
            return owned.Lips;
        return owned.SelectedSlots.TryGetValue(slot, out var timeline)
            ? timeline
            : null;
    }

    /// <summary>Selects a timeline through the route verified for its layer.</summary>
    public AnimationResult SelectSlot(
        ActorId actor, AnimationSlot slot, ushort timeline, bool nativeLoop = false)
    {
        if (!AnimationSlots.Selectable.Contains(slot))
            return AnimationResult.Fail("This animation layer is not selectable.");
        if (timeline == 0)
            return AnimationResult.Fail("Choose an animation first.");
        if (slot == AnimationSlot.Base)
            return PlayBase(actor, timeline, nativeLoop);
        if (slot == AnimationSlot.Lips)
            return SetLips(actor, timeline);

        var landing = _port.TimelineSlot(timeline);
        if (landing != slot)
            return AnimationResult.Fail(
                $"Timeline {timeline} does not route to {AnimationSlots.DisplayName(slot)}.");

        var before = OverridesFor(actor);
        var result = BlendCore(actor, timeline, landing);
        if (!result.Success)
            return result;
        Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots)
            {
                [slot] = timeline,
            };
            return o with
            {
                SelectedSlots = selected,
                // The Animation picker takes authority from a held Pose
                // expression; both surfaces still share this one selection.
                HeldExpression = slot == AnimationSlot.Facial ? null : o.HeldExpression,
            };
        });

        if (slot == AnimationSlot.Facial && before.HeldExpression != null &&
            OverridesFor(actor).SlotSpeeds.TryGetValue(slot, out var speed) && speed == 0f)
            return ResumeSlotSpeedCore(actor, slot);
        return AnimationResult.Ok();
    }

    /// <summary>Plays the actor's full-body timeline.</summary>
    public AnimationResult PlayBase(ActorId actor, ushort timeline) =>
        PlayBase(actor, timeline, nativeLoop: false);

    private AnimationResult PlayBase(ActorId actor, ushort timeline, bool nativeLoop)
    {
        var before = OverridesFor(actor);
        return PlayBaseCore(
            actor,
            timeline,
            before,
            before.LoopWantedSlots.Contains(AnimationSlot.Base),
            nativeLoop);
    }

    private AnimationResult PlayBaseCore(
        ActorId actor,
        ushort timeline,
        AnimationOverrides before,
        bool loopWanted,
        bool nativeLoop)
    {
        if (Suspended() is { } blocked) return blocked;
        bool armRepeat = loopWanted && !nativeLoop && !HasLayerSelection(before);
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
                        BaseUsesNativeLoop = nativeLoop,
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
                BaseUsesNativeLoop = nativeLoop,
                LoopedSlots = loops,
                BaseRepeatSuspended = loopWanted && !nativeLoop && !armRepeat,
            };
        });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a timeline through its native route.</summary>
    public AnimationResult Blend(ActorId actor, ushort timeline) =>
        BlendCore(actor, timeline, _port.TimelineSlot(timeline));

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
        overrides.SelectedSlots.Count > 0 || overrides.SlotCaptures.Keys.Any(
            slot => slot != AnimationSlot.Base);

    public AnimationResult PlayEmote(ActorId actor, uint emoteId) =>
        PlayEmoteCore(actor, emoteId);

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
            Mutate(actor, o => o with
            {
                BaseTimeline = null,
                BaseUsesNativeLoop = false,
            });
            return AnimationResult.Ok();
        }

        var result = _port.RestoreBase(actor, capture);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Base restore failed.");
        // Clear ownership after restoration succeeds.
        Mutate(actor, o => o with
        {
            BaseTimeline = null,
            BaseUsesNativeLoop = false,
            BaseCapture = null,
        });
        return AnimationResult.Ok();
    }

    /// <summary>Plays a catalog entry through its native route.</summary>
    public AnimationResult PlayEntry(
        ActorId actor, TimelineEntry entry, bool asBase, bool playFromStart)
    {
        var timeline = (ushort)entry.TimelineId;
        if (asBase)
            return PlayBase(actor, timeline, entry.IsLoop);
        if (playFromStart && entry.CanPlayFromStart)
        {
            var result = PlayEmote(actor, entry.EmoteId);
            if (result.Success)
                return result;
        }
        return Blend(actor, timeline);
    }

    /// <summary>Sets repeat intent for one slot.</summary>
    public AnimationResult SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        SetSlotLoopCore(actor, slot, timeline, on);

    private AnimationResult SetSlotLoopCore(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        if (Suspended() is { } blocked) return blocked;
        if (slot != AnimationSlot.Base)
            return AnimationResult.Fail(
                "Repeat is unavailable for this layer: exact replay is unverified.");
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

        // Zero means sticky intent. Only a Poser selection or an explicit
        // timeline may establish native base ownership.
        ushort target = current.BaseUsesNativeLoop
            ? (ushort)0
            : timeline != 0 ? timeline : current.BaseTimeline ?? 0;
        if (target == 0)
            return AnimationResult.Ok();
        if (!SupportsForceLoop)
            return AnimationResult.Fail("Full-body repeat is unavailable for this client layout.");
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

    public AnimationResult Pause(ActorId actor) => SetSpeed(actor, 0f);

    /// <summary>Resume drops the override rather than writing 1, so an
    /// actor the game is driving at its own speed keeps it.</summary>
    public AnimationResult Resume(ActorId actor) => ClearSpeed(actor);

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

    public AnimationResult SetSlotSpeed(
        ActorId actor, AnimationSlot slot, float speed) =>
        SetSlotSpeedCore(actor, slot, speed);

    private AnimationResult SetSlotSpeedCore(
        ActorId actor, AnimationSlot slot, float speed)
    {
        if (Suspended() is { } blocked) return blocked;
        var current = OverridesFor(actor);
        float live = current.SlotSpeeds.TryGetValue(slot, out var ownedSpeed)
            ? ownedSpeed
            : _port.Read(actor)?.SpeedFor(slot) ?? 1f;
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return AnimationResult.Fail(result.Detail ?? "Slot speed failed.");
        Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds) { [slot] = speed };
            var captures = new Dictionary<AnimationSlot, float>(o.SlotSpeedCaptures);
            if (!captures.ContainsKey(slot))
                captures[slot] = float.IsFinite(live) ? live : 1f;
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

    public AnimationResult PauseSlot(ActorId actor, AnimationSlot slot) =>
        SetSlotSpeedCore(actor, slot, 0f);

    public AnimationResult PlaySelectedSlot(ActorId actor, AnimationSlot slot)
    {
        var current = OverridesFor(actor);
        var selected = SelectedFor(actor, slot);
        ushort live = _port.Read(actor)?.TimelineFor(slot) ?? 0;
        if (selected is { } chosen && live != chosen)
        {
            var replayed = SelectSlot(
                actor,
                slot,
                chosen,
                slot == AnimationSlot.Base && current.BaseUsesNativeLoop);
            if (!replayed.Success)
                return replayed;
        }
        if (OverridesFor(actor).SlotSpeeds.TryGetValue(slot, out var speed) && speed == 0f)
            return ResumeSlotSpeedCore(actor, slot);
        return selected == null
            ? AnimationResult.Fail("Choose an animation first.")
            : AnimationResult.Ok();
    }

    private AnimationResult ResumeSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        var current = OverridesFor(actor);
        if (!current.SlotSpeeds.TryGetValue(slot, out var speed) || speed != 0f)
            return AnimationResult.Ok();
        if (!current.SlotResumeSpeeds.TryGetValue(slot, out var resume) ||
            !float.IsFinite(resume) || resume <= 0f)
            return AnimationResult.Fail("No previous nonzero layer speed is available.");
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

        var failures = new List<string>();
        // Restore speed first. A failed unpin must not clear a selection
        // whose paused native state still belongs to Poser.
        if (OverridesFor(actor).SlotSpeedCaptures.ContainsKey(slot))
        {
            var speed = ClearSlotSpeedCore(actor, slot);
            if (!speed.Success)
                return speed;
        }
        AnimationResult selection = slot switch
        {
            AnimationSlot.Base => ResetBaseSelection(actor),
            AnimationSlot.Lips => SelectedFor(actor, slot) != null
                ? SetLips(actor, 0)
                : AnimationResult.Ok(),
            _ => ResetBlendSelection(actor, slot),
        };
        if (!selection.Success)
            failures.Add(selection.Detail ?? "Layer restore failed.");
        return failures.Count == 0
            ? AnimationResult.Ok()
            : AnimationResult.Fail(string.Join("; ", failures));
    }

    private AnimationResult ResetBaseSelection(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (current.BaseTimeline == null)
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
            bool baseStillNeeded = o.SelectedSlots.Count > 0 || o.SlotCaptures.Count > 0;
            return o with
            {
                BaseTimeline = null,
                BaseUsesNativeLoop = false,
                BaseCapture = baseStillNeeded ? o.BaseCapture : null,
                BaseRepeatSuspended = false,
                LoopedSlots = loops,
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
            return AnimationResult.Fail("The layer restore point is unavailable.");

        var restored = incoming != 0
            ? _port.Blend(actor, incoming, current.BaseCapture, out _)
            : RestoreEmptySlot(actor, slot, current);
        if (!restored.Success)
            return AnimationResult.Fail(restored.Detail ?? "Layer restore failed.");

        // A blend restore uses the mode-changing sequencer route. Put the
        // captured base back when no explicit Base selection should remain.
        if (current.BaseTimeline == null && current.BaseCapture is { } capture)
        {
            var baseRestored = _port.RestoreBase(actor, capture);
            if (!baseRestored.Success)
                return AnimationResult.Fail(baseRestored.Detail ?? "Base restore failed.");
        }

        Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
            selected.Remove(slot);
            var captures = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
            captures.Remove(slot);
            return o with
            {
                SelectedSlots = selected,
                SlotCaptures = captures,
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
        var cancelled = _port.CancelActiveTimeline(actor);
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

    // ── Expression preview ───────────────────────────────────────────────

    /// <summary>Plays a facial preview without freezing its first frame.</summary>
    public AnimationResult HoldExpression(ActorId actor, ushort timeline)
    {
        if (Suspended() is { } blocked) return blocked;
        var played = SelectSlot(actor, AnimationSlot.Facial, timeline);
        if (!played.Success)
            return played;
        // Facial expressions often begin from a neutral frame. Let the game
        // advance the selected timeline; an explicit bake captures it later.
        Mutate(actor, o => o with { HeldExpression = timeline });
        return AnimationResult.Ok();
    }

    /// <summary>Releases a held facial expression.</summary>
    public AnimationResult ReleaseExpression(ActorId actor)
        => ResetSlot(actor, AnimationSlot.Facial);

    /// <summary>Restores the captured facial layer.</summary>
    public AnimationResult RestoreFacialLayer(ActorId actor)
        => ResetSlot(actor, AnimationSlot.Facial);

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
            var selected = new Dictionary<AnimationSlot, ushort>(remaining.SelectedSlots);
            foreach (var selectedSlot in selected.Keys.ToList())
                if (!slots.ContainsKey(selectedSlot))
                    selected.Remove(selectedSlot);
            remaining = remaining with
            {
                SlotCaptures = slots,
                SelectedSlots = selected,
                HeldExpression = slots.ContainsKey(AnimationSlot.Facial)
                    ? remaining.HeldExpression
                    : null,
            };
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
            remaining = remaining with
            {
                BaseCapture = null,
                BaseTimeline = null,
                BaseUsesNativeLoop = false,
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
