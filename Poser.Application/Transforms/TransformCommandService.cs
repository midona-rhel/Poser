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
    /// Bakes runtime-produced absolutes — a live IK solve — into the pose
    /// stacks as ONE history entry, with the same all-or-nothing discipline
    /// as <see cref="SetAbsoluteMany"/>: every target is captured before
    /// anything is written and the whole set rolls back on any failure.
    ///
    /// The difference is the basis. These absolutes were produced by the
    /// solver on top of the animation, not by a stack, so each one is
    /// re-expressed against the bone's animated baseline and REPLACES the
    /// bone's interactive stacks. Ordering between the writes cannot matter:
    /// no bone's baseline is a function of another bone's stacks.
    /// </summary>
    public GestureResult BakeAbsoluteMany(
        IReadOnlyList<(TransformTargetId Target, PoseTransform Desired)> writes,
        string description)
    {
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("A transform gesture is active.");
        if (writes.Count == 0)
            return GestureResult.Fail("Nothing to bake.");

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
            var applied = _runtime.ApplyBakedAbsolute(before[i], writes[i].Desired);
            if (applied.Success)
                continue;
            RestoreAll(before);
            return GestureResult.Fail(
                applied.Detail ?? $"Could not bake {writes[i].Target}.");
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
        // Two distinct state maps: `before` is the immutable pre-edit
        // capture for rollback and history, while `working` is the
        // application state a later step builds on. A reset REPLACES the
        // working state, so file writes for a reset bone apply over the
        // cleared stacks instead of resurrecting the pre-reset pose.
        var working = new Dictionary<TransformTargetId, TransformTargetState>(order.Count);
        foreach (var target in order)
        {
            var captured = Capture(target);
            if (!captured.Success || captured.State == null)
                return GestureResult.Fail(captured.Detail!);
            before.Add(captured.State);
            working[target] = captured.State;
        }

        GestureResult FailWithRollback(string detail)
        {
            // Every captured target is attempted independently — a restore
            // that throws must not stop restoration of the targets after
            // it — and returned and thrown failures aggregate together.
            var rollbackFailures = new List<string>();
            foreach (var state in before)
            {
                try
                {
                    var restored = _runtime.Restore(state);
                    if (!restored.Success)
                        rollbackFailures.Add(
                            restored.Detail ?? $"Could not restore {state.Target}.");
                }
                catch (Exception ex)
                {
                    rollbackFailures.Add($"{state.Target}: {ex.Message}");
                }
            }
            return GestureResult.Fail(rollbackFailures.Count == 0
                ? detail
                : $"{detail} Rollback also failed: {string.Join("; ", rollbackFailures)}");
        }

        try
        {
            foreach (var target in resets)
            {
                var reset = working[target] with
                {
                    Pose = PoseOperations.Reset(working[target].Pose),
                    HasOverride = false,
                };
                var applied = _runtime.Restore(reset);
                if (!applied.Success)
                    return FailWithRollback(applied.Detail ?? $"Could not reset {target}.");
                working[target] = reset;
            }

            foreach (var (target, desired) in writes)
            {
                var applied = _runtime.ApplyAbsolute(working[target], desired, rawBaseline: true);
                if (!applied.Success)
                    return FailWithRollback(applied.Detail ?? $"Could not transform {target}.");
            }

            if (model is { } edit)
            {
                var state = working[edit.Target];
                var applied = edit.Absolute is { } absolute
                    ? _runtime.ApplyAbsolute(state, absolute)
                    : _runtime.Restore(state with { HasOverride = false });
                if (!applied.Success)
                    return FailWithRollback(
                        applied.Detail ?? "Could not apply the model transform.");
            }

            var after = new List<TransformTargetState>(before.Count);
            foreach (var state in before)
            {
                var captured = _runtime.Capture(state.Target);
                if (!captured.Success || captured.State == null)
                    return FailWithRollback(
                        captured.Detail ?? $"Could not capture {state.Target}.");
                after.Add(captured.State);
            }

            _history.Append(new TransformPatch(description, before, after));
            return GestureResult.Ok();
        }
        catch (Exception ex)
        {
            // A thrown mutation is the same as a returned failure: restore
            // every captured target, append no history item.
            return FailWithRollback($"The import edit failed unexpectedly: {ex.Message}");
        }
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
