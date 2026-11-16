using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Collections;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>Applies and pauses a facial expression, then restores the previous
/// facial animation and speed when the expression is released.</summary>
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
        // A held expression stands still, so the Facial layer's Loop (Animation tab) ends.
        if (_store.For(actor).LoopWantedSlots.Contains(AnimationSlot.Facial))
            _base.SetSlotLoop(actor, AnimationSlot.Facial, 0, false);
        var chosen = _base.ChooseSlot(actor, AnimationSlot.Facial, timeline);
        if (!chosen.Success)
            return chosen;

        // When the face is already held, let it go first: the new expression
        // has to start playing before it is held on its first frame.
        var current = _store.For(actor);
        float? speedCapture = current.SlotSpeedCaptures.ContainsKey(AnimationSlot.Facial)
            ? null
            : _timeline.Read(actor)?.SpeedFor(AnimationSlot.Facial);
        if (current.SlotSpeeds.ContainsKey(AnimationSlot.Facial))
        {
            float restore = current.SlotSpeedCaptures.TryGetValue(
                AnimationSlot.Facial, out var captured) ? captured : 1f;
            var unpinned = _speedPort.ClearSlotSpeed(
                actor, AnimationSlot.Facial, restore);
            if (!unpinned.Success)
                return unpinned;
            _store.Mutate(actor, o => o with
            {
                SlotSpeeds = o.SlotSpeeds.Without(AnimationSlot.Facial),
                SlotResumeSpeeds = o.SlotResumeSpeeds.Without(AnimationSlot.Facial),
                HeldExpression = null,
            });
        }

        var played = _base.ApplySelectedSlotCore(actor, AnimationSlot.Facial);
        if (!played.Success)
            return played;

        var held = _speed.SetSlotSpeed(
            actor, AnimationSlot.Facial, 0f, speedCapture);
        if (!held.Success)
            return held;
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
        // Nothing is chosen for the face. Only a Facial speed Poser set can
        // be left to undo.
        if (active == null)
            return current.SlotSpeedCaptures.ContainsKey(AnimationSlot.Facial)
                ? _speed.ClearSlotSpeed(actor, AnimationSlot.Facial)
                : Outcome.Ok();
        // Chosen but never played: only the choice is forgotten.
        if (!current.SlotCaptures.ContainsKey(AnimationSlot.Facial))
            return _base.ResetBlendSelection(actor, AnimationSlot.Facial);

        float restoreSpeed = current.SlotSpeedCaptures.TryGetValue(
            AnimationSlot.Facial, out var capturedSpeed) ? capturedSpeed : 1f;
        var unpinned = _speedPort.ClearSlotSpeed(
            actor, AnimationSlot.Facial, restoreSpeed);
        if (!unpinned.Success)
            return unpinned;

        // With the face free to move, play Straight Face, then put back the
        // Facial animation that played before Poser's first change.
        var straight = _base.BlendCore(
            actor, AnimationTimelines.StraightFace, AnimationSlot.Facial);
        var restored = straight.Success
            ? _base.ResetBlendSelection(actor, AnimationSlot.Facial)
            : straight;
        // The face is no longer held, whether or not the game put the earlier
        // face back. After a failure Reset can be pressed again.
        _store.Mutate(actor, o => o.WithoutSlotSpeed(AnimationSlot.Facial) with { HeldExpression = null });
        return restored;
    }

    /// <summary>The expression currently held on the face, if any.</summary>
    public ushort? HeldExpressionFor(ActorId actor) =>
        _store.For(actor).HeldExpression;
}
