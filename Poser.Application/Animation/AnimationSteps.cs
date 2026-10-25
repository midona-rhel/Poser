using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Connects animation actions to undo history using before/after snapshots.
/// Changes remain undoable when an action completes only some of its steps.
/// Grouped actions share one history step; sliders are grouped by their caller.
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
        _animation.Journal = Record;
        _animation.Group = Group;
    }

    private Outcome Record(ActorId actor, string description, Func<Outcome> action)
    {
        if (!Alive(actor))
            return action();
        var before = _animation.Capture(actor);
        var result = action();
        var after = _animation.Capture(actor);
        _journal.Record((actor, "Animation", description), description, before, after,
            snapshot => _animation.Restore(actor, snapshot), () => Alive(actor));
        return result;
    }

    // One step for several actors (each keeps its own before/after).
    private void Group(Action action)
    {
        var token = new object();
        _journal.BeginEdit(token);
        try { action(); }
        finally
        {
            _journal.EndEdit();
            _journal.CommitEdit(token);
        }
    }

    public Outcome SetAdvanced(ActorId actor, bool enabled)
    {
        if (!Alive(actor)) return Outcome.Fail("The actor is no longer available.");
        return _animation.Tracked(actor, enabled ? "Advanced animation on" : "Advanced animation off",
            () => SetAdvancedCore(actor, enabled));
    }

    private Outcome SetAdvancedCore(ActorId actor, bool enabled)
    {
        if (_animation.IsAdvanced(actor) == enabled) return Outcome.Ok();
        if (!enabled)
        {
            var expression = _expressions.Reset(actor);
            if (!expression.Success) return expression;
            var reset = ResetLayers(actor);
            if (!reset.Success) return reset;
        }
        // Entering only exposes the existing layers. Leaving keeps the prior
        // layer changes if a later reset fails; Basic is selected only after success.
        _animation.SetAdvanced(actor, enabled);
        return Outcome.Ok();
    }

    private bool Alive(ActorId actor) => _scene.Snapshot.FindActor(actor) is not null;

    public Outcome Play(
        ActorId actor, AnimationSlot slot, TimelineEntry? entry, bool playFromStart, bool resume = true) =>
        _animation.PlaySelectedSlot(actor, slot, entry, playFromStart, resume);

    public Outcome ResetSlot(ActorId actor, AnimationSlot slot) => _animation.ResetSlot(actor, slot);

    public Outcome SetLoop(ActorId actor, AnimationSlot slot, bool on) =>
        _animation.SetSlotLoop(actor, slot, 0, on);

    public Outcome ResetGeneral(ActorId actor, AnimationSlot slot = AnimationSlot.Base) =>
        _animation.Tracked(actor, "Reset animation", () =>
        {
            var reset = ResetSlot(actor, slot);
            return reset.Success && _animation.LoopWantedFor(actor, AnimationSlot.Base)
                ? SetLoop(actor, AnimationSlot.Base, false)
                : reset;
        });

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
}
