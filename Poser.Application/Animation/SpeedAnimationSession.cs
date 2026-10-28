using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Changes animation speed for the whole actor or an individual layer.
/// Keeps previous layer speeds so a pause can be resumed or reset.
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

    public Outcome SetSpeed(ActorId actor, float speed)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var result = _port.SetOverallSpeed(actor, speed);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { OverallSpeed = speed });
        return Outcome.Ok();
    }

    public Outcome ClearSpeed(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var result = _port.ClearOverallSpeed(actor);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with { OverallSpeed = null });
        return Outcome.Ok();
    }

    public bool IsPaused(ActorId actor) => _store.For(actor).IsPaused;

    /// <summary>Whether Poser holds this one layer still (its speed is 0).</summary>
    public bool IsSlotHeld(ActorId actor, AnimationSlot slot) =>
        _store.For(actor).SlotSpeeds.TryGetValue(slot, out var speed) && speed == 0f;

    public Outcome Pause(ActorId actor)
    {
        _store.Trace?.Invoke($"Pause(all) {actor}");
        return SetSpeed(actor, 0f);
    }

    /// <summary>Resumes paused animation layers and clears the whole-actor speed override.
    /// A held facial expression stays in place until the Facial controls release it.</summary>
    public Outcome Resume(ActorId actor)
    {
        _store.Trace?.Invoke($"Resume(all) {actor}");
        var current = _store.For(actor);
        foreach (var (slot, speed) in current.SlotSpeeds)
        {
            if (slot == AnimationSlot.Facial && current.HeldExpression != null)
                continue;
            if (speed == 0f)
                ResumeSlotSpeedCore(actor, slot);
        }
        return ClearSpeed(actor);
    }

    /// <summary>Reports a nonempty layer with no zero-speed override, unless
    /// the whole actor is paused. This reads playback state, not bone movement.</summary>
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

    /// <summary>Pauses other live layers that have no speed override, then
    /// clears the whole-actor speed override so the chosen layer can play.</summary>
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
                var held = SetSlotSpeed(actor, slotReading.Slot, 0f);
                _store.Trace?.Invoke(
                    $"  hold slot={slotReading.Slot} (tl {slotReading.TimelineId})"
                    + $" -> {(held.Success ? "ok" : held.Detail)}");
                if (!held.Success)
                    return held;
            }
        }
        _store.Trace?.Invoke($"  lift overall for {playing}");
        return ClearSpeed(actor);
    }

    /// <summary>Rewinds paused animation controls.</summary>
    public Outcome RewindPausedControls(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        return _port.RewindPausedControls(actor);
    }

    public Outcome SetSlotSpeed(
        ActorId actor, AnimationSlot slot, float speed, float? firstCapture = null)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var current = _store.For(actor);
        // Without a reading the actor is gone, and the port write below fails too.
        float live = current.SlotSpeeds.TryGetValue(slot, out var ownedSpeed)
            ? ownedSpeed
            : _timeline.Read(actor)?.SpeedFor(slot) ?? 1f;
        var result = _port.SetSlotSpeed(actor, slot, speed);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o =>
        {
            var speeds = new Dictionary<AnimationSlot, float>(o.SlotSpeeds) { [slot] = speed };
            var captures = new Dictionary<AnimationSlot, float>(o.SlotSpeedCaptures);
            if (!captures.ContainsKey(slot))
                captures[slot] = firstCapture ?? live;
            var resume = new Dictionary<AnimationSlot, float>(o.SlotResumeSpeeds);
            if (speed > 0f)
                resume[slot] = speed;
            else if (live > 0f)
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

    public Outcome ClearSlotSpeed(ActorId actor, AnimationSlot slot)
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
        // Pause only this layer. Pausing the whole actor here would also freeze
        // any animation the user starts on another layer afterwards.
        return SetSlotSpeed(actor, slot, 0f);
    }

    public Outcome ResumeSlotSpeedCore(ActorId actor, AnimationSlot slot)
    {
        var current = _store.For(actor);
        if (!current.SlotSpeeds.TryGetValue(slot, out var speed) || speed != 0f)
            return Outcome.Ok();
        // A layer that was already still when Poser paused it has no remembered
        // resume speed. Use its original positive speed, or normal speed (1).
        if (!current.SlotResumeSpeeds.TryGetValue(slot, out var resume))
            resume = current.SlotSpeedCaptures.TryGetValue(slot, out var captured) && captured > 0f
                ? captured
                : 1f;
        return SetSlotSpeed(actor, slot, resume);
    }
}
