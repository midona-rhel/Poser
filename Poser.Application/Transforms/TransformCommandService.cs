using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>
/// Changes transforms from a typed value or a command, outside a drag. Each call
/// is one undo step. The pose commands (Reset, Mirror, Flip, applying a copied
/// pose) use the same read, write and record steps; only how they work out the
/// new values differs.
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
        Refusal() ?? Change([target], (before, _) => _runtime.ApplyAbsolute(before, desired), description);

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
            description,
            parentsFirst: true);

    /// <summary>Lets the game place these actors again, as one undo step.</summary>
    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets,
        string description) =>
        Refusal() ?? Change(
            targets,
            (before, _) => _runtime.Restore(before with { HasOverride = false }),
            description);

    /// <summary>
    /// Refuses while an earlier failed edit is still being put back, or during
    /// a drag. A drag can be live here: an expression bake writes a few frames
    /// after it starts, and a gizmo can be grabbed in between.
    /// </summary>
    internal GestureResult? Refusal() =>
        _gestures.RecoveryBarrier() ??
        (_gestures.ActiveGesture != null ? GestureResult.Fail("A transform gesture is active.") : null);

    /// <summary>
    /// Reads every target in order. Stops at the first one that has left the
    /// scene or cannot be read, which happens while its actor is being redrawn.
    /// </summary>
    internal GestureResult Read(
        IReadOnlyList<TransformTargetId> targets,
        out List<TransformTargetState> states)
    {
        states = new List<TransformTargetState>(targets.Count);
        foreach (var target in targets)
        {
            if (!_scene.Contains(target))
                return GestureResult.Fail($"Target {target} is stale.");
            var captured = _runtime.Capture(target);
            if (!captured.Success)
                return GestureResult.Fail(captured.Detail!);
            states.Add(captured.State!);
        }
        return GestureResult.Ok();
    }

    /// <summary>
    /// Writes every target that was read, reads them all again and records the
    /// change as one undo step named by <paramref name="description"/>.
    /// </summary>
    internal GestureResult Commit(
        IReadOnlyList<TransformTargetState> before,
        Func<TransformTargetState, int, TransformPortResult> write,
        string description,
        bool parentsFirst = false)
    {
        var result = Write(before, write, parentsFirst, out var after);
        if (result.Success)
            _history.Append(new TransformPatch(description, before, after));
        return result;
    }

    /// <summary>
    /// Writes every target that was read and reads them all again, without
    /// recording an undo step. Reset all uses this directly because it records
    /// one step for the pose and everything else it resets.
    /// A failed write changes nothing by itself. When a write fails after
    /// earlier ones landed, those earlier targets are put back: resetting
    /// several actors can stop at one whose parent is gone. When the read
    /// after writing fails, every target is put back.
    /// </summary>
    internal GestureResult Write(
        IReadOnlyList<TransformTargetState> before,
        Func<TransformTargetState, int, TransformPortResult> write,
        bool parentsFirst,
        out List<TransformTargetState> after)
    {
        after = new List<TransformTargetState>(before.Count);
        var order = Enumerable.Range(0, before.Count);
        if (parentsFirst)
            order = order.OrderBy(index => _runtime.DependencyDepth(before[index].Target));
        var written = new List<TransformTargetState>(before.Count);
        foreach (int index in order)
        {
            var result = write(before[index], index);
            if (!result.Success)
                return written.Count == 0
                    ? GestureResult.Fail(result.Detail!)
                    : PutBack(result.Detail!, written);
            written.Add(before[index]);
        }

        foreach (var state in before)
        {
            var captured = _runtime.Capture(state.Target);
            if (!captured.Success)
                return PutBack(captured.Detail!, before);
            after.Add(captured.State!);
        }
        return GestureResult.Ok();
    }

    private GestureResult Change(
        IReadOnlyList<TransformTargetId> targets,
        Func<TransformTargetState, int, TransformPortResult> write,
        string description,
        bool parentsFirst = false)
    {
        var read = Read(targets, out var before);
        return read.Success ? Commit(before, write, description, parentsFirst) : read;
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
