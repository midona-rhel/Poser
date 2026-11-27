using Poser.Application.Animation;
using Poser.Application.Integration;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Appearance;
using Poser.Game.Entities;
using Poser.Game.Services;
using Poser.Game.Presentation;

namespace Poser.Game.Posing;

/// <summary>Reset → All for one actor: its expression, gaze, pose, IK, animation and
/// looks go back to the game's, in that order, as one undo step.</summary>
public sealed class ActorResetControl(
    SceneSession scene, TransformCommands transforms, PoseEditService poses,
    IEntityBindings bindings, ISkeletonService skeletons, IBonePosingService posing,
    IExpressionService expressions, IPoseImportCommands imports,
    GazeSession gaze, IAnimationPlayback animation,
    ActorPresentationSession presentation, ActorModelIdSession models, IntegrationReset integration,
    EditHistory history, ValueJournal values, IActorStateSnapshots snapshots)
    : IActorResetControl
{
    public PoseEditResult ResetAll(ActorId actor)
    {
        values.Seal();
        if (transforms.Refusal() is { } refused)
            return PoseEditResult.Fail(refused.Detail!);
        var admission = CanReset(actor);
        if (!admission.Success) return admission;
        var capture = snapshots.Capture(actor);
        if (!capture.Success || capture.Value is not { } before)
            return PoseEditResult.Fail(capture.Detail ?? "The actor's state could not be captured before resetting.");

        // Reset all is one undo step. The snapshot above does not hold the
        // animation, and the animation reset below adds no undo step of its own,
        // so the animation is saved here separately.
        var animationBefore = animation.Capture(actor);
        var result = ResetCore(actor);
        history.Append(new JournalStep("Reset all",
            () => CanReset(actor).Success,
            () => ResetCore(actor).Success)
        {
            AffectedEntities = new[] { SelectionId.ForActor(actor) },
            OnRefusal = () => RefusalAction.Keep,
            CompleteReplay = (undo, current, cancellation, completed) =>
            {
                if (undo)
                    snapshots.Restore(before, current, cancellation, restored => completed(
                        restored.Success ? GestureResult.From(animation.Restore(actor, animationBefore)) : restored));
                else snapshots.WaitForReset(actor, current, cancellation, completed);
            },
        });
        return result;
    }

    /// <summary>A reset is refused when the actor is gone, and while a pose
    /// import is still being applied.</summary>
    private PoseEditResult CanReset(ActorId actor) =>
        !scene.Contains(TransformTargetId.ForActor(actor)) ? Gone()
            : imports.IsImportBusy ? PoseEditResult.Fail("A pose import is applying.")
            : bindings.Resolve(actor).Success ? PoseEditResult.Ok(0)
            : Gone();

    private static PoseEditResult Gone() => PoseEditResult.Fail("This actor is no longer available.");

    private PoseEditResult ResetCore(ActorId actor)
    {
        var admission = CanReset(actor);
        if (!admission.Success) return admission;
        var failures = new List<string>();
        void Run(string name, Func<(bool Success, string? Detail)> reset)
        {
            try
            {
                var result = reset();
                if (!result.Success) failures.Add($"{name}: {result.Detail ?? "reset failed"}");
            }
            catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
        }
        Run("Expression", () => { var r = WithActor(actor, expressions.ResetExpression); return (r.Success, r.Detail); });
        Run("Gaze", () => { var r = gaze.ResetWithoutUndoStep(actor); return (r.Success, r.Detail); });
        Run("Pose", () =>
        {
            var targets = scene.Snapshot.Actors.Where(a => a.Id == actor)
                .SelectMany(a => a.Skeletons).SelectMany(s => s.Bones)
                .Select(b => TransformTargetId.ForBone(b.Id)).ToArray();
            var r = poses.ResetBones(targets);
            return (r.Success, r.Detail);
        });
        Run("IK", () =>
        {
            var r = WithActor(actor, live =>
            {
                foreach (var skeleton in skeletons.GetSkeletons(live))
                    posing.ClearIkConfigurations(skeleton);
            });
            return (r.Success, r.Detail);
        });
        // Animation and appearance are reset after the pose. Changes from
        // Penumbra, Glamourer and Customize+ go last, because putting back the
        // Penumbra collection can redraw the actor.
        Run("Animation", () =>
        {
            using var noStep = animation.Untracked();
            var r = animation.ResetActor(actor);
            return (r.Success, r.Detail);
        });
        Run("Appearance", () => { var r = presentation.ResetActor(actor); return (r.Success, r.Detail); });
        Run("Model", () => { var r = models.Reset(actor); return (r.Success, r.Detail); });
        Run("External appearance", () => { var r = integration.ResetActor(actor); return (r.Success, r.Detail); });
        return failures.Count == 0 ? PoseEditResult.Ok(1)
            : PoseEditResult.Fail(string.Join(" | ", failures));
    }

    private PoseEditResult WithActor(ActorId id, Action<IActor> action)
    {
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor })
            return Gone();
        action(actor);
        return PoseEditResult.Ok(1);
    }
}
