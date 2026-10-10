using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Whole-actor and per-layer speed ownership: pause, resume, slot holds
/// and the captured speeds a release restores.
/// </summary>
internal sealed class SpeedAnimationSession
{
    private readonly AnimationOverrideStore _store;
    private readonly IAnimationTimelinePort _timeline;
    private readonly IAnimationSpeedPort _port;

    public SpeedAnimationSession(
        AnimationOverrideStore store, IAnimationTimelinePort timeline, IAnimationSpeedPort port)
    {
        _store = store;
        _timeline = timeline;
        _port = port;
    }

    public Outcome SetSpeed(ActorId actor, float speed) =>
        SetSpeedCore(actor, speed);

    private Outcome SetSpeedCore(ActorId actor, float speed)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { OverallSpeed = speed });
        return Outcome.Ok();
    }

    public Outcome ClearSpeed(ActorId actor) => ClearSpeedCore(actor);

    private Outcome ClearSpeedCore(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var result = _port.ClearOverallSpeed(actor);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { OverallSpeed = null });
        return Outcome.Ok();
    }

    public bool IsPaused(ActorId actor) => _store.For(actor).IsPaused;

    public Outcome Pause(ActorId actor)
    {
        _store.Trace?.Invoke($"Pause(all) {actor}");
        return SetSpeed(actor, 0f);
    }

    /// <summary>Resume drops the override rather than writing 1, so an
    /// actor the game is driving at its own speed keeps it.</summary>
    /// <summary>Play-all: releases every hold — the per-slot pauses the
    /// layer-play conversion parked, then the whole-actor speed. The
    /// sidebar's play is the ONLY verb that releases everything (ruled
    /// 2026-09-01).</summary>
    public Outcome Resume(ActorId actor)
    {
        _store.Trace?.Invoke($"Resume(all) {actor}");
        foreach (var (slot, speed) in _store.For(actor).SlotSpeeds)
        {
            if (speed == 0f)
                ResumeSlotSpeedCore(actor, slot);
        }
        return ClearSpeed(actor);
    }

    /// <summary>Whether any live layer is actually MOVING. The sidebar
    /// button offers Pause while this is true and Resume otherwise
    /// (ruled 2026-09-01): pause stops the stack, play overrides every
    /// individual hold.</summary>
    public bool AnyPlaying(ActorId actor)
    {
        if (IsPaused(actor))
            return false;
        if (_timeline.Read(actor) is not { } reading)
            return false;
        var owned = _store.For(actor);
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
    public Outcome ResumeForLayerPlay(ActorId actor, AnimationSlot playing)
    {
        var current = _store.For(actor);
        if (_timeline.Read(actor) is { } reading)
        {
            foreach (var slotReading in reading.Slots)
            {
                if (slotReading.TimelineId == 0
                    || slotReading.Slot == playing
                    || current.SlotSpeeds.ContainsKey(slotReading.Slot))
                    continue;
                var held = SetSlotSpeedCore(actor, slotReading.Slot, 0f);
                _store.Trace?.Invoke(
                    $"  hold slot={slotReading.Slot} (tl {slotReading.TimelineId})"
                    + $" -> {(held.Success ? "ok" : held.Detail)}");
                if (!held.Success)
                    return held;
            }
        }
        _store.Trace?.Invoke($"  lift overall for {playing}");
        return ClearSpeedCore(actor);
    }

    /// <summary>Rewinds paused animation controls.</summary>
    public Outcome RewindPausedControls(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        return _port.RewindPausedControls(actor);
    }

    public Outcome SetSlotSpeed(
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
        _store.Trace?.Invoke($"collapse: every layer held on {actor}");
        SetSpeed(actor, 0f);
    }

    public Outcome SetSlotSpeedCore(
        ActorId actor, AnimationSlot slot, float speed, float? firstCapture = null)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var current = _store.For(actor);
        float live;
        if (current.SlotSpeeds.TryGetValue(slot, out var ownedSpeed))
            live = ownedSpeed;
        else
        {
            var reading = _timeline.Read(actor);
            if (reading == null)
                return Outcome.Fail("The layer speed restore point is unavailable.");
            live = reading.SpeedFor(slot);
        }
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    public Outcome ClearSlotSpeed(ActorId actor, AnimationSlot slot) =>
        ClearSlotSpeedCore(actor, slot);

    public Outcome ClearSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var current = _store.For(actor);
        float restore = current.SlotSpeedCaptures.TryGetValue(slot, out var captured)
            ? captured
            : 1f;
        var result = _port.ClearSlotSpeed(actor, slot, restore);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    public Outcome PauseSlot(ActorId actor, AnimationSlot slot)
    {
        var held = SetSlotSpeedCore(actor, slot, 0f);
        if (!held.Success)
            return held;
        CollapseWhenNothingPlays(actor);
        return held;
    }

    public Outcome ResumeSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        var current = _store.For(actor);
        if (!current.SlotSpeeds.TryGetValue(slot, out var speed) || speed != 0f)
            return Outcome.Ok();
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
}
