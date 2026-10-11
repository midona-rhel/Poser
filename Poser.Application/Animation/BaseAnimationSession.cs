using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Base and layered timeline ownership: selection, playback, repeat arms,
/// lips, and the restore points each first write records.
/// </summary>
internal sealed class BaseAnimationSession
{
    private readonly AnimationOverrideStore _store;
    private readonly IAnimationTimelinePort _timeline;

    public BaseAnimationSession(AnimationOverrideStore store, IAnimationTimelinePort timeline)
    {
        _store = store;
        _timeline = timeline;
    }

    public ushort? SelectedFor(ActorId actor, AnimationSlot slot)
    {
        var owned = _store.For(actor);
        return owned.SelectedSlots.TryGetValue(slot, out var timeline)
            ? timeline
            : null;
    }

    /// <summary>Stages a selection without reading or writing native state.</summary>
    public Outcome ChooseSlot(
        ActorId actor, AnimationSlot slot, ushort timeline)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (!AnimationSlots.Selectable.Contains(slot))
            return Outcome.Fail("This animation layer is not selectable.");
        if (timeline == 0)
            return Outcome.Fail("Choose an animation first.");
        if (slot is not AnimationSlot.Base and not AnimationSlot.Lips &&
            _timeline.TimelineSlot(timeline) != slot)
            return Outcome.Fail(
                $"Timeline {timeline} does not route to {AnimationSlots.DisplayName(slot)}.");

        _store.Mutate(actor, o =>
        {
            var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots)
            {
                [slot] = timeline,
            };
            return o with { SelectedSlots = selected };
        });
        return Outcome.Ok();
    }

    private Outcome PlayBaseCore(
        ActorId actor,
        ushort timeline,
        AnimationOverrides before,
        bool loopWanted)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        bool armRepeat = loopWanted;
        // A retarget needs the immediate native state, not the session's
        // original restore point, if repeat arming has to be rolled back.
        var rollbackCapture = armRepeat && before.BaseCapture != null
            ? _timeline.CaptureBase(actor)
            : null;
        var result = _timeline.PlayBase(actor, timeline, before.BaseCapture, out var captured);
        if (!result.Success)
            return result;
        if (armRepeat)
        {
            var armed = _timeline.SetForceLoop(actor, timeline);
            if (!armed.Success)
            {
                var baseline = rollbackCapture ?? captured ?? before.BaseCapture;
                var rolledBack = baseline is { } restore
                    ? _timeline.RestoreBase(actor, restore)
                    : Outcome.Fail("The base restore point is unavailable.");
                if (rolledBack.Success)
                    return armed;

                // The play landed but rollback did not. Keep the original
                // restore point so Reset can retry instead of abandoning it.
                var ownedCapture = before.BaseCapture ?? captured ?? rollbackCapture;
                _store.Mutate(actor, o =>
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
                return Outcome.Fail(
                    $"{armed.Detail ?? "Repeat arm failed."} " +
                    $"Rollback failed: {rolledBack.Detail ?? "base restore failed."}");
            }
        }
        if (captured is { } taken)
            _store.Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    public Outcome BlendCore(
        ActorId actor, ushort timeline, AnimationSlot? landing)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var current = _store.For(actor);
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
            var reading = _timeline.Read(actor);
            if (reading == null)
                return Outcome.Fail("The layer restore point is unavailable.");
            incoming = reading.TimelineFor(landing!.Value);
        }

        // SetTimelineId clears the global force while routing by native slot.
        // Release it for the layer write, then restore the same Base force.
        if (suspendBaseRepeat)
        {
            var cleared = _timeline.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }

        var result = _timeline.Blend(actor, timeline, current.BaseCapture, out var captured);
        if (!result.Success)
        {
            if (suspendBaseRepeat)
            {
                var restored = _timeline.SetForceLoop(actor, suspendedTimeline);
                if (!restored.Success)
                {
                    _store.Mutate(actor, o =>
                    {
                        var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                        loops.Remove(AnimationSlot.Base);
                        return o with { LoopedSlots = loops };
                    });
                    return Outcome.Fail(
                        $"{result.Detail ?? "Blend failed."} Repeat restore failed: " +
                        (restored.Detail ?? "full-body repeat arm failed."));
                }
            }
            return result;
        }
        // The layer write has landed. Record its restore points before the
        // independent Base-force rearm can fail.
        if (captured is { } taken)
            _store.Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        if (captureSlot)
        {
            var landed = landing!.Value;
            _store.Mutate(actor, o =>
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
            var restored = _timeline.SetForceLoop(actor, suspendedTimeline);
            if (!restored.Success)
            {
                _store.Mutate(actor, o =>
                {
                    var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                    loops.Remove(AnimationSlot.Base);
                    return o with { LoopedSlots = loops };
                });
                return Outcome.Fail(
                    "Layer playback landed, but full-body repeat could not be " +
                    $"restored: {restored.Detail ?? "repeat arm failed."}");
            }
        }
        return Outcome.Ok();
    }

    /// <summary>Sets repeat intent for one slot.</summary>
    public Outcome SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on) =>
        SetSlotLoopCore(actor, slot, timeline, on);

    private Outcome SetSlotLoopCore(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return Outcome.Fail(
                "Repeat is unavailable for this layer: exact replay is unverified.");
        var current = _store.For(actor);
        if (!on && current.LoopedSlots.ContainsKey(slot))
        {
            var cleared = slot == AnimationSlot.Base
                ? _timeline.SetForceLoop(actor, 0)
                : _timeline.ClearSlotLoop(actor, slot);
            if (!cleared.Success)
                return cleared;
        }
        _store.Mutate(actor, o =>
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
            return Outcome.Ok();

        if (slot == AnimationSlot.UpperBody)
        {
            // The switch may resume ownership only when Apply's last target
            // is still live; it never starts or retargets Upper playback.
            ushort upperTarget = current.AppliedSlots.GetValueOrDefault(slot);
            ushort liveUpper = _timeline.Read(actor)?.TimelineFor(slot) ?? 0;
            if (upperTarget == 0 || liveUpper != upperTarget)
                return Outcome.Ok();
            var armedUpper = _timeline.SetSlotLoop(actor, slot, upperTarget);
            if (!armedUpper.Success)
                return armedUpper;
            _store.Mutate(actor, o => o with
            {
                LoopedSlots = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
                {
                    [slot] = upperTarget,
                },
            });
            return Outcome.Ok();
        }

        // Zero means sticky intent. Only a Poser selection or an explicit
        // timeline may establish native base ownership.
        ushort target = timeline != 0 ? timeline : current.BaseTimeline ?? 0;
        if (target == 0)
            return Outcome.Ok();
        if (!SupportsForceLoop)
            return Outcome.Fail("Full-body repeat is unavailable for this client layout.");
        var captured = current.BaseCapture == null ? _timeline.CaptureBase(actor) : null;
        if (current.BaseCapture == null && captured == null)
            return Outcome.Fail("The base restore point is unavailable.");
        var armed = _timeline.SetForceLoop(actor, target);
        if (!armed.Success)
            return armed;
        _store.Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            LoopedSlots = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
            {
                [AnimationSlot.Base] = target,
            },
        });
        return Outcome.Ok();
    }

    /// <summary>Whether full-body repeat is available.</summary>
    public bool SupportsForceLoop => _timeline.SupportsForceLoop;

    public Outcome ApplySelectedSlotCore(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry = null)
    {
        var current = _store.For(actor);
        if (!current.SelectedSlots.TryGetValue(slot, out var selected))
            return Outcome.Fail("Choose an animation first.");
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

        _store.Mutate(actor, o =>
        {
            var applied = new Dictionary<AnimationSlot, ushort>(o.AppliedSlots)
            {
                [slot] = selected,
            };
            return o with { AppliedSlots = applied };
        });
        if (slot != AnimationSlot.UpperBody ||
            !_store.For(actor).LoopWantedSlots.Contains(slot))
            return Outcome.Ok();

        var armed = _timeline.SetSlotLoop(actor, slot, selected);
        if (!armed.Success)
            return armed;
        _store.Mutate(actor, o =>
        {
            var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots)
            {
                [slot] = selected,
            };
            return o with { LoopedSlots = loops };
        });
        return Outcome.Ok();
    }

    private Outcome PlayBaseEmoteCore(
        ActorId actor, TimelineEntry entry, AnimationOverrides before)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        bool armRepeat = before.LoopWantedSlots.Contains(AnimationSlot.Base);
        var firstCapture = before.BaseCapture ?? _timeline.CaptureBase(actor);
        if (firstCapture == null)
            return Outcome.Fail("The base restore point is unavailable.");
        var rollbackCapture = before.BaseCapture != null
            ? _timeline.CaptureBase(actor)
            : firstCapture;
        var played = _timeline.PlayEmote(actor, entry.EmoteId);
        if (!played.Success)
            return played;
        if (armRepeat)
        {
            var armed = _timeline.SetForceLoop(actor, (ushort)entry.TimelineId);
            if (!armed.Success)
            {
                var baseline = rollbackCapture ?? before.BaseCapture;
                var rolledBack = baseline is { } restore
                    ? _timeline.RestoreBase(actor, restore)
                    : Outcome.Fail("The base restore point is unavailable.");
                if (rolledBack.Success)
                    return armed;
                _store.Mutate(actor, o => o with
                {
                    BaseTimeline = (ushort)entry.TimelineId,
                    BaseCapture = o.BaseCapture ?? baseline,
                });
                return Outcome.Fail(
                    $"{armed.Detail ?? "Repeat arm failed."} " +
                    $"Rollback failed: {rolledBack.Detail ?? "base restore failed."}");
            }
        }
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    public Outcome ResetBaseSelection(
        ActorId actor, bool preserveLoopIntent = false)
    {
        var current = _store.For(actor);
        if (current.BaseTimeline == null &&
            !current.SelectedSlots.ContainsKey(AnimationSlot.Base))
            return Outcome.Ok();
        if (current.LoopedSlots.ContainsKey(AnimationSlot.Base))
        {
            var cleared = _timeline.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }
        if (current.BaseCapture is { } capture)
        {
            var restored = _timeline.RestoreBase(actor, capture);
            if (!restored.Success)
                return restored;
        }
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    public Outcome ResetBlendSelection(ActorId actor, AnimationSlot slot)
    {
        var current = _store.For(actor);
        if (!current.SelectedSlots.ContainsKey(slot))
            return Outcome.Ok();
        if (!current.SlotCaptures.TryGetValue(slot, out var incoming))
        {
            // Choose is staging-only, so an unapplied row has nothing native to undo.
            _store.Mutate(actor, o =>
            {
                var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
                selected.Remove(slot);
                var wanted = new HashSet<AnimationSlot>(o.LoopWantedSlots);
                wanted.Remove(slot);
                return o with { SelectedSlots = selected, LoopWantedSlots = wanted };
            });
            return Outcome.Ok();
        }

        if (current.LoopedSlots.ContainsKey(slot))
        {
            var loopCleared = _timeline.ClearSlotLoop(actor, slot);
            if (!loopCleared.Success)
                return loopCleared;
        }

        bool preserveRepeat = current.LoopedSlots.TryGetValue(
            AnimationSlot.Base, out var repeated);
        if (preserveRepeat)
        {
            var cleared = _timeline.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }

        var restored = incoming != 0
            ? _timeline.Blend(actor, incoming, current.BaseCapture, out _)
            : RestoreEmptySlot(actor, slot, current);

        // A blend restore uses the mode-changing sequencer route. Put the
        // captured base back when no explicit Base selection should remain.
        Outcome? baseRestored = null;
        if (restored.Success && current.BaseTimeline == null &&
            current.BaseCapture is { } capture)
        {
            baseRestored = _timeline.RestoreBase(actor, capture);
        }

        Outcome? repeatRestored = null;
        if (preserveRepeat)
            repeatRestored = _timeline.SetForceLoop(actor, repeated);
        if (repeatRestored is { Success: false } repeatFailure)
        {
            _store.Mutate(actor, o =>
            {
                var loops = new Dictionary<AnimationSlot, ushort>(o.LoopedSlots);
                loops.Remove(AnimationSlot.Base);
                return o with { LoopedSlots = loops };
            });
            return Outcome.Fail(
                $"Layer restore could not rearm full-body repeat: " +
                (repeatFailure.Detail ?? "repeat arm failed."));
        }
        if (!restored.Success)
            return restored;
        if (baseRestored is { Success: false } baseFailure)
            return baseFailure;

        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    private Outcome RestoreEmptySlot(
        ActorId actor, AnimationSlot slot, AnimationOverrides current)
    {
        var reading = _timeline.Read(actor);
        if (reading == null)
            return Outcome.Fail("The actor is no longer available.");
        var immediateBase = _timeline.CaptureBase(actor);
        // Zero the slot's own id entries first: a bare cancel left them and
        // the base restore below re-scheduled the layer from them.
        var cancelled = _timeline.ClearSlotTimeline(actor, slot);
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
                ? _timeline.SetLips(actor, reading.LipsOverride)
                : _timeline.Blend(actor, survivor.TimelineId, current.BaseCapture, out _);
            if (!replayed.Success)
            {
                failures.Add(replayed.Detail ?? $"{survivor.Slot} replay failed.");
                retrySlots[survivor.Slot] = survivor.TimelineId;
            }
        }
        if (immediateBase is { } baseline)
        {
            var baseRestored = _timeline.RestoreBase(actor, baseline);
            if (!baseRestored.Success)
                failures.Add(baseRestored.Detail ?? "Base rollback failed.");
        }
        if (failures.Count > 0)
            _store.Mutate(actor, o =>
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
            ? Outcome.Ok()
            : Outcome.Fail(string.Join("; ", failures));
    }

    // ── Lips ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the lip override. Selecting None restores the captured incoming
    /// timeline because zero is a native "no speech timeline" value.
    /// </summary>
    public Outcome SetLips(ActorId actor, ushort timeline)
    {
        if (timeline == 0)
            return ResetLipsSelection(actor);
        var chosen = ChooseSlot(actor, AnimationSlot.Lips, timeline);
        return chosen.Success ? SetLipsCore(actor, timeline) : chosen;
    }

    private Outcome SetLipsCore(ActorId actor, ushort timeline)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var current = _store.For(actor);
        ushort? captured = null;
        if (current.LipsCapture == null)
        {
            var reading = _timeline.Read(actor);
            if (reading == null)
                return Outcome.Fail("The lips restore point is unavailable.");
            captured = reading.LipsOverride;
        }
        var result = _timeline.SetLips(actor, timeline);
        if (!result.Success)
            return result;

        _store.Mutate(actor, o => o with
        {
            Lips = timeline,
            LipsCapture = o.LipsCapture ?? captured,
        });
        return Outcome.Ok();
    }

    public Outcome ResetLipsSelection(ActorId actor)
    {
        var current = _store.For(actor);
        if (current.Lips == null && current.LipsCapture == null)
        {
            _store.Mutate(actor, o =>
            {
                var selected = new Dictionary<AnimationSlot, ushort>(o.SelectedSlots);
                selected.Remove(AnimationSlot.Lips);
                return o with { SelectedSlots = selected };
            });
            return Outcome.Ok();
        }
        ushort target = current.LipsCapture ?? 0;
        var result = _timeline.SetLips(actor, target);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }
}
