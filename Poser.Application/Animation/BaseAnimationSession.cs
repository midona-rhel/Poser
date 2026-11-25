using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Collections;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Chooses, plays, loops and resets the animation on each of an actor's layers.
/// It records what a layer played before Poser's first change, so Reset can put
/// that back. Choosing an animation never plays it.
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

    /// <summary>Remembers the chosen animation. Nothing in the game is read or changed.</summary>
    public Outcome ChooseSlot(
        ActorId actor, AnimationSlot slot, ushort timeline)
    {
        if (!AnimationSlots.Selectable.Contains(slot))
            return Outcome.Fail("This animation layer is not selectable.");
        if (timeline == 0)
            return Outcome.Fail("Choose an animation first.");
        // The game decides which layer an animation plays on. One whose layer it
        // doesn't name plays on the Full body. Lips takes any animation.
        var layer = _timeline.TimelineSlot(timeline) ?? AnimationSlot.Base;
        if (slot != AnimationSlot.Lips && layer != slot)
            return Outcome.Fail(
                $"Animation {timeline} does not play on the {AnimationSlots.DisplayName(slot)} layer.");

        _store.Mutate(actor, o => o with { SelectedSlots = o.SelectedSlots.With(slot, timeline) });
        return Outcome.Ok();
    }

    /// <summary>The tracked loops with the Full body repeating <paramref name="timeline"/>, or not repeating.</summary>
    private static IReadOnlyDictionary<AnimationSlot, ushort> WithBaseLoop(
        AnimationOverrides owned, bool repeat, ushort timeline) =>
        repeat
            ? owned.LoopedSlots.With(AnimationSlot.Base, timeline)
            : owned.LoopedSlots.Without(AnimationSlot.Base);

    private Outcome PlayBaseCore(
        ActorId actor,
        ushort timeline,
        AnimationOverrides before,
        bool loopWanted)
    {
        var result = _timeline.PlayBase(actor, timeline, before.BaseCapture, out var captured);
        if (!result.Success)
            return result;
        // The game only repeats animations that belong to the Full body. One
        // that it will not repeat keeps playing, and the refusal is returned.
        var repeat = loopWanted ? _timeline.SetForceLoop(actor, timeline) : Outcome.Ok();
        _store.Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            BaseTimeline = timeline,
            LoopedSlots = WithBaseLoop(o, loopWanted && repeat.Success, timeline),
        });
        return repeat;
    }

    /// <summary>Plays a Full body animation again while the actor keeps sitting
    /// or lying down, for undo after it has put that stance back.</summary>
    public Outcome ReplayBaseInPosture(ActorId actor, ushort timeline)
    {
        var played = _timeline.ReplayBase(actor, timeline);
        if (!played.Success)
            return played;
        _timeline.SetLayerOwned(actor, AnimationSlot.Base, true);
        _store.Mutate(actor, o => o with { BaseTimeline = timeline });
        return Outcome.Ok();
    }

    public Outcome BlendCore(
        ActorId actor, ushort timeline, AnimationSlot? landing)
    {
        var current = _store.For(actor);
        bool suspendBaseRepeat = landing is { } target && target != AnimationSlot.Base &&
            current.LoopedSlots.ContainsKey(AnimationSlot.Base);
        ushort suspendedTimeline = suspendBaseRepeat
            ? current.LoopedSlots[AnimationSlot.Base]
            : (ushort)0;

        // Record what the layer plays now, before anything changes.
        bool captureSlot = landing is { } slot &&
            slot != AnimationSlot.Base &&
            !current.SlotCaptures.ContainsKey(slot);
        // Read only returns nothing when the actor is gone, and then the writes below fail too.
        ushort incoming = captureSlot
            ? _timeline.Read(actor)?.TimelineFor(landing!.Value) ?? 0
            : (ushort)0;

        // Playing a layer makes the game drop the Full body repeat. Turn the
        // repeat off first and back on afterwards.
        if (suspendBaseRepeat)
        {
            var cleared = _timeline.SetForceLoop(actor, 0);
            if (!cleared.Success)
                return cleared;
        }

        // Turning the repeat back on cannot be refused: the actor was just found,
        // and the game accepted this same animation as a repeat before.
        var result = _timeline.Blend(actor, timeline, current.BaseCapture, out var captured);
        if (suspendBaseRepeat)
            _timeline.SetForceLoop(actor, suspendedTimeline);
        if (!result.Success)
            return result;
        if (captured is { } taken)
            _store.Mutate(actor, o => o with { BaseCapture = o.BaseCapture ?? taken });
        if (captureSlot)
        {
            var landed = landing!.Value;
            _store.Mutate(actor, o => o.SlotCaptures.ContainsKey(landed)
                ? o
                : o with { SlotCaptures = o.SlotCaptures.With(landed, incoming) });
        }
        return Outcome.Ok();
    }

    /// <summary>Turns Loop on or off for one layer.</summary>
    public Outcome SetSlotLoop(
        ActorId actor, AnimationSlot slot, ushort timeline, bool on)
    {
        var current = _store.For(actor);
        if (!on && current.LoopedSlots.ContainsKey(slot))
        {
            if (slot != AnimationSlot.Base)
                _timeline.ClearSlotLoop(actor, slot);
            else if (_timeline.SetForceLoop(actor, 0) is { Success: false } actorGone)
                return actorGone;
        }
        _store.Mutate(actor, o => on
            ? o with { LoopWantedSlots = o.LoopWantedSlots.With(slot) }
            : o with
            {
                LoopedSlots = o.LoopedSlots.Without(slot),
                LoopWantedSlots = o.LoopWantedSlots.Without(slot),
            });
        if (!on)
            return Outcome.Ok();

        if (slot != AnimationSlot.Base)
        {
            // Loop only takes effect on the animation Apply played last, and
            // only while it is still playing. The switch never starts one.
            ushort layerTarget = current.AppliedSlots.GetValueOrDefault(slot);
            ushort layerLive = _timeline.Read(actor)?.TimelineFor(slot) ?? 0;
            if (layerTarget == 0 || layerLive != layerTarget)
                return Outcome.Ok();
            var layerArmed = _timeline.SetSlotLoop(actor, slot, layerTarget);
            if (!layerArmed.Success)
                return layerArmed;
            _store.Mutate(actor, o => o with { LoopedSlots = o.LoopedSlots.With(slot, layerTarget) });
            return Outcome.Ok();
        }

        // With no animation to repeat, only the choice is remembered. Otherwise
        // repeat the given animation, or the Full body animation Poser applied.
        ushort target = timeline != 0 ? timeline : current.BaseTimeline ?? 0;
        if (target == 0)
            return Outcome.Ok();
        // CaptureBase only returns nothing when the actor is gone, and then turning repeat on fails too.
        var captured = current.BaseCapture == null ? _timeline.CaptureBase(actor) : null;
        var armed = _timeline.SetForceLoop(actor, target);
        if (!armed.Success)
            return armed;
        _store.Mutate(actor, o => o with
        {
            BaseCapture = o.BaseCapture ?? captured,
            LoopedSlots = o.LoopedSlots.With(AnimationSlot.Base, target),
        });
        return Outcome.Ok();
    }

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

        _store.Mutate(actor, o => o with { AppliedSlots = o.AppliedSlots.With(slot, selected) });
        if (!_store.For(actor).LoopWantedSlots.Contains(slot))
            return Outcome.Ok();

        var armed = _timeline.SetSlotLoop(actor, slot, selected);
        if (!armed.Success)
            return armed;
        _store.Mutate(actor, o => o with { LoopedSlots = o.LoopedSlots.With(slot, selected) });
        return Outcome.Ok();
    }

    private Outcome PlayBaseEmoteCore(
        ActorId actor, TimelineEntry entry, AnimationOverrides before)
    {
        ushort timeline = (ushort)entry.TimelineId;
        bool armRepeat = before.LoopWantedSlots.Contains(AnimationSlot.Base);
        // CaptureBase only returns nothing when the actor is gone, and then both play routes below fail too.
        var firstCapture = before.BaseCapture ?? _timeline.CaptureBase(actor);
        var played = _timeline.PlayEmote(actor, entry.EmoteId, timeline);
        // If the emote can't start from its intro, play its animation directly.
        if (!played.Success)
            return PlayBaseCore(actor, timeline, before, armRepeat);
        var repeat = armRepeat ? _timeline.SetForceLoop(actor, timeline) : Outcome.Ok();
        _store.Mutate(actor, o => o with
        {
            BaseTimeline = timeline,
            BaseCapture = o.BaseCapture ?? firstCapture,
            LoopedSlots = WithBaseLoop(o, armRepeat && repeat.Success, timeline),
        });
        return repeat;
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
            var selected = o.SelectedSlots.Without(AnimationSlot.Base);
            // Another layer Poser changed still needs the capture for its own Reset.
            bool baseStillNeeded = selected.Count > 0 || o.SlotCaptures.Count > 0;
            return o with
            {
                SelectedSlots = selected,
                BaseTimeline = null,
                BaseCapture = baseStillNeeded ? o.BaseCapture : null,
                LoopedSlots = o.LoopedSlots.Without(AnimationSlot.Base),
                LoopWantedSlots = preserveLoopIntent
                    ? o.LoopWantedSlots
                    : o.LoopWantedSlots.Without(AnimationSlot.Base),
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
            // The animation was chosen but never applied, so there is nothing
            // in the game to put back.
            _store.Mutate(actor, o => o with
            {
                SelectedSlots = o.SelectedSlots.Without(slot),
                LoopWantedSlots = o.LoopWantedSlots.Without(slot),
            });
            return Outcome.Ok();
        }

        if (current.LoopedSlots.ContainsKey(slot))
            _timeline.ClearSlotLoop(actor, slot);

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

        // Putting a layer back can disturb the Full body. Restore the Full body
        // too, unless Poser is playing its own Full body animation.
        Outcome? baseRestored = null;
        if (restored.Success && current.BaseTimeline == null &&
            current.BaseCapture is { } capture)
        {
            baseRestored = _timeline.RestoreBase(actor, capture);
        }

        // As in BlendCore: the actor was just found and the game accepted this repeat before.
        if (preserveRepeat)
            _timeline.SetForceLoop(actor, repeated);
        if (!restored.Success)
            return restored;
        if (baseRestored is { Success: false } baseFailure)
            return baseFailure;

        _store.Mutate(actor, o =>
        {
            var selected = o.SelectedSlots.Without(slot);
            var captures = o.SlotCaptures.Without(slot);
            return o with
            {
                SelectedSlots = selected,
                AppliedSlots = o.AppliedSlots.Without(slot),
                SlotCaptures = captures,
                LoopedSlots = o.LoopedSlots.Without(slot),
                LoopWantedSlots = o.LoopWantedSlots.Without(slot),
                HeldExpression = slot == AnimationSlot.Facial ? null : o.HeldExpression,
                // Nothing of Poser's is left on this actor's layers: drop the Full body capture.
                BaseCapture = o.BaseTimeline == null && selected.Count == 0 && captures.Count == 0
                    ? null
                    : o.BaseCapture,
            };
        });
        return Outcome.Ok();
    }

    /// <summary>Stops a layer that played nothing before Poser's first change.</summary>
    private Outcome RestoreEmptySlot(
        ActorId actor, AnimationSlot slot, AnimationOverrides current)
    {
        var reading = _timeline.Read(actor);
        var immediateBase = _timeline.CaptureBase(actor);
        // Stop the layer and clear its animation ID. Without that, restoring the
        // Full body below would start the layer again. This only fails when the
        // actor is gone, which is also the only time there is no reading.
        var cancelled = _timeline.ClearSlotTimeline(actor, slot);
        if (!cancelled.Success)
            return cancelled;

        // Stopping one layer can stop others. Start again only the layers that
        // stopped: replaying one that still plays would restart it.
        var after = _timeline.Read(actor)!;
        var failures = new List<string>();
        var retrySlots = new Dictionary<AnimationSlot, ushort>();
        foreach (var survivor in reading!.Slots)
        {
            if (survivor.Slot is AnimationSlot.Base || survivor.Slot == slot ||
                survivor.TimelineId == 0)
                continue;
            if (after.TimelineFor(survivor.Slot) == survivor.TimelineId)
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
        if (immediateBase is { } baseline &&
            after.TimelineFor(AnimationSlot.Base) != reading.TimelineFor(AnimationSlot.Base))
        {
            var baseRestored = _timeline.RestoreBase(actor, baseline);
            if (!baseRestored.Success)
                failures.Add(baseRestored.Detail ?? "Base rollback failed.");
        }
        // Remember the layers that could not be started again, so their Reset can.
        if (failures.Count > 0)
            _store.Mutate(actor, o =>
            {
                var captures = new Dictionary<AnimationSlot, ushort>(o.SlotCaptures);
                foreach (var (failedSlot, timeline) in retrySlots)
                    captures.TryAdd(failedSlot, timeline);
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

    /// <summary>
    /// Sets the lip movement. Choosing None puts back what the lips did before,
    /// because writing zero would mean "no speech" to the game.
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
        var current = _store.For(actor);
        // Read only returns nothing when the actor is gone, and then the write below fails too.
        ushort? captured = current.LipsCapture == null
            ? _timeline.Read(actor)?.LipsOverride
            : null;
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
            _store.Mutate(actor, o => o with { SelectedSlots = o.SelectedSlots.Without(AnimationSlot.Lips) });
            return Outcome.Ok();
        }
        ushort target = current.LipsCapture ?? 0;
        var result = _timeline.SetLips(actor, target);
        if (!result.Success)
            return result;
        _store.Mutate(actor, o => o with
        {
            SelectedSlots = o.SelectedSlots.Without(AnimationSlot.Lips),
            Lips = null,
            LipsCapture = null,
        });
        return Outcome.Ok();
    }
}
