using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>A facial expression applied and pinned at its frame, and its release.</summary>
internal sealed class HeldExpressionSession
{
    private readonly AnimationOverrideStore _store;
    private readonly IAnimationTimelinePort _timeline;
    private readonly IAnimationSpeedPort _speedPort;
    private readonly BaseAnimationSession _base;
    private readonly SpeedAnimationSession _speed;

    public HeldExpressionSession(
        AnimationOverrideStore store, IAnimationTimelinePort timeline,
        IAnimationSpeedPort speedPort, BaseAnimationSession baseSession,
        SpeedAnimationSession speed)
    {
        _store = store;
        _timeline = timeline;
        _speedPort = speedPort;
        _base = baseSession;
        _speed = speed;
    }

    /// <summary>Applies an expression and immediately pins its facial frame.</summary>
    public Outcome HoldExpression(ActorId actor, ushort timeline)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        var chosen = _base.ChooseSlot(actor, AnimationSlot.Facial, timeline);
        if (!chosen.Success)
            return chosen;

        // A replacement must run with Facial unpinned before it is held again.
        var current = _store.For(actor);
        float? speedCapture = null;
        if (!current.SlotSpeedCaptures.ContainsKey(AnimationSlot.Facial))
        {
            var reading = _timeline.Read(actor);
            if (reading == null)
                return Outcome.Fail("The facial speed restore point is unavailable.");
            speedCapture = reading.SpeedFor(AnimationSlot.Facial);
        }
        if (current.SlotSpeeds.ContainsKey(AnimationSlot.Facial))
        {
            float restore = current.SlotSpeedCaptures.TryGetValue(
                AnimationSlot.Facial, out var captured) ? captured : 1f;
            var unpinned = _speedPort.ClearSlotSpeed(
                actor, AnimationSlot.Facial, restore);
            if (!unpinned.Success)
                return unpinned;
            _store.Mutate(actor, o =>
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

        var played = _base.ApplySelectedSlotCore(actor, AnimationSlot.Facial);
        if (!played.Success)
            return played;

        var held = _speed.SetSlotSpeedCore(
            actor, AnimationSlot.Facial, 0f, speedCapture);
        if (!held.Success)
        {
            // Facial is selected here, so a Facial slot reset is exactly
            // the expression release.
            var rollback = ReleaseExpressionCore(actor);
            return rollback.Success
                ? held
                : Outcome.Fail(
                    $"{held.Detail ?? "Expression hold failed."} " +
                    $"Restore failed: {rollback.Detail}");
        }
        _store.Mutate(actor, o => o with { HeldExpression = timeline });
        return Outcome.Ok();
    }

    /// <summary>Releases a held facial expression.</summary>
    public Outcome ReleaseExpression(ActorId actor)
    {
        if (_store.Suspended() is { } blocked) return blocked;
        return ReleaseExpressionCore(actor);
    }

    public Outcome ReleaseExpressionCore(ActorId actor)
    {
        var current = _store.For(actor);
        ushort? active = current.HeldExpression;
        if (active == null &&
            current.SelectedSlots.TryGetValue(AnimationSlot.Facial, out var selected))
            active = selected;
        if (active is not { } held)
            return ResetSlotWithoutExpressionBridge(actor, AnimationSlot.Facial);
        if (!current.SlotCaptures.ContainsKey(AnimationSlot.Facial))
            return _base.ResetBlendSelection(actor, AnimationSlot.Facial);

        float restoreSpeed = current.SlotSpeedCaptures.TryGetValue(
            AnimationSlot.Facial, out var capturedSpeed) ? capturedSpeed : 1f;
        var unpinned = _speedPort.ClearSlotSpeed(
            actor, AnimationSlot.Facial, restoreSpeed);
        if (!unpinned.Success)
            return unpinned;

        // Release clears Facial speed, plays Straight Face, clears speed
        // again, then restores the captured facial slot.
        var straight = _base.BlendCore(
            actor, AnimationTimelines.StraightFace, AnimationSlot.Facial);
        var again = straight.Success
            ? _speedPort.ClearSlotSpeed(actor, AnimationSlot.Facial, restoreSpeed)
            : straight;
        var restored = straight.Success && again.Success
            ? _base.ResetBlendSelection(actor, AnimationSlot.Facial)
            : Outcome.Fail(
                straight.Detail ?? again.Detail ?? "Expression release failed.");
        if (!restored.Success)
        {
            // Session ownership has not been cleared. Put the held expression
            // back when possible so Reset remains a truthful retry.
            var replayed = _base.BlendCore(actor, held, AnimationSlot.Facial);
            var repinned = replayed.Success
                ? _speedPort.SetSlotSpeed(actor, AnimationSlot.Facial, 0f)
                : replayed;
            return Outcome.Fail(
                (restored.Detail ?? "Expression release failed.") +
                (replayed.Success && repinned.Success
                    ? string.Empty
                    : $" Hold rollback failed: " +
                      (replayed.Detail ?? repinned.Detail ?? "facial hold failed.")));
        }

        _store.Mutate(actor, o =>
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
        return Outcome.Ok();
    }

    private Outcome ResetSlotWithoutExpressionBridge(
        ActorId actor, AnimationSlot slot)
    {
        if (_store.For(actor).SlotSpeedCaptures.ContainsKey(slot))
        {
            var speed = _speed.ClearSlotSpeedCore(actor, slot);
            if (!speed.Success)
                return speed;
        }
        return _base.ResetBlendSelection(actor, slot);
    }

    /// <summary>Restores the captured facial layer.</summary>
    public Outcome RestoreFacialLayer(ActorId actor)
        => ReleaseExpression(actor);

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) =>
        _store.For(actor).HeldExpression;
}
