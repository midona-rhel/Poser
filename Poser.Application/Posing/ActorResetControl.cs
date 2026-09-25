using Poser.Application.Animation;
using Poser.Application.Gaze;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>Whole-actor reset ordering and history; native calls resolve IDs per operation.</summary>
public sealed class ActorResetControl(
    SceneSession scene, TransformGestureService gestures, PoseEditService poses,
    IActorPoseResetRuntime runtime, IGazeRuntimePort gaze, AnimationSession animation,
    ActorPresentationSession presentation, ActorIntegrationSession integration,
    TransformHistory history, ValueJournal values, Lazy<IPoseSnapshotPort> snapshots)
    : IActorResetControl
{
    public PoseEditResult ResetAll(ActorId actor)
    {
        values.Seal();
        if (gestures.PendingRecovery != null || gestures.ActiveGesture != null)
            return PoseEditResult.Fail("Finish the active transform or recovery before resetting the actor.");
        using var transition = gestures.TryEnterTransition();
        if (transition == null)
            return PoseEditResult.Fail("Another pose operation is running.");
        var admission = CanReset(actor);
        if (!admission.Success) return admission;
        var before = snapshots.Value.Capture(actor.LogicalId);
        if (before == null)
            return PoseEditResult.Fail("The actor's pose could not be captured before resetting.");

        var result = ResetCore(actor);
        // Preserve the existing pose/IK inverse. This is not an animation or
        // external-appearance snapshot, and must not pretend to be one.
        history.Append(new JournalStep("Reset all",
            () => CanReset(actor).Success,
            () => ResetCore(actor).Success)
        {
            RestoreSnapshotsAfterReplay = true,
            RetainOnFailure = true,
            Context = new StepContext([], [before], [], null),
        });
        return result;
    }

    private PoseEditResult CanReset(ActorId actor) =>
        scene.Contains(TransformTargetId.ForActor(actor))
            ? runtime.CanReset(actor)
            : PoseEditResult.Fail("This actor is no longer available.");

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
        Run("Expression", () => { var r = runtime.ResetExpression(actor); return (r.Success, r.Detail); });
        Run("Gaze", () => { var r = gaze.Reset(actor); return (r.Success, r.Detail); });
        Run("Pose", () =>
        {
            var targets = scene.Snapshot.Actors.Where(a => a.Id == actor)
                .SelectMany(a => a.Skeletons).SelectMany(s => s.Bones)
                .Select(b => TransformTargetId.ForBone(b.Id)).ToArray();
            var r = poses.ResetWithinTransition(targets, PoseRegion.All, "Reset pose");
            return (r.Success, r.Detail);
        });
        Run("IK", () => { var r = runtime.ClearIk(actor); return (r.Success, r.Detail); });
        // Release animation/presentation after pose writes. External integration
        // is last because restoring its collection may redraw the native model.
        Run("Animation", () => { var r = animation.ResetActor(actor); return (r.Success, r.Detail); });
        Run("Appearance", () => { var r = presentation.ResetActor(actor); return (r.Success, r.Detail); });
        Run("External appearance", () => { var r = integration.ResetActor(actor); return (r.Success, r.Detail); });
        return failures.Count == 0 ? PoseEditResult.Ok(1)
            : PoseEditResult.Fail(string.Join(" | ", failures));
    }
}
