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
    /// <summary>When enabled, secondary bones rotate in the primary's frame
    /// instead of receiving the primary's raw delta.</summary>
    bool RelativeSecondaryBones = false,
    GroupScaleMode GroupScale = GroupScaleMode.SizesAndSpacing,
    Guid? GroupId = null,
    bool IsGroupTransform = false);

/// <summary>
/// Coordinates transform gestures, rollback recovery, and history entries.
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
        // While a put-back is pending, the members' live transforms may be partial.
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

    /// <summary>
    /// An incomplete exact-state restore. While present it blocks every new
    /// transform/pose mutation so partial state cannot become a new baseline.
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

        // Frozen at Begin, from the captured baselines: a pivot re-derived
        // per frame from the moving targets would feed a result back as the
        // next frame's input, which is the one thing every gesture here
        // refuses to do.
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
    /// The arithmetic mean of the captured positions. The sum uses double
    /// precision to preserve low bits for large scenes; a single target
    /// returns its position exactly.
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
            return GestureResult.Fail("Transform delta is invalid.");

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
                return GestureResult.Fail("The cumulative group factors would overflow or become zero.");
            nextControls = controls;
        }
        var desired = new PoseTransform[active.Before.Count];
        try
        {
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
            // Apply relative secondary rotation after symmetry and only to
            // secondary bones. Local-space deltas already use each target's
            // own frame, so rebasing them again would rotate twice.
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
                // The centroid is nobody's own origin, so EVERY target swings
                // about it — including the primary, unlike Primary mode:
                // exempting index 0 would pin one member of the group in
                // place and swing the rest around it, the exact behaviour the
                // centroid exists to replace.
                PivotMode.Centroid => true,
                PivotMode.Custom => true,
                _ => false,
            };
            var pivot = active.Command.PivotMode == PivotMode.PerTarget
                ? baseline.Transform.Position
                : active.Pivot;
            // A shared pivot makes this a group gesture: the offsets scale
            // with it, and each member's own size follows only in the
            // sizes-and-spacing mode. Per-target scaling is unchanged.
            // Bones never spread: scaling several bones scales the bones.
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
            return GestureResult.Fail("A group member transform would exceed the supported numeric range.");
        }

        foreach (var index in Enumerable.Range(0, active.Before.Count)
            .OrderBy(i => _runtime.DependencyDepth(active.Before[i].Target)))
        {
            var result = _runtime.ApplyAbsolute(active.Before[index], desired[index]);
            if (result.Success)
                continue;

            // A runtime apply failure ends the gesture. Every frozen Before
            // state is attempted in order even after a restore failure, and
            // the complete receipt remains available for exact retry.
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
    /// Reconciles the active gesture with a completed scene refresh. The
    /// gesture survives when all targets remain current; otherwise it is
    /// cancelled and incomplete recovery remains a mutation barrier.
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
    /// Runs one direction of the entry <see cref="UndoJournal"/> peeked and
    /// commits it only when it lands. A refused entry remains available to
    /// retry. The journal has already finished any pending put-back first.
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
    /// Runs one direction of a lifecycle entry and commits it only when the
    /// action reports success. A refused action remains available to retry.
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
        if (_active != null) return GestureResult.Fail("Cancel the active gesture before changing actor values.");
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
    /// Undo and redo call this first. With a put-back still pending it tries
    /// that put-back again instead, and returns its result; otherwise null.
    /// </summary>
    public GestureResult? RecoverPending()
    {
        if (PendingRecovery is not { } pending)
            return null;
        if (_active != null)
            return GestureResult.Fail(
                "Cancel the active gesture before retrying recovery.") with
            {
                Recovery = pending,
            };
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
        // A redraw replaces the bones under an undo as it does under a gesture:
        // a restore it left pending retries on the same bones of the new body.
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
        // No target was bound, so nothing was written and nothing is left to recover:
        // the step is refused and stays in history for the redrawn body.
        if (recovery.Attempts.All(attempt => attempt.Status == TransformPortStatus.StaleTarget))
        {
            PendingRecovery = null;
            _recoveryCompleted = null;
            _recoveryRemap = false;
        }
        return RecoveryResult(recovery);
    }

    /// <summary>
    /// One automatic retry of the pending recovery, run before a mutation is
    /// refused for it. A recovery usually goes incomplete because its bones
    /// were unreachable for a moment (mid-redraw), and nothing in the product
    /// retries by hand — without this, one transient failure refused every
    /// later mutation permanently. Returns true when nothing is pending
    /// anymore.
    /// </summary>
    public bool TryCompleteRecovery()
    {
        if (PendingRecovery is not { } pending)
            return true;
        if (_active != null)
            return false;
        return Retry(pending).Complete;
    }

    private TransformRecoveryReceipt Retry(TransformRecoveryReceipt pending) =>
        AttemptRecovery(
            pending.Attempts.Select(attempt => attempt.RequestedState).ToArray(),
            _recoveryCompleted, _recoveryRemap);

    /// <summary>
    /// Puts back transforms another operation saved (a pose import that
    /// failed part-way). If some cannot be put back yet, they become the
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

    // A redraw cancels ordinary bone gestures too. Retain their baseline
    // through the publication gap and resolve its exact bone identity on
    // retry, rather than permanently retrying a retired skeleton generation.
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
        // Bone targets must belong to one logical actor, but may span its slots.
        // Other entity kinds may be mixed because their transforms use world space.
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
