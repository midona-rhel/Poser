using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// The animation choices that are journal steps: playing a timeline into a
/// slot, resetting a slot, and the loop switch. Transport — pause, resume,
/// scrub, speed — never journals. The undo of a play plays the timeline
/// the slot held before, or resets the slot when it held none.
/// </summary>
public sealed class AnimationSteps : IAnimationActions
{
    private readonly AnimationSession _animation;
    private readonly ValueJournal _journal;
    private readonly SceneSession _scene;
    private readonly IExpressionPreview _expressions;

    public AnimationSteps(AnimationSession animation, ValueJournal journal, SceneSession scene,
        IExpressionPreview expressions)
    {
        _animation = animation;
        _journal = journal;
        _scene = scene;
        _expressions = expressions;
    }

    public Outcome SetAdvanced(ActorId actor, bool enabled)
    {
        if (!Alive(actor)) return Outcome.Fail("The actor is no longer available.");
        if (_animation.IsAdvanced(actor) == enabled) return Outcome.Ok();
        if (!enabled)
        {
            var expression = _expressions.Reset(actor);
            if (!expression.Success) return expression;
            var reset = ResetLayers(actor);
            if (!reset.Success) return reset;
        }
        // Entering only exposes the existing layers. Leaving keeps the prior
        // non-atomic restore policy, and publishes Basic only after success.
        _animation.SetAdvanced(actor, enabled);
        return Outcome.Ok();
    }

    private bool Alive(ActorId actor) => _scene.Snapshot.FindActor(actor) is not null;

    private ushort? Applied(ActorId actor, AnimationSlot slot) =>
        _animation.OverridesFor(actor).AppliedSlots.TryGetValue(slot, out var timeline) ? timeline : null;

    public Outcome Play(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry, bool playFromStart, bool resume = true)
    {
        var before = Applied(actor, slot);
        var result = _animation.PlaySelectedSlot(actor, slot, entry, playFromStart, resume);
        if (!result.Success)
            return result;
        var after = Applied(actor, slot);
        _journal.Record(SelectionId.ForActor(actor), $"Play {AnimationSlots.DisplayName(slot)}", before, after,
            ValueWrites.Unchecked<ushort?>(next => Put(actor, slot, next, playFromStart)), () => Alive(actor));
        return result;
    }

    public Outcome ResetSlot(ActorId actor, AnimationSlot slot)
    {
        var before = Applied(actor, slot);
        var result = _animation.ResetSlot(actor, slot);
        if (!result.Success)
            return result;
        _journal.Record(SelectionId.ForActor(actor), $"Reset {AnimationSlots.DisplayName(slot)}", before, (ushort?)null,
            ValueWrites.Unchecked<ushort?>(next => Put(actor, slot, next, false)), () => Alive(actor));
        return result;
    }

    public Outcome SetLoop(ActorId actor, AnimationSlot slot, bool on)
    {
        return _journal.Set((actor, slot, "Loop"), on ? "Loop on" : "Loop off",
            () => _animation.LoopWantedFor(actor, slot),
            x => _animation.SetSlotLoop(actor, slot, 0, x),
            on, () => Alive(actor));
    }

    public Outcome ResetGeneral(ActorId actor)
    {
        var reset = ResetSlot(actor, AnimationSlot.Base);
        return reset.Success && _animation.LoopWantedFor(actor, AnimationSlot.Base)
            ? SetLoop(actor, AnimationSlot.Base, false)
            : reset;
    }

    /// <summary>Restores outgoing advanced layers in their existing order.
    /// A failed restore leaves earlier successes intact and keeps the mode unchanged.</summary>
    private Outcome ResetLayers(ActorId actor)
    {
        foreach (var slot in new[] { AnimationSlot.Base, AnimationSlot.UpperBody,
                     AnimationSlot.Facial, AnimationSlot.Additive, AnimationSlot.Lips })
        {
            var reset = ResetSlot(actor, slot);
            if (!reset.Success)
                return reset;
        }
        return Outcome.Ok();
    }

    private void Put(ActorId actor, AnimationSlot slot, ushort? timeline, bool playFromStart)
    {
        if (timeline is not { } chosen)
        {
            _animation.ResetSlot(actor, slot);
            return;
        }
        if (_animation.ChooseSlot(actor, slot, chosen).Success)
            _animation.PlaySelectedSlot(actor, slot, null, playFromStart);
    }
}
