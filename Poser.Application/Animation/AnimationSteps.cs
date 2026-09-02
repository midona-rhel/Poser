using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// The animation choices that are journal steps: playing a timeline into a
/// slot, resetting a slot, and the loop switch. Transport — pause, resume,
/// scrub, speed — never journals. The undo of a play plays the timeline
/// the slot held before, or resets the slot when it held none.
/// </summary>
public sealed class AnimationSteps
{
    private readonly AnimationSession _animation;
    private readonly ValueJournal _journal;
    private readonly SceneSession _scene;

    public AnimationSteps(AnimationSession animation, ValueJournal journal, SceneSession scene)
    {
        _animation = animation;
        _journal = journal;
        _scene = scene;
    }

    private bool Alive(ActorId actor) => _scene.Snapshot.FindActor(actor) is not null;

    private ushort? Applied(ActorId actor, AnimationSlot slot) =>
        _animation.OverridesFor(actor).AppliedSlots.TryGetValue(slot, out var timeline) ? timeline : null;

    public AnimationResult Play(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry, bool playFromStart, bool resume = true)
    {
        var before = Applied(actor, slot);
        var result = _animation.PlaySelectedSlot(actor, slot, entry, playFromStart, resume);
        if (!result.Success)
            return result;
        var after = Applied(actor, slot);
        _journal.Record($"Play {AnimationSlots.DisplayName(slot)}", before, after,
            next => Put(actor, slot, next, playFromStart), () => Alive(actor));
        return result;
    }

    public AnimationResult ResetSlot(ActorId actor, AnimationSlot slot)
    {
        var before = Applied(actor, slot);
        var result = _animation.ResetSlot(actor, slot);
        if (!result.Success)
            return result;
        _journal.Record($"Reset {AnimationSlots.DisplayName(slot)}", before, (ushort?)null,
            next => Put(actor, slot, next, false), () => Alive(actor));
        return result;
    }

    public AnimationResult SetLoop(ActorId actor, AnimationSlot slot, bool on)
    {
        AnimationResult result = AnimationResult.Ok();
        _journal.Set((actor, slot, "Loop"), on ? "Loop on" : "Loop off",
            () => _animation.LoopWantedFor(actor, slot),
            x => result = _animation.SetSlotLoop(actor, slot, 0, x),
            on, () => Alive(actor));
        return result;
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
