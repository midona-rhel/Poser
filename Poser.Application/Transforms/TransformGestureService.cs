using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public readonly record struct TransformGestureId(Guid Value)
{
    public static TransformGestureId New() => new(Guid.NewGuid());
}

public sealed record BeginTransformGesture(
    IReadOnlyList<TransformTargetId> Targets,
    TransformOperation Operation,
    TransformSpace Space,
    PivotMode PivotMode,
    Vector3? CustomPivot = null,
    string Description = "Transform",
    IReadOnlyDictionary<TransformTargetId, TransformDeltaMode>? TargetModes = null);

public readonly record struct GestureResult(
    bool Success,
    string? Detail = null,
    TransformGestureId? GestureId = null)
{
    public static GestureResult Ok(TransformGestureId? id = null) =>
        new(true, null, id);
    public static GestureResult Fail(string detail) =>
        new(false, detail);
}

/// <summary>
/// Idempotent transform gesture and patch-history coordinator.
/// </summary>
public sealed class TransformGestureService : IDisposable
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private ActiveGestureState? _active;

    public TransformGestureService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        TransformHistory history)
    {
        _scene = scene;
        _runtime = runtime;
        History = history;
        _scene.Selection.SelectionChanged += OnSelectionChanged;
    }

    public TransformHistory History { get; }
    public TransformGestureId? ActiveGesture => _active?.Id;

    public GestureResult Begin(BeginTransformGesture command)
    {
        if (_active != null)
            return GestureResult.Fail("A transform gesture is already active.");
        if (command.Targets.Count == 0)
            return GestureResult.Fail("A transform gesture requires a target.");
        if (command.Targets.Distinct().Count() != command.Targets.Count)
            return GestureResult.Fail("A transform gesture contains duplicate targets.");
        if (!IsHomogeneous(command.Targets))
            return GestureResult.Fail("Transform targets must be homogeneous.");
        if (command.PivotMode == PivotMode.Custom &&
            (command.CustomPivot is not { } supplied ||
             !TransformMath.IsFinite(supplied)))
            return GestureResult.Fail("A custom pivot must be finite.");
        if (command.TargetModes != null &&
            command.TargetModes.Keys.Any(
                target => !command.Targets.Contains(target)))
            return GestureResult.Fail(
                "A transform target mode references an unknown target.");

        var captured = new List<TransformTargetState>(command.Targets.Count);
        foreach (var target in command.Targets)
        {
            if (!_scene.Contains(target))
                return GestureResult.Fail($"Target {target} is stale.");
            var result = _runtime.Capture(target);
            if (!result.Success || result.State == null)
                return GestureResult.Fail(
                    result.Detail ?? $"Could not capture {target}.");
            captured.Add(result.State);
        }

        var pivot = command.PivotMode switch
        {
            PivotMode.PerTarget => captured[0].Transform.Position,
            PivotMode.Primary => captured[0].Transform.Position,
            PivotMode.Custom => command.CustomPivot!.Value,
            _ => captured[0].Transform.Position,
        };

        var id = TransformGestureId.New();
        _active = new ActiveGestureState(
            id,
            _scene.Revision,
            command,
            pivot,
            captured.ToArray());
        return GestureResult.Ok(id);
    }

    public GestureResult Update(
        TransformGestureId gestureId,
        TransformDelta delta)
    {
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        if (_scene.Revision != active.SceneRevision)
        {
            Cancel(gestureId);
            return GestureResult.Fail("Scene changed during transform gesture.");
        }
        if (!delta.IsValid)
            return GestureResult.Fail("Transform delta is invalid.");

        delta = Filter(delta.Normalized(), active.Command.Operation);
        var desired = new PoseTransform[active.Before.Count];
        for (var index = 0; index < active.Before.Count; index++)
        {
            var baseline = active.Before[index];
            // Symmetry partners transform the delta before it applies.
            // Mirrored: Local-space deltas act in each bone's own frame, so
            // they rebase through BOTH frozen animated baselines (captured
            // at Begin); model-frame deltas reflect directly. Direct (Link):
            // the partner repeats the primary's motion in its OWN local
            // frame — world-frame deltas rebase through both baselines,
            // Local-space deltas already act per-bone. No frame feeds an
            // applied result back as a baseline.
            var targetDelta = delta;
            if (active.Command.TargetModes != null &&
                active.Command.TargetModes.TryGetValue(
                    baseline.Target,
                    out var mode))
            {
                targetDelta = mode switch
                {
                    TransformDeltaMode.Mirrored =>
                        active.Command.Space == TransformSpace.Local
                            ? TransformMath.MirrorRebased(
                                delta,
                                active.Before[0].AnimatedBaselineRotation,
                                baseline.AnimatedBaselineRotation)
                            : TransformMath.Mirror(delta),
                    TransformDeltaMode.Direct =>
                        active.Command.Space == TransformSpace.Local
                            ? delta
                            : TransformMath.LinkRebased(
                                delta,
                                active.Before[0].AnimatedBaselineRotation,
                                baseline.AnimatedBaselineRotation),
                    _ => delta,
                };
            }
            var rotatePosition = active.Command.PivotMode switch
            {
                PivotMode.PerTarget => false,
                PivotMode.Primary => index != 0,
                PivotMode.Custom => true,
                _ => false,
            };
            var pivot = active.Command.PivotMode == PivotMode.PerTarget
                ? baseline.Transform.Position
                : active.Pivot;
            desired[index] = TransformMath.Apply(
                baseline.Transform,
                targetDelta,
                active.Command.Space,
                pivot,
                rotatePosition);
        }

        for (var index = 0; index < active.Before.Count; index++)
        {
            var result = _runtime.ApplyAbsolute(
                active.Before[index],
                desired[index]);
            if (result.Success)
                continue;

            // A runtime apply failure ends the gesture: restore the captured
            // Before states exactly once and clear the active gesture before
            // returning, so callers observe it as already cancelled and never
            // restore again. The original apply detail is preserved; a failed
            // rollback appends its own detail.
            var rollback = RestoreAll(active.Before);
            _active = null;
            var applyDetail =
                result.Detail ??
                $"Runtime rejected target {active.Before[index].Target}.";
            return GestureResult.Fail(
                rollback.Success
                    ? applyDetail
                    : $"{applyDetail} Rollback also failed: {rollback.Detail}");
        }

        return GestureResult.Ok(gestureId);
    }

    public GestureResult Commit(TransformGestureId gestureId)
    {
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        if (_scene.Revision != active.SceneRevision)
        {
            Cancel(gestureId);
            return GestureResult.Fail(
                "Scene changed during transform gesture.");
        }

        var after = new List<TransformTargetState>(active.Before.Count);
        foreach (var before in active.Before)
        {
            var result = _runtime.Capture(before.Target);
            if (!result.Success || result.State == null)
            {
                RestoreAll(active.Before);
                _active = null;
                return GestureResult.Fail(
                    result.Detail ?? $"Could not capture final state for {before.Target}.");
            }
            after.Add(result.State);
        }

        History.Append(new TransformPatch(
            active.Command.Description,
            active.Before,
            after));
        _active = null;
        return GestureResult.Ok(gestureId);
    }

    public GestureResult Cancel(TransformGestureId gestureId)
    {
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        var result = RestoreAll(active.Before);
        _active = null;
        return result;
    }

    public GestureResult Undo()
    {
        if (_active != null)
            return GestureResult.Fail("Cancel the active gesture before undo.");
        var patch = History.PeekUndo();
        if (patch == null)
            return GestureResult.Fail("Nothing to undo.");
        var result = RestoreAll(patch.Before);
        if (result.Success)
            History.CommitUndo(patch);
        return result;
    }

    public GestureResult Redo()
    {
        if (_active != null)
            return GestureResult.Fail("Cancel the active gesture before redo.");
        var patch = History.PeekRedo();
        if (patch == null)
            return GestureResult.Fail("Nothing to redo.");
        var result = RestoreAll(patch.After);
        if (result.Success)
            History.CommitRedo(patch);
        return result;
    }

    public void Dispose()
    {
        _scene.Selection.SelectionChanged -= OnSelectionChanged;
        if (_active is { } active)
            Cancel(active.Id);
    }

    private GestureResult RestoreAll(IReadOnlyList<TransformTargetState> states)
    {
        string? firstFailure = null;
        foreach (var state in states)
        {
            var result = _runtime.Restore(state);
            if (!result.Success && firstFailure == null)
                firstFailure =
                    result.Detail ?? $"Could not restore {state.Target}.";
        }
        return firstFailure == null
            ? GestureResult.Ok()
            : GestureResult.Fail(firstFailure);
    }

    private static bool IsHomogeneous(
        IReadOnlyList<TransformTargetId> targets)
    {
        var first = targets[0];
        if (targets.Any(target => target.Kind != first.Kind))
            return false;
        return first.Kind != TransformTargetKind.Bone ||
               targets.All(target =>
                   target.ActorLineage == first.ActorLineage);
    }

    private static TransformDelta Filter(
        TransformDelta delta,
        TransformOperation operation) =>
        operation switch
        {
            TransformOperation.Translate =>
                TransformDelta.Identity with
                {
                    Translation = delta.Translation,
                },
            TransformOperation.Rotate =>
                TransformDelta.Identity with
                {
                    Rotation = delta.Rotation,
                },
            TransformOperation.Scale =>
                TransformDelta.Identity with
                {
                    ScaleFactor = delta.ScaleFactor,
                },
            _ => delta,
        };

    private void OnSelectionChanged(
        IReadOnlyList<SelectionId> _)
    {
        if (_active is { } active)
            Cancel(active.Id);
    }

    private sealed record ActiveGestureState(
        TransformGestureId Id,
        ulong SceneRevision,
        BeginTransformGesture Command,
        Vector3 Pivot,
        IReadOnlyList<TransformTargetState> Before);
}
