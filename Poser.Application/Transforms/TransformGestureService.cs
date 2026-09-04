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
/// Idempotent transform gesture, recovery, and patch-history coordinator.
/// Public transitions run serialized on the Application/framework thread.
/// The narrow guard rejects synchronous port-callback reentry; it is not a
/// lock, scheduler, or cross-thread coordinator.
/// </summary>
public sealed class TransformGestureService : IDisposable, IUndoRunner
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private ActiveGestureState? _active;
    private bool _transitionActive;

    private readonly JournalContexts? _journal;
    private readonly GroupTransformState? _groupTransforms;
    private readonly IGroupTransformSource? _groupSource;
    private readonly GroupTransformCoordinator? _groupCoordinator;
    private Action? _recoveryCompleted;
    private bool _recoveryRemap;

    public TransformGestureService(
        SceneSession scene,
        ITransformRuntimePort runtime,
        TransformHistory history,
        JournalContexts? journal = null,
        GroupTransformState? groupTransforms = null,
        IGroupTransformSource? groupSource = null,
        GroupTransformCoordinator? groupCoordinator = null)
    {
        _scene = scene;
        _runtime = runtime;
        History = history;
        _journal = journal;
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

    public TransformHistory History { get; }
    public TransformGestureId? ActiveGesture => _active?.Id;

    private GroupTransformPresentation ReadGroupPresentation(Guid? named, IReadOnlyList<TransformTargetId> targets)
    {
        // Never publish midway through native writes, rollback, or recovery.
        if (_transitionActive || PendingRecovery != null) return default;
        if (_active is not { } active) return new(true, null);
        if (active.GroupBefore is not { } before || active.Command.GroupId != named
            || active.SceneRevision != _scene.Revision || !before.HasSameMembership(targets)
            || _groupTransforms?.IsCurrent(GroupTransformKey.For(named, targets), before) != true)
            return default;
        return new(false, active.GroupProposed ?? before);
    }

    /// <summary>The point the running gesture rotates and scales ABOUT, frozen
    /// at Begin. Published because a surface that draws a handle has to draw it
    /// where the thing it moves has GOT to — under
    /// <see cref="PivotMode.Centroid"/> and <see cref="PivotMode.Custom"/> the
    /// primary orbits this point like every other target, so a handle echoing
    /// the primary must orbit it too, and it must be THIS point rather than one
    /// the surface derived again from live positions.</summary>
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
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
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
            TransformPortResult result;
            try
            {
                result = _runtime.Capture(target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture {target}", exception, captured);
            }
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
            groupBefore,
            _journal?.BeginActorStep(
                captured.Select(state => state.Target.ActorLineage)));
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
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
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

        for (var index = 0; index < active.Before.Count; index++)
        {
            TransformPortResult result;
            try
            {
                result = _runtime.ApplyAbsolute(
                    active.Before[index],
                    desired[index]);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not transform {active.Before[index].Target}",
                    exception,
                    active.Before);
            }
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
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
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
            TransformPortResult result;
            try
            {
                result = _runtime.Capture(before.Target);
            }
            catch (Exception exception)
            {
                return ThrownFailureAfterRecovery(
                    $"Could not capture final state for {before.Target}",
                    exception,
                    active.Before);
            }
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
            Context = active.Journal?.Complete(),
            GroupState = groupChange,
        });
        _active = null;
        return GestureResult.Ok(gestureId);
    }

    public GestureResult Cancel(TransformGestureId gestureId)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
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
        using var transition = TryEnterTransition();
        if (transition == null)
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

    public GestureResult Undo()
    {
        if (RecoverPending() is { } recovered)
            return recovered;
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        if (_active != null)
            return GestureResult.Fail("Cancel the active gesture before undo.");
        var entry = History.PeekUndo();
        if (entry == null)
            return GestureResult.Fail("Nothing to undo.");
        if (entry is SceneLifecyclePatch lifecycle)
            return RunLifecycle(
                lifecycle.Undo,
                $"Could not undo {lifecycle.Description.ToLowerInvariant()}.",
                () => History.CommitUndo(entry));
        if (entry is JournalStep step)
            return RunLifecycle(
                step.Undo,
                $"Could not undo {step.Description.ToLowerInvariant()}.",
                () => History.CommitUndo(entry), step.FailureDetail);
        var patch = (TransformPatch)entry;
        return RestorePatch(patch, true, () => History.CommitUndo(patch));
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

    public GestureResult Redo()
    {
        if (RecoverPending() is { } recovered)
            return recovered;
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        if (_active != null)
            return GestureResult.Fail("Cancel the active gesture before redo.");
        var entry = History.PeekRedo();
        if (entry == null)
            return GestureResult.Fail("Nothing to redo.");
        if (entry is SceneLifecyclePatch lifecycle)
            return RunLifecycle(
                lifecycle.Redo,
                $"Could not redo {lifecycle.Description.ToLowerInvariant()}.",
                () => History.CommitRedo(entry));
        if (entry is JournalStep step)
            return RunLifecycle(
                step.Redo,
                $"Could not redo {step.Description.ToLowerInvariant()}.",
                () => History.CommitRedo(entry), step.FailureDetail);
        var patch = (TransformPatch)entry;
        return RestorePatch(patch, false, () => History.CommitRedo(patch));
    }

    public GestureResult RunValueTransition(Action action)
    {
        if (PendingRecovery != null) return RecoveryRequired(PendingRecovery);
        using var transition = TryEnterTransition();
        if (transition == null) return Busy();
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
        // Dispose has no result channel for Busy. If a port callback violates
        // the non-reentrant transition contract, unsubscribe but never start a
        // second restore or replace recovery owned by the outer transition.
        using var transition = TryEnterTransition();
        if (transition == null)
            return;
        if (_active is { } active)
            CancelActive(active);
    }

    /// <summary>
    /// The only mutation accepted while recovery is pending. Replays every
    /// originally requested state in order and replaces the receipt on another
    /// partial failure; complete recovery clears the mutation barrier.
    /// </summary>
    public GestureResult RetryRecovery(TransformRecoveryReceipt recovery)
    {
        if (PendingRecovery is not { } pending)
        {
            return GestureResult.Fail(
                "No transform recovery is pending.");
        }
        if (!ReferenceEquals(pending, recovery))
            return GestureResult.Fail(
                "The supplied recovery receipt is stale; use the current " +
                "pending recovery.") with
            {
                Recovery = pending,
            };
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        if (_active != null)
            return GestureResult.Fail(
                "Cancel the active gesture before retrying recovery.") with
            {
                Recovery = pending,
            };

        var retried = AttemptRecovery(
            recovery.Attempts.Select(attempt => attempt.RequestedState).ToArray(),
            _recoveryCompleted, _recoveryRemap);
        return RecoveryResult(retried);
    }

    internal TransformRecoveryReceipt AttemptRecovery(
        IReadOnlyList<TransformTargetState> states,
        Action? completed = null,
        bool remap = false)
    {
        var requested = remap && _groupSource != null
            ? states.Select(state => _groupSource.CurrentTarget(state.Target) is { } current
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

    public GestureResult? RecoverPending() =>
        PendingRecovery is { } pending ? RetryRecovery(pending) : null;

    private GestureResult RestorePatch(TransformPatch patch, bool before, Action commit)
    {
        var recovery = AttemptRecovery(before ? patch.Before : patch.After, () =>
        {
            if (patch.GroupState is { } change)
            {
                var current = _groupSource == null ? change : change.Remap(_groupSource.CurrentTarget);
                if (current != null)
                    _groupTransforms?.Restore(current, before);
            }
            commit();
        }, remap: patch.GroupState != null);
        return RecoveryResult(recovery);
    }

    public GestureResult CompleteSnapshotRestore(HistoryEntry entry, bool before, Action commit)
    {
        if (entry is TransformPatch { GroupState: not null } patch)
        {
            using var transition = TryEnterTransition();
            return transition == null ? Busy() : RestorePatch(patch, before, commit);
        }
        commit();
        return GestureResult.Ok();
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
        using var transition = TryEnterTransition();
        if (transition == null)
            return false;
        var retried = AttemptRecovery(
            pending.Attempts.Select(attempt => attempt.RequestedState).ToArray(),
            _recoveryCompleted, _recoveryRemap);
        return retried.Complete;
    }

    /// <summary>
    /// Restores an operation-owned exact snapshot through the same ordered
    /// recovery barrier used by gesture cancellation. This is intentionally
    /// the only public recovery entry point for another application workflow:
    /// incomplete evidence remains the identity-bound <see
    /// cref="PendingRecovery"/> token and must be retried through
    /// <see cref="RetryRecovery"/>.
    /// </summary>
    public GestureResult RestoreForOperation(
        IReadOnlyList<TransformTargetState> states)
    {
        if (!TryCompleteRecovery() && PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        var recovery = AttemptRecovery(states);
        return RecoveryResult(recovery);
    }

    internal GestureResult? RecoveryBarrier() =>
        TryCompleteRecovery()
            ? null
            : RecoveryRequired(PendingRecovery!);

    internal IDisposable? TryEnterTransition()
    {
        if (_transitionActive)
            return null;
        _transitionActive = true;
        return new TransitionScope(this);
    }

    internal GestureResult Busy() =>
        GestureResult.Fail(
            "A transform application transition is busy.") with
        {
            Recovery = PendingRecovery,
        };

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

    private TransformRecoveryReceipt RecoverGesture(ActiveGestureState active) =>
        AttemptRecovery(active.Before, () =>
        {
            if (active.GroupBefore is not { } frozen || _groupTransforms == null)
                return;
            var restored = _groupSource == null ? frozen : frozen.Remap(_groupSource.CurrentTarget);
            if (restored != null)
                _groupTransforms.Put(GroupTransformKey.For(active.Command.GroupId,
                    restored.Expected.Keys), restored);
        }, remap: active.GroupBefore != null);

    /// <summary>
    /// A thrown port call is mutation-unknown, exactly like an explicit
    /// failure: every supplied frozen baseline is attempted in order, the
    /// gesture disarms so stale baselines never feed a later tick, and
    /// incomplete recovery arms the <see cref="PendingRecovery"/> barrier.
    /// </summary>
    private GestureResult ThrownFailureAfterRecovery(
        string operation,
        Exception exception,
        IReadOnlyList<TransformTargetState> baselines)
    {
        var recovery = _active is { } active ? RecoverGesture(active) : AttemptRecovery(baselines);
        _active = null;
        return FailureAfterRecovery(
            $"{operation}: {exception.Message}",
            recovery);
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
        // Bones stay alone and per-skeleton: their pivot math lives in
        // model space. Scene ENTITIES mix freely — the anonymous group —
        // because every entity target captures and applies in world space
        // through its own port.
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
        using var transition = TryEnterTransition();
        if (transition == null)
            return;
        if (_active is { } active)
            CancelActive(active);
    }

    private sealed class TransitionScope(
        TransformGestureService owner) : IDisposable
    {
        public void Dispose() => owner._transitionActive = false;
    }

    private sealed record ActiveGestureState(
        TransformGestureId Id,
        ulong SceneRevision,
        BeginTransformGesture Command,
        Vector3 Pivot,
        IReadOnlyList<TransformTargetState> Before,
        GroupTransformSnapshot? GroupBefore = null,
        JournalContexts.StepScope? Journal = null,
        GroupTransformSnapshot? GroupProposed = null);
}
