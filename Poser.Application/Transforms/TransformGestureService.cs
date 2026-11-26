using System.Numerics;
using Poser.Domain.Operations;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public sealed record BeginTransformGesture(
    IReadOnlyList<TransformTargetId> Targets,
    TransformOperation Operation,
    TransformSpace Space,
    PivotMode PivotMode,
    Vector3? CustomPivot = null,
    string Description = "Transform",
    IReadOnlyDictionary<TransformTargetId, TransformDeltaMode>? TargetModes = null,
    /// <summary>When on, the other selected bones turn in the first bone's frame,
    /// instead of copying the first bone's change as it is.</summary>
    bool RelativeSecondaryBones = false,
    GroupScaleMode GroupScale = GroupScaleMode.SizesAndSpacing,
    Guid? GroupId = null,
    bool IsGroupTransform = false);

/// <summary>
/// Runs move, rotate and scale gestures such as a gizmo drag, restores the targets
/// when a gesture fails, and records undo steps.
/// Every caller runs on the game's main thread, one call at a time.
/// </summary>
public sealed class TransformGestureService : IDisposable, IUndoRunner
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private ActiveGestureState? _active;

    private readonly GroupTransformState? _groupTransforms;
    private readonly IGroupTransformSource? _groupSource;
    private readonly GroupTransformCoordinator? _groupCoordinator;
    private Action? _recoveryCompleted;
    private bool _recoveryRemap;

    public TransformGestureService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        EditHistory history,
        GroupTransformState? groupTransforms = null,
        IGroupTransformSource? groupSource = null,
        GroupTransformCoordinator? groupCoordinator = null)
    {
        _scene = scene;
        _runtime = runtime;
        History = history;
        _groupTransforms = groupTransforms;
        _groupSource = groupSource;
        _groupCoordinator = groupCoordinator;
        if (groupCoordinator != null)
        {
            groupCoordinator.ReadPresentation = ReadGroupPresentation;
            groupCoordinator.CaptureAllowed = () => PendingRecovery == null && _active == null;
            groupCoordinator.BeforeSelectionCapture = () =>
            {
                OnSelectionChanged(Array.Empty<SelectionId>());
                return PendingRecovery == null && _active == null;
            };
        }
        _scene.Selection.SelectionChanged += OnSelectionChanged;
    }

    public EditHistory History { get; }
    public TransformGestureId? ActiveGesture => _active?.Id;

    private GroupTransformPresentation ReadGroupPresentation(Guid? named, IReadOnlyList<TransformTargetId> targets)
    {
        // While a restore is still waiting, the members' current transforms may be
        // only half restored.
        if (PendingRecovery != null) return default;
        if (_active is not { } active) return new(true, null);
        if (active.GroupBefore is not { } before || active.Command.GroupId != named
            || active.SceneRevision != _scene.Revision || !before.HasSameMembership(targets)
            || _groupTransforms?.IsCurrent(GroupTransformKey.For(named, targets), before) != true)
            return default;
        return new(false, active.GroupProposed ?? before);
    }

    /// <summary>The pivot captured at Begin. Viewport handles use this same point
    /// when displaying rotation or scaling around a centroid or custom pivot.</summary>
    public Vector3? ActivePivot => _active?.Pivot;

    /// <summary>What the running gesture turns and scales around.</summary>
    public PivotMode? ActivePivotMode => _active?.Command.PivotMode;

    /// <summary>
    /// Values that a failed edit, cancelled drag or undo could not restore yet,
    /// usually because the actor was being redrawn. Until they are restored,
    /// new pose edits are turned away, so a half-restored pose is never recorded
    /// as the starting point of another undo step.
    /// </summary>
    public TransformRecoveryReceipt? PendingRecovery { get; private set; }

    public GestureResult Begin(BeginTransformGesture command)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        if (_active != null)
            return GestureResult.Fail("A transform gesture is already active.");
        if (!Enum.IsDefined(command.GroupScale))
            return GestureResult.Fail("The group scale mode is invalid.");
        if (command.IsGroupTransform && (command.Space != TransformSpace.World
            || command.PivotMode != PivotMode.Centroid
            || command.Targets.Any(target => target.Kind == TransformTargetKind.Bone)))
            return GestureResult.Fail("Entity group gestures require a world-space centroid.");
        if (command.Targets.Count == 0)
            return GestureResult.Fail("A transform gesture requires a target.");
        if (!IsHomogeneous(command.Targets))
            return GestureResult.Fail("Transform targets must be homogeneous.");
        if (command.PivotMode == PivotMode.Custom &&
            (command.CustomPivot is not { } supplied ||
             !TransformMath.IsFinite(supplied)))
            return GestureResult.Fail("A custom pivot must be finite.");

        var read = Read(command.Targets, out var captured);
        if (!read.Success)
            return read;

        // The pivot is fixed at the start, from the values captured then.
        // Working it out again each frame from the moving targets would feed
        // each frame's result into the next frame, which no gesture here may do.
        var pivot = command.PivotMode switch
        {
            PivotMode.PerTarget => captured[0].Transform.Position,
            PivotMode.Primary => captured[0].Transform.Position,
            PivotMode.Centroid => Centroid(captured),
            PivotMode.Custom => command.CustomPivot!.Value,
            _ => captured[0].Transform.Position,
        };

        var id = TransformGestureId.New();
        GroupTransformSnapshot? groupBefore = null;
        if (command.IsGroupTransform)
        {
            groupBefore = _groupTransforms?.Snapshot(
                command.GroupId, command.Targets);
            if (groupBefore == null)
                return GestureResult.Fail(
                    "The group transform frame is not initialized.");
            var capturedMap = captured.ToDictionary(state => state.Target, state => state.Transform);
            if (!GroupTransformReadModel.TryRead(groupBefore, capturedMap,
                    command.GroupScale, out _, out var refusal))
                return GestureResult.Fail(refusal!);
            foreach (var target in command.Targets)
                if (_groupSource?.Refusal(target) is { } capability)
                    return GestureResult.Fail(capability);
        }
        _active = new ActiveGestureState(
            id,
            _scene.Revision,
            command,
            pivot,
            captured.ToArray(),
            groupBefore);
        return GestureResult.Ok(id);
    }

    /// <summary>
    /// Reads every target in order, for a drag or a typed value. Stops at the
    /// first one that has left the scene or cannot be read, which happens
    /// while its actor is being redrawn.
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
    /// The average of the captured positions. The sum is kept in double
    /// precision so it stays accurate far from the world's origin; a single
    /// target returns its own position exactly.
    /// </summary>
    private static Vector3 Centroid(IReadOnlyList<TransformTargetState> captured)
    {
        if (captured.Count == 1)
            return captured[0].Transform.Position;
        double x = 0d, y = 0d, z = 0d;
        foreach (var state in captured)
        {
            x += state.Transform.Position.X;
            y += state.Transform.Position.Y;
            z += state.Transform.Position.Z;
        }
        return new Vector3(
            (float)(x / captured.Count),
            (float)(y / captured.Count),
            (float)(z / captured.Count));
    }

    public GestureResult Update(
        TransformGestureId gestureId,
        TransformDelta delta)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        if (_scene.Revision != active.SceneRevision)
        {
            var cancellation = CancelActive(active);
            return FailureAfterRecovery(
                "Scene changed during transform gesture.",
                cancellation.Recovery!);
        }
        if (active.GroupBefore != null ? !GroupTransformControls.ValidDelta(delta) : !delta.IsValid)
            return CancelWithFailure(active, "Transform delta is invalid.");

        delta = Filter(delta with { Rotation = TransformMath.NormalizeRotation(delta.Rotation) },
            active.Command.Operation);
        GroupTransformControls? nextControls = null;
        if (active.GroupBefore is { } frozen)
        {
            if (_groupTransforms == null || !_groupTransforms.IsCurrent(
                GroupTransformKey.For(active.Command.GroupId, active.Command.Targets), frozen))
                return CancelWithFailure(active, "The group changed during this gesture.");
            if (!frozen.Controls.TryAdvance(frozen.Baseline.Frame, delta,
                    active.Command.GroupScale, frozen.Controls.Position + delta.Translation, out var controls))
                return CancelWithFailure(active, "The cumulative group factors would overflow or become zero.");
            nextControls = controls;
        }
        var desired = new PoseTransform[active.Before.Count];
        try
        {
        for (var index = 0; index < active.Before.Count; index++)
        {
            var baseline = active.Before[index];
            // A symmetry partner gets an adjusted copy of the change.
            // Mirrored: the partner gets the mirror image of the change, taken
            // across the body's left-right plane rotated along with each bone's parent.
            // Direct (Link): the partner repeats the first bone's motion in its
            // own local frame, so a world-space change is converted through both
            // starting rotations, and a Local-space change already acts per bone.
            // No frame uses an applied result as its starting point.
            var targetDelta = delta;
            if (active.Command.TargetModes != null &&
                active.Command.TargetModes.TryGetValue(
                    baseline.Target,
                    out var mode))
            {
                targetDelta = mode switch
                {
                    TransformDeltaMode.Mirrored => TransformMath.MirrorAcrossParents(
                        delta,
                        active.Command.Space,
                        active.Before[0].AnimatedBaselineRotation,
                        baseline.AnimatedBaselineRotation,
                        active.Before[0].ParentRotationFromRest,
                        baseline.ParentRotationFromRest),
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
            // Turn the other bones relative to the first bone after symmetry,
            // and only those other bones. A Local-space change already uses each
            // bone's own frame, so converting it again would turn it twice.
            if (active.Command.RelativeSecondaryBones &&
                index != 0 &&
                active.Command.Space != TransformSpace.Local &&
                baseline.Target.Kind == TransformTargetKind.Bone)
            {
                targetDelta = TransformMath.RelativeToPrimary(
                    targetDelta,
                    active.Before[0].Transform.Rotation,
                    baseline.Transform.Rotation);
            }
            var rotatePosition = active.Command.PivotMode switch
            {
                PivotMode.PerTarget => false,
                PivotMode.Primary => index != 0,
                // The centre point belongs to no target, so every target swings
                // around it, the first one too. Leaving the first one out would
                // hold it in place and swing the rest around it, which is what
                // Primary mode does and what the centre point is there to avoid.
                PivotMode.Centroid => true,
                PivotMode.Custom => true,
                _ => false,
            };
            var pivot = active.Command.PivotMode == PivotMode.PerTarget
                ? baseline.Transform.Position
                : active.Pivot;
            // A shared pivot makes this a group gesture: the distances between
            // members scale, and each member's own size changes only in the
            // sizes-and-spacing mode. Scaling each target on its own pivot works
            // as usual. Bones never spread apart: scaling several bones scales
            // the bones themselves.
            bool scalePosition = rotatePosition
                && baseline.Target.Kind != TransformTargetKind.Bone;
            bool scaleOwn = !scalePosition
                || active.Command.GroupScale == GroupScaleMode.SizesAndSpacing;
            desired[index] = TransformMath.Apply(
                baseline.Transform,
                targetDelta,
                active.Command.Space,
                pivot,
                rotatePosition,
                scalePosition,
                scaleOwn,
                groupFactors: active.GroupBefore != null,
                scaleFrame: active.GroupBefore?.WorldRotation);
        }
        }
        catch (ArgumentOutOfRangeException)
        {
            return CancelWithFailure(active, "A group member transform would exceed the supported numeric range.");
        }

        foreach (var index in Enumerable.Range(0, active.Before.Count)
            .OrderBy(i => _runtime.DependencyDepth(active.Before[i].Target)))
        {
            var result = _runtime.ApplyAbsolute(active.Before[index], desired[index]);
            if (result.Success)
                continue;

            // If the game side cannot apply a value, the gesture ends. Every
            // target is restored to its starting value in order, even after one
            // restore fails, and the full record is kept so it can be retried.
            var rollback = RecoverGesture(active);
            _active = null;
            var applyDetail =
                result.Detail ??
                $"Runtime rejected target {active.Before[index].Target}.";
            return FailureAfterRecovery(applyDetail, rollback);
        }

        active = active with {
            GroupProposed = nextControls is { } proposed
                ? new GroupTransformSnapshot(active.GroupBefore!.Baseline,
                    active.Before.Select((state, i) => (state.Target, Value: desired[i]))
                        .ToDictionary(pair => pair.Target, pair => pair.Value),
                    proposed with { Position = GroupTransformBaseline.Centroid(desired) }) : null
        };
        _active = active;
        return GestureResult.Ok(gestureId);
    }

    public GestureResult Commit(TransformGestureId gestureId)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        if (_scene.Revision != active.SceneRevision)
        {
            var cancellation = CancelActive(active);
            return FailureAfterRecovery(
                "Scene changed during transform gesture.",
                cancellation.Recovery!);
        }

        var after = new List<TransformTargetState>(active.Before.Count);
        foreach (var before in active.Before)
        {
            var result = _runtime.Capture(before.Target);
            if (!result.Success || result.State == null)
            {
                var rollback = RecoverGesture(active);
                _active = null;
                return FailureAfterRecovery(
                    result.Detail ??
                    $"Could not capture final state for {before.Target}.",
                    rollback);
            }
            after.Add(result.State);
        }

        GroupTransformHistoryChange? groupChange = null;
        if (active.GroupBefore is { } frozen)
        {
            var key = GroupTransformKey.For(active.Command.GroupId, active.Command.Targets);
            var afterMap = after.ToDictionary(state => state.Target, state => state.Transform);
            var proposed = active.GroupProposed ?? frozen;
            if (_groupTransforms == null || !_groupTransforms.IsCurrent(key, frozen)
                || !GroupTransformReadModel.TryRead(proposed, afterMap,
                    active.Command.GroupScale, out _, out _))
                return CancelWithFailure(active, "The group changed before commit.");
            if (proposed.Controls == frozen.Controls
                && active.Before.All(state => GroupTransformReadModel.Equivalent(
                    state.Transform, afterMap[state.Target])))
            {
                _active = null;
                return GestureResult.Ok(gestureId);
            }
            var committed = new GroupTransformSnapshot(frozen.Baseline, afterMap, proposed.Controls);
            groupChange = new GroupTransformHistoryChange(key, frozen, committed);
            _groupTransforms.Put(key, committed);
        }

        History.Append(new TransformPatch(
            active.Command.Description,
            active.Before,
            after)
        {
            GroupState = groupChange,
        });
        _active = null;
        return GestureResult.Ok(gestureId);
    }

    public GestureResult Cancel(TransformGestureId gestureId)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        return CancelActive(active);
    }

    /// <summary>
    /// Called after the scene's entities are refreshed. The gesture goes on if
    /// all its targets are still there. Otherwise it is cancelled, and if its
    /// targets cannot all be restored, new edits are turned away until they are.
    /// </summary>
    public void ReconcileScene(Func<TransformTargetId, bool> isCurrent)
    {
        if (PendingRecovery != null)
            return;
        if (_active is not { } active)
            return;
        if (active.Command.Targets.All(isCurrent))
        {
            _active = active with { SceneRevision = _scene.Revision };
            return;
        }
        CancelActive(active);
    }

    /// <summary>
    /// Runs the undo or redo of the step <see cref="UndoJournal"/> picked, and
    /// moves the step to the other list only if it worked. A step that fails
    /// stays, so it can be tried again. The journal has already retried any
    /// restore that was still waiting.
    /// Undo and redo wait for a drag to end: the drag's undo step is recorded
    /// on release with the values its bones had when it started, so an undo
    /// run under it would come back when the drag is undone, with its step
    /// already gone from history.
    /// </summary>
    public HistoryRunResult Run(HistoryEntry entry, bool undo, SelectionId? entity)
    {
        var verb = undo ? "undo" : "redo";
        if (_active != null) return new(GestureResult.Fail($"Cancel the active gesture before {verb}."), false);
        Action commit = undo
            ? () => History.CommitUndo(entry, entity)
            : () => History.CommitRedo(entry, entity);
        if (entry is InverseEntry inverse)
            return new(RunLifecycle(undo ? inverse.Undo : inverse.Redo,
                $"Could not {verb} {inverse.Description.ToLowerInvariant()}.",
                commit, inverse.FailureDetail), true);
        return new(RestorePatch((TransformPatch)entry, undo, commit), true);
    }

    /// <summary>
    /// Runs the undo or redo of a step that carries its own undo and redo
    /// actions, and moves the step only if the action worked. A step that fails
    /// stays, so it can be tried again.
    /// </summary>
    private static GestureResult RunLifecycle(
        Func<bool> act, string failure, Action commit, Func<string?>? failureDetail = null)
    {
        bool landed;
        try
        {
            landed = act();
        }
        catch (Exception exception)
        {
            return GestureResult.Fail($"{failure} {exception.Message}");
        }
        if (!landed)
            return GestureResult.Fail(failureDetail?.Invoke() ?? failure);
        commit();
        return GestureResult.Ok();
    }

    /// <summary>Starts a step that takes several frames. Waits for a drag to
    /// end, for the same reason as <see cref="Run"/>.</summary>
    public HistoryRunResult Replay(JournalStep step, bool before, SelectionId? entity)
    {
        if (_active != null) return new(GestureResult.Fail("Cancel the active gesture before undo or redo."), false);
        return new(RunLifecycle(before ? step.Undo : step.Redo,
            $"Could not {(before ? "undo" : "redo")} {step.Description.ToLowerInvariant()}.",
            () => { }, step.FailureDetail), true);
    }

    public GestureResult RunValueTransition(Action action)
    {
        if (PendingRecovery != null) return RecoveryRequired(PendingRecovery);
        try { action(); return GestureResult.Ok(); }
        catch (Exception ex) { return GestureResult.Fail(ex.Message); }
    }

    public void Dispose()
    {
        if (_groupCoordinator != null) _groupCoordinator.ReadPresentation = (_, _) => default;
        _scene.Selection.SelectionChanged -= OnSelectionChanged;
        if (PendingRecovery != null)
            return;
        if (_active is { } active)
            CancelActive(active);
    }

    /// <summary>
    /// Undo and redo call this first. With a restore still waiting, it tries
    /// that restore again instead and returns its result; otherwise null.
    /// </summary>
    public GestureResult? RecoverPending()
    {
        if (PendingRecovery is not { } pending)
            return null;
        return RecoveryResult(Retry(pending));
    }

    internal TransformRecoveryReceipt AttemptRecovery(
        IReadOnlyList<TransformTargetState> states,
        Action? completed = null,
        bool remap = false)
    {
        var requested = remap
            ? states.Select(state => _scene.CurrentTransformTarget(state.Target) is { } current
                ? state with { Target = current } : state).ToArray()
            : states;
        var recovery = TransformRecovery.RestoreAll(_runtime, requested);
        PendingRecovery = recovery.Complete ? null : recovery;
        _recoveryCompleted = recovery.Complete ? null : completed;
        _recoveryRemap = !recovery.Complete && remap;
        if (recovery.Complete)
        {
            completed?.Invoke();
            _groupCoordinator?.BindingsPublished();
        }
        return recovery;
    }

    private GestureResult RestorePatch(TransformPatch patch, bool before, Action commit)
    {
        // A redraw can replace the bones during an undo, just as during a drag.
        // A restore left waiting is retried on the same bones of the redrawn body.
        var recovery = AttemptRecovery(before ? patch.Before : patch.After, () =>
        {
            if (patch.GroupState is { } change)
            {
                var current = _groupSource == null ? change : change.Remap(_groupSource.CurrentTarget);
                if (current != null)
                    _groupTransforms?.Restore(current, before);
            }
            commit();
        }, remap: true);
        // No target could be found, so nothing was written and nothing needs
        // restoring. The step fails and stays in history, to be tried on the
        // redrawn body.
        if (recovery.Attempts.All(attempt => attempt.Status == TransformPortStatus.StaleTarget))
        {
            PendingRecovery = null;
            _recoveryCompleted = null;
            _recoveryRemap = false;
        }
        return RecoveryResult(recovery);
    }

    /// <summary>
    /// Tries the waiting restore once more before an edit is turned away for
    /// it. A restore usually fails because its bones could not be reached for a
    /// moment, during a redraw. Nothing in the interface lets a person retry it,
    /// so without this, one short failure would block every later edit for good.
    /// Returns true when nothing is waiting any more.
    /// </summary>
    public bool TryCompleteRecovery()
    {
        if (PendingRecovery is not { } pending)
            return true;
        return Retry(pending).Complete;
    }

    private TransformRecoveryReceipt Retry(TransformRecoveryReceipt pending) =>
        AttemptRecovery(
            pending.Attempts.Select(attempt => attempt.RequestedState).ToArray(),
            _recoveryCompleted, _recoveryRemap);

    /// <summary>
    /// Restores transforms that another action saved, such as a pose import
    /// that failed part way. Any that cannot be restored yet become the
    /// <see cref="PendingRecovery"/> that later edits retry first.
    /// </summary>
    public GestureResult RestoreForOperation(
        IReadOnlyList<TransformTargetState> states)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        return RecoveryResult(AttemptRecovery(states));
    }

    internal GestureResult? RecoveryBarrier() =>
        TryCompleteRecovery()
            ? null
            : RecoveryRequired(PendingRecovery!);

    private GestureResult CancelActive(ActiveGestureState active)
    {
        _active = null;
        return RecoveryResult(RecoverGesture(active));
    }

    private GestureResult CancelWithFailure(ActiveGestureState active, string detail)
    {
        _active = null;
        return FailureAfterRecovery(detail, RecoverGesture(active));
    }

    // A redraw also cancels an ordinary bone drag. Keep the starting values
    // while the redrawn bones have no ids yet, and look up the new bones on
    // each retry, rather than retrying forever on bones that no longer exist.
    private TransformRecoveryReceipt RecoverGesture(ActiveGestureState active) =>
        AttemptRecovery(active.Before, () =>
        {
            if (active.GroupBefore is not { } frozen || _groupTransforms == null)
                return;
            var restored = _groupSource == null ? frozen : frozen.Remap(_groupSource.CurrentTarget);
            if (restored != null)
                _groupTransforms.Put(GroupTransformKey.For(active.Command.GroupId,
                    restored.Expected.Keys), restored);
        }, remap: true);

    private static GestureResult FailureAfterRecovery(
        string primaryFailure,
        TransformRecoveryReceipt recovery) =>
        GestureResult.Fail(TransformRecovery.AppendRollbackFailure(
            primaryFailure,
            recovery)) with
        {
            Recovery = recovery,
        };

    private static GestureResult RecoveryResult(
        TransformRecoveryReceipt recovery) =>
        recovery.Complete
            ? GestureResult.Ok() with { Recovery = recovery }
            : GestureResult.Fail(
                TransformRecovery.DescribeFailures(recovery)) with
            {
                Recovery = recovery,
            };

    private static GestureResult RecoveryRequired(
        TransformRecoveryReceipt recovery) =>
        GestureResult.Fail(
            "Transform recovery must complete before another mutation.") with
        {
            Recovery = recovery,
        };

    private static bool IsHomogeneous(
        IReadOnlyList<TransformTargetId> targets)
    {
        // Bones must all belong to one actor, but may come from different
        // skeletons of it, such as the body and a weapon. Other kinds of entity
        // may be mixed, because their transforms are all in world space.
        var first = targets[0];
        if (targets.Any(target => target.Kind == TransformTargetKind.Bone))
            return targets.All(target =>
                target.Kind == TransformTargetKind.Bone
                && target.ActorLineage == first.ActorLineage);
        return true;
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
        if (PendingRecovery != null)
            return;
        if (_active is { } active)
            CancelActive(active);
    }

    private sealed record ActiveGestureState(
        TransformGestureId Id,
        ulong SceneRevision,
        BeginTransformGesture Command,
        Vector3 Pivot,
        IReadOnlyList<TransformTargetState> Before,
        GroupTransformSnapshot? GroupBefore = null,
        GroupTransformSnapshot? GroupProposed = null);
}
