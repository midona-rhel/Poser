using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>
/// Changes transforms from a typed value or a command, outside a drag. Each call
/// is one undo step. If a write fails part-way, everything already written is put
/// back. If that fails too, the result says so and later edits wait until it works.
/// </summary>
public sealed class TransformCommandService
{
    private const string Cancelled = "Transform application was cancelled.";

    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private readonly EditHistory _history;
    private readonly TransformGestureService _gestures;

    public TransformCommandService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        EditHistory history,
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
        if (Enter(out var transition) is { } refused)
            return refused;
        using (transition)
            return Change([target], (before, _) => _runtime.ApplyAbsolute(before, desired),
                description, "Could not transform", "transform");
    }

    /// <summary>
    /// Sets several transforms as one undo step, parents before children. Used
    /// where one action moves many bones, such as baking an expression into the
    /// pose. Targets that are not listed are left alone.
    /// <paramref name="cancellationRequested"/> is asked between writes and once
    /// more before the step is recorded. A cancel puts everything back and
    /// records nothing.
    /// </summary>
    public GestureResult SetAbsoluteMany(
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        string description,
        bool rawBaseline = false,
        Func<bool>? cancellationRequested = null)
    {
        if (Enter(out var transition) is { } refused)
            return refused;
        using (transition)
        {
            if (writes.Count == 0)
                return GestureResult.Fail("Nothing to apply.");
            return Change(
                writes.Select(write => write.Target).ToArray(),
                (before, index) => _runtime.ApplyAbsolute(before, writes[index].Desired, rawBaseline),
                description, "Could not transform", "transform",
                parentsFirst: true, cancellationRequested);
        }
    }

    /// <summary>Lets the game place these actors again, as one undo step.</summary>
    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets,
        string description)
    {
        if (Enter(out var transition) is { } refused)
            return refused;
        using (transition)
        {
            if (targets.Count == 0)
                return GestureResult.Fail("No actors were supplied.");
            if (targets.Any(target => target.Kind != TransformTargetKind.Actor))
                return GestureResult.Fail("Only actor transforms can clear model overrides.");
            return Change(
                targets.Distinct().ToArray(),
                (before, _) => _runtime.Restore(before with { HasOverride = false }),
                description, "Could not clear actor override", "reset");
        }
    }

    /// <summary>
    /// Refuses while an earlier failed edit is still being put back, while another
    /// edit is being applied, or during a drag. Otherwise the caller holds
    /// <paramref name="transition"/> until its own edit is done.
    /// </summary>
    private GestureResult? Enter(out IDisposable? transition)
    {
        transition = null;
        if (_gestures.RecoveryBarrier() is { } waiting)
            return waiting;
        transition = _gestures.TryEnterTransition();
        if (transition == null)
            return _gestures.Busy();
        if (_gestures.ActiveGesture == null)
            return null;
        transition.Dispose();
        transition = null;
        return GestureResult.Fail("A transform gesture is active.");
    }

    /// <summary>
    /// Reads every target, writes each one, reads them all again and records the
    /// undo step. A failed read before the first write changes nothing. Any
    /// failure after that puts every target back.
    /// </summary>
    private GestureResult Change(
        IReadOnlyList<TransformTargetId> targets,
        Func<TransformTargetState, int, TransformPortResult> write,
        string description,
        string writeFailure,
        string afterWhat,
        bool parentsFirst = false,
        Func<bool>? cancellationRequested = null)
    {
        bool IsCancelled() => cancellationRequested?.Invoke() == true;

        var before = new List<TransformTargetState>(targets.Count);
        foreach (var target in targets)
        {
            var captured = Try(() => Capture(target), $"Could not capture {target}");
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
            if (IsCancelled())
                return PutBack(Cancelled, before);
        }

        var order = Enumerable.Range(0, before.Count);
        if (parentsFirst)
            order = order.OrderBy(index => _runtime.DependencyDepth(before[index].Target));
        foreach (int index in order)
        {
            if (IsCancelled())
                return PutBack(Cancelled, before);
            var target = before[index].Target;
            var written = Try(() => write(before[index], index), $"{writeFailure} {target}");
            if (IsCancelled())
                return PutBack(Cancelled, before);
            if (!written.Success)
                return PutBack(written.Detail ?? $"{writeFailure} {target}.", before);
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            string failure = $"Could not capture {state.Target} after {afterWhat}";
            var captured = Try(() => _runtime.Capture(state.Target), failure);
            if (!captured.Success || captured.State == null)
                return PutBack(captured.Detail ?? $"{failure}.", before);
            after.Add(captured.State);
            if (IsCancelled())
                return PutBack(Cancelled, before);
        }

        if (IsCancelled())
            return PutBack(Cancelled, before);
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

    /// <summary>Runs one call into the game. An exception becomes a failed result.</summary>
    private static TransformPortResult Try(Func<TransformPortResult> call, string failure)
    {
        try
        {
            return call();
        }
        catch (Exception exception)
        {
            return TransformPortResult.Fail(
                TransformPortStatus.NativeUnavailable, $"{failure}: {exception.Message}");
        }
    }

    /// <summary>Restores the values read before the edit and reports the failure.</summary>
    private GestureResult PutBack(string failure, IReadOnlyList<TransformTargetState> before)
    {
        var recovery = _gestures.AttemptRecovery(before);
        return GestureResult.Fail(TransformRecovery.AppendRollbackFailure(failure, recovery)) with
        {
            Recovery = recovery,
        };
    }
}
