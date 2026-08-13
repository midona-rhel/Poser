using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>
/// Non-interactive transform commands with exhaustive rollback evidence.
/// </summary>
public sealed class TransformCommandService
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;

    public TransformCommandService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        TransformHistory history,
        TransformGestureService gestures)
    {
        _scene = scene;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
    }

    public GestureResult SetAbsolute(
        TransformTargetId target,
        PoseTransform desired,
        string description)
    {
        if (_gestures.RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return _gestures.Busy();
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail(
                "A transform gesture is active.");
        TransformPortResult captured;
        try
        {
            captured = Capture(target);
        }
        catch (Exception exception)
        {
            return ThrownFailureAfterRecovery(
                $"Could not capture {target}",
                exception,
                Array.Empty<TransformTargetState>());
        }
        if (!captured.Success || captured.State == null)
            return GestureResult.Fail(captured.Detail!);
        var before = captured.State;

        TransformPortResult applied;
        try
        {
            applied = _runtime.ApplyAbsolute(before, desired);
        }
        catch (Exception exception)
        {
            return ThrownFailureAfterRecovery(
                $"Could not transform {target}", exception, new[] { before });
        }
        if (!applied.Success)
        {
            var rollback = _gestures.AttemptRecovery(new[] { before });
            return FailureAfterRecovery(
                applied.Detail ?? $"Could not transform {target}.",
                rollback);
        }

        TransformPortResult after;
        try
        {
            after = _runtime.Capture(target);
        }
        catch (Exception exception)
        {
            return ThrownFailureAfterRecovery(
                $"Could not capture {target} after transform",
                exception,
                new[] { before });
        }
        if (!after.Success || after.State == null)
        {
            var rollback = _gestures.AttemptRecovery(new[] { before });
            return FailureAfterRecovery(
                after.Detail ?? $"Could not capture {target}.",
                rollback);
        }

        _history.Append(new TransformPatch(
            description,
            new[] { before },
            new[] { after.State }));
        return GestureResult.Ok();
    }

    /// <summary>
    /// Writes several absolute transforms as ONE history entry. Used
    /// where a single user act moves many bones at once — baking a facial
    /// animation into the pose, for example — so undo puts all of them
    /// back together instead of unwinding bone by bone.
    ///
    /// Every target is captured before anything is written, and any
    /// failure attempts every captured baseline in order. Incomplete rollback
    /// is returned as typed recovery evidence and blocks later mutations.
    /// Targets not supplied are untouched, which is what leaves expression,
    /// gaze, and unrelated manual edits intact.
    /// A supplied cancellation probe is sampled between native calls and
    /// immediately before history commit; cancellation restores every frozen
    /// baseline and never starts a later write or appends a patch.
    /// </summary>
    public GestureResult SetAbsoluteMany(
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        string description,
        bool rawBaseline = false,
        Func<bool>? cancellationRequested = null)
    {
        if (_gestures.RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return _gestures.Busy();
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("A transform gesture is active.");
        if (writes.Count == 0)
            return GestureResult.Fail("Nothing to apply.");

        var before = new List<TransformTargetState>(writes.Count);
        foreach (var (target, _) in writes)
        {
            TransformPortResult captured;
            try
            {
                captured = Capture(target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture {target}", exception, before);
            }
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
            if (cancellationRequested?.Invoke() == true)
                return CancelledAfterRecovery(before);
        }

        for (int i = 0; i < writes.Count; i++)
        {
            if (cancellationRequested?.Invoke() == true)
                return CancelledAfterRecovery(before);
            TransformPortResult applied;
            try
            {
                applied = _runtime.ApplyAbsolute(
                    before[i], writes[i].Desired, rawBaseline);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not transform {writes[i].Target}",
                    exception,
                    before);
            }
            if (cancellationRequested?.Invoke() == true)
                return CancelledAfterRecovery(before);
            if (applied.Success)
                continue;
            var rollback = _gestures.AttemptRecovery(before);
            return FailureAfterRecovery(
                applied.Detail ?? $"Could not transform {writes[i].Target}.",
                rollback);
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            TransformPortResult captured;
            try
            {
                captured = _runtime.Capture(state.Target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture {state.Target} after transform",
                    exception,
                    before);
            }
            if (!captured.Success || captured.State == null)
            {
                var rollback = _gestures.AttemptRecovery(before);
                return FailureAfterRecovery(
                    captured.Detail ?? $"Could not capture {state.Target}.",
                    rollback);
            }
            after.Add(captured.State);
            if (cancellationRequested?.Invoke() == true)
                return CancelledAfterRecovery(before);
        }

        if (cancellationRequested?.Invoke() == true)
            return CancelledAfterRecovery(before);
        _history.Append(new TransformPatch(description, before, after));
        return GestureResult.Ok();
    }

    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets,
        string description)
    {
        if (_gestures.RecoveryBarrier() is { } recoveryBarrier)
            return recoveryBarrier;
        using var transition = _gestures.TryEnterTransition();
        if (transition == null)
            return _gestures.Busy();
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail(
                "A transform gesture is active.");
        if (targets.Count == 0)
            return GestureResult.Fail("No actors were supplied.");
        if (targets.Any(target =>
                target.Kind != TransformTargetKind.Actor))
            return GestureResult.Fail(
                "Only actor transforms can clear model overrides.");

        var before = new List<TransformTargetState>(targets.Count);
        foreach (var target in targets.Distinct())
        {
            TransformPortResult captured;
            try
            {
                captured = Capture(target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture {target}", exception, before);
            }
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
        }

        foreach (var state in before)
        {
            var cleared = state with { HasOverride = false };
            TransformPortResult result;
            try
            {
                result = _runtime.Restore(cleared);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not clear actor override {state.Target}",
                    exception,
                    before);
            }
            if (result.Success)
                continue;
            var rollback = _gestures.AttemptRecovery(before);
            return FailureAfterRecovery(
                result.Detail ??
                $"Could not clear actor override {state.Target}.",
                rollback);
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            TransformPortResult captured;
            try
            {
                captured = _runtime.Capture(state.Target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture {state.Target} after reset",
                    exception,
                    before);
            }
            if (!captured.Success || captured.State == null)
            {
                var rollback = _gestures.AttemptRecovery(before);
                return FailureAfterRecovery(
                    captured.Detail ??
                    $"Could not capture {state.Target} after reset.",
                    rollback);
            }
            after.Add(captured.State);
        }

        _history.Append(new TransformPatch(description, before, after));
        return GestureResult.Ok();
    }

    private TransformPortResult Capture(TransformTargetId target)
    {
        if (!_scene.Contains(target))
            return TransformPortResult.Fail(
                TransformPortStatus.StaleTarget,
                $"Target {target} is stale.");
        return _runtime.Capture(target);
    }

    private static GestureResult FailureAfterRecovery(
        string primaryFailure,
        TransformRecoveryReceipt recovery) =>
        GestureResult.Fail(TransformRecovery.AppendRollbackFailure(
            primaryFailure,
            recovery)) with
        {
            Recovery = recovery,
        };

    private GestureResult ThrownFailureAfterRecovery(
        string operation,
        Exception exception,
        IReadOnlyList<TransformTargetState> baselines)
    {
        var recovery = _gestures.AttemptRecovery(baselines);
        return FailureAfterRecovery(
            $"{operation}: {exception.Message}",
            recovery);
    }

    private GestureResult CancelledAfterRecovery(
        IReadOnlyList<TransformTargetState> baselines)
    {
        var recovery = _gestures.AttemptRecovery(baselines);
        return FailureAfterRecovery(
            "Transform application was cancelled.",
            recovery);
    }
}
