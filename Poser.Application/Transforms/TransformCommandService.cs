using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>Atomic non-interactive transform commands.</summary>
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
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail(
                "A transform gesture is active.");
        var captured = Capture(target);
        if (!captured.Success || captured.State == null)
            return GestureResult.Fail(captured.Detail!);
        var before = captured.State;

        var applied = _runtime.ApplyAbsolute(before, desired);
        if (!applied.Success)
            return GestureResult.Fail(
                applied.Detail ?? $"Could not transform {target}.");

        var after = _runtime.Capture(target);
        if (!after.Success || after.State == null)
        {
            _runtime.Restore(before);
            return GestureResult.Fail(
                after.Detail ?? $"Could not capture {target}.");
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
    /// failure restores what was already applied, so the edit either
    /// lands whole or not at all. Targets not supplied are untouched,
    /// which is what leaves expression, gaze, and unrelated manual edits
    /// intact.
    /// </summary>
    public GestureResult SetAbsoluteMany(
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        string description,
        bool rawBaseline = false)
    {
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("A transform gesture is active.");
        if (writes.Count == 0)
            return GestureResult.Fail("Nothing to apply.");

        var before = new List<TransformTargetState>(writes.Count);
        foreach (var (target, _) in writes)
        {
            var captured = Capture(target);
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
        }

        for (int i = 0; i < writes.Count; i++)
        {
            var applied = _runtime.ApplyAbsolute(before[i], writes[i].Desired, rawBaseline);
            if (applied.Success)
                continue;
            RestoreAll(before);
            return GestureResult.Fail(
                applied.Detail ?? $"Could not transform {writes[i].Target}.");
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            var captured = _runtime.Capture(state.Target);
            if (!captured.Success || captured.State == null)
            {
                RestoreAll(before);
                return GestureResult.Fail(
                    captured.Detail ?? $"Could not capture {state.Target}.");
            }
            after.Add(captured.State);
        }

        _history.Append(new TransformPatch(description, before, after));
        return GestureResult.Ok();
    }

    /// <summary>
    /// One atomic pose-file import: reset-before-import and application
    /// form a SINGLE edit. Every affected slot-qualified target is
    /// captured before anything changes; resets clear the captured pose
    /// stacks, the file's transforms then write against the live raw
    /// basis, and the model transform applies (or clears) once on the
    /// owning actor. Any failure restores every captured target and
    /// creates no history item; success appends exactly one.
    /// </summary>
    public GestureResult ImportEdit(
        IReadOnlyList<TransformTargetId> resets,
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        (TransformTargetId Target, PoseTransform? Absolute)? model,
        string description)
    {
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("A transform gesture is active.");
        if (resets.Count == 0 && writes.Count == 0 && model == null)
            return GestureResult.Fail("The pose file affects nothing on this actor.");

        // Capture every affected exact target once, before any write.
        var order = new List<TransformTargetId>();
        var seen = new HashSet<TransformTargetId>();
        foreach (var target in resets)
            if (seen.Add(target))
                order.Add(target);
        foreach (var (target, _) in writes)
            if (seen.Add(target))
                order.Add(target);
        if (model is { } modelEdit && seen.Add(modelEdit.Target))
            order.Add(modelEdit.Target);

        var before = new List<TransformTargetState>(order.Count);
        var byTarget = new Dictionary<TransformTargetId, TransformTargetState>(order.Count);
        foreach (var target in order)
        {
            var captured = Capture(target);
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
            byTarget[target] = captured.State;
        }

        foreach (var target in resets)
        {
            var state = byTarget[target];
            var applied = _runtime.Restore(state with
            {
                Pose = PoseOperations.Reset(state.Pose),
                HasOverride = false,
            });
            if (applied.Success)
                continue;
            RestoreAll(before);
            return GestureResult.Fail(
                applied.Detail ?? $"Could not reset {target}.");
        }

        foreach (var (target, desired) in writes)
        {
            var applied = _runtime.ApplyAbsolute(byTarget[target], desired, rawBaseline: true);
            if (applied.Success)
                continue;
            RestoreAll(before);
            return GestureResult.Fail(
                applied.Detail ?? $"Could not transform {target}.");
        }

        if (model is { } edit)
        {
            var state = byTarget[edit.Target];
            var applied = edit.Absolute is { } absolute
                ? _runtime.ApplyAbsolute(state, absolute)
                : _runtime.Restore(state with { HasOverride = false });
            if (!applied.Success)
            {
                RestoreAll(before);
                return GestureResult.Fail(
                    applied.Detail ?? "Could not apply the model transform.");
            }
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            var captured = _runtime.Capture(state.Target);
            if (!captured.Success || captured.State == null)
            {
                RestoreAll(before);
                return GestureResult.Fail(
                    captured.Detail ?? $"Could not capture {state.Target}.");
            }
            after.Add(captured.State);
        }

        _history.Append(new TransformPatch(description, before, after));
        return GestureResult.Ok();
    }

    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets,
        string description)
    {
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
            var captured = Capture(target);
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
        }

        foreach (var state in before)
        {
            var cleared = state with { HasOverride = false };
            var result = _runtime.Restore(cleared);
            if (result.Success)
                continue;
            RestoreAll(before);
            return GestureResult.Fail(
                result.Detail ??
                $"Could not clear actor override {state.Target}.");
        }

        var after = new List<TransformTargetState>(before.Count);
        foreach (var state in before)
        {
            var captured = _runtime.Capture(state.Target);
            if (!captured.Success || captured.State == null)
            {
                RestoreAll(before);
                return GestureResult.Fail(
                    captured.Detail ??
                    $"Could not capture {state.Target} after reset.");
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

    private void RestoreAll(
        IReadOnlyList<TransformTargetState> states)
    {
        foreach (var state in states)
            _runtime.Restore(state);
    }
}
