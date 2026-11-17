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
        string description) =>
        Refusal() ?? Change([target], (before, _) => _runtime.ApplyAbsolute(before, desired),
            description, "Could not transform", "transform");

    /// <summary>
    /// Sets several transforms as one undo step, parents before children. Used
    /// where one action moves many bones, such as baking an expression into the
    /// pose. Targets that are not listed are left alone.
    /// </summary>
    public GestureResult SetAbsoluteMany(
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        string description,
        bool rawBaseline = false) =>
        Refusal() ?? Change(
            writes.Select(write => write.Target).ToArray(),
            (before, index) => _runtime.ApplyAbsolute(before, writes[index].Desired, rawBaseline),
            description, "Could not transform", "transform",
            parentsFirst: true);

    /// <summary>Lets the game place these actors again, as one undo step.</summary>
    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets,
        string description) =>
        Refusal() ?? Change(
            targets,
            (before, _) => _runtime.Restore(before with { HasOverride = false }),
            description, "Could not clear actor override", "reset");

    /// <summary>
    /// Refuses while an earlier failed edit is still being put back, or during
    /// a drag. A drag can be live here: an expression bake writes a few frames
    /// after it starts, and a gizmo can be grabbed in between.
    /// </summary>
    private GestureResult? Refusal() =>
        _gestures.RecoveryBarrier() ??
        (_gestures.ActiveGesture != null ? GestureResult.Fail("A transform gesture is active.") : null);

    /// <summary>
    /// Reads every target, writes each one, reads them all again and records the
    /// undo step. A failed read or write changes nothing by itself. When a
    /// write fails after earlier ones landed, those earlier targets are put
    /// back: resetting several actors can stop at one whose parent is gone.
    /// </summary>
    private GestureResult Change(
        IReadOnlyList<TransformTargetId> targets,
        Func<TransformTargetState, int, TransformPortResult> write,
        string description,
        string writeFailure,
        string afterWhat,
        bool parentsFirst = false)
    {
        var before = new List<TransformTargetState>(targets.Count);
        foreach (var target in targets)
        {
            var captured = Capture(target);
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail ?? $"Could not capture {target}.");
            before.Add(captured.State);
        }

        var order = Enumerable.Range(0, before.Count);
        if (parentsFirst)
            order = order.OrderBy(index => _runtime.DependencyDepth(before[index].Target));
        var written = new List<TransformTargetState>(before.Count);
        foreach (int index in order)
        {
            var result = write(before[index], index);
            if (!result.Success)
            {
                string failure = result.Detail ?? $"{writeFailure} {before[index].Target}.";
                return written.Count == 0 ? GestureResult.Fail(failure) : PutBack(failure, written);
            }
            written.Add(before[index]);
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            var captured = _runtime.Capture(state.Target);
            if (!captured.Success || captured.State == null)
                return PutBack(
                    captured.Detail ?? $"Could not capture {state.Target} after {afterWhat}.", before);
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

    /// <summary>Restores targets this edit already changed and reports the failure.</summary>
    private GestureResult PutBack(string failure, IReadOnlyList<TransformTargetState> written)
    {
        var recovery = _gestures.AttemptRecovery(written);
        return GestureResult.Fail(TransformRecovery.AppendRollbackFailure(failure, recovery)) with
        {
            Recovery = recovery,
        };
    }
}
