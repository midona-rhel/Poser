using System.Numerics;
using Poser.Application.Operations;
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

    /// <summary>Additive evidence, excluded from legacy positional equality.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>Additive operation evidence, excluded from legacy positional
    /// equality, hashing, and deconstruction.</summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(GestureResult other) =>
        Success == other.Success &&
        Detail == other.Detail &&
        GestureId == other.GestureId;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Detail, GestureId);
}

/// <summary>
/// Idempotent transform gesture, recovery, and patch-history coordinator.
/// Public transitions run serialized on the Application/framework thread.
/// The narrow guard rejects synchronous port-callback reentry; it is not a
/// lock, scheduler, or cross-thread coordinator.
/// </summary>
public sealed class TransformGestureService : IDisposable
{
    private readonly SceneSession _scene;
    private readonly ITransformRuntimePort _runtime;
    private ActiveGestureState? _active;
    private bool _transitionActive;

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

    /// <summary>
    /// An incomplete exact-state restore. While present it blocks every new
    /// transform/pose mutation so partial state cannot become a new baseline.
    /// </summary>
    public TransformRecoveryReceipt? PendingRecovery { get; private set; }

    public GestureResult Begin(BeginTransformGesture command)
    {
        if (PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
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
        if (PendingRecovery is { } pending)
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
            var rollback = AttemptRecovery(active.Before);
            _active = null;
            var applyDetail =
                result.Detail ??
                $"Runtime rejected target {active.Before[index].Target}.";
            return FailureAfterRecovery(applyDetail, rollback);
        }

        return GestureResult.Ok(gestureId);
    }

    public GestureResult Commit(TransformGestureId gestureId)
    {
        if (PendingRecovery is { } pending)
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
                var rollback = AttemptRecovery(active.Before);
                _active = null;
                return FailureAfterRecovery(
                    result.Detail ??
                    $"Could not capture final state for {before.Target}.",
                    rollback);
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
        if (PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        if (_active is not { } active || active.Id != gestureId)
            return GestureResult.Fail("Transform gesture is not active.");
        return CancelActive(active);
    }

    /// <summary>
    /// Reconciles the active gesture against a completed structural scene
    /// refresh. When every gesture target remains current at its exact
    /// generation, the gesture SURVIVES and accepts the new revision — an
    /// unrelated actor or slot appearing, vanishing, or changing does not
    /// end a drag. When any target is stale it cancels once: every
    /// frozen baseline is attempted in order, incomplete recovery is retained
    /// as a mutation barrier, and no history entry is created.
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
        if (PendingRecovery is { } pending)
            return RecoveryRequired(pending);
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
        var patch = (TransformPatch)entry;
        var recovery = AttemptRecovery(patch.Before);
        if (recovery.Complete)
            History.CommitUndo(patch);
        return RecoveryResult(recovery);
    }

    /// <summary>
    /// Runs one direction of a lifecycle entry. The entry moves stacks ONLY
    /// when the act it names actually landed — the same rule the transform
    /// path applies to its restore receipt: a spawn the game refused leaves
    /// the entry exactly where it was, still undoable, rather than quietly
    /// consuming a step of the user's history.
    /// </summary>
    private static GestureResult RunLifecycle(
        Func<bool> act, string failure, Action commit)
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
            return GestureResult.Fail(failure);
        commit();
        return GestureResult.Ok();
    }

    public GestureResult Redo()
    {
        if (PendingRecovery is { } pending)
            return RecoveryRequired(pending);
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
        var patch = (TransformPatch)entry;
        var recovery = AttemptRecovery(patch.After);
        if (recovery.Complete)
            History.CommitRedo(patch);
        return RecoveryResult(recovery);
    }

    public void Dispose()
    {
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

        var retried = TransformRecovery.RestoreAll(
            _runtime,
            recovery.Attempts.Select(attempt => attempt.RequestedState));
        if (retried.Complete)
            PendingRecovery = null;
        else
            PendingRecovery = retried;
        return RecoveryResult(retried);
    }

    internal TransformRecoveryReceipt AttemptRecovery(
        IReadOnlyList<TransformTargetState> states)
    {
        var recovery = TransformRecovery.RestoreAll(_runtime, states);
        if (!recovery.Complete)
            PendingRecovery = recovery;
        return recovery;
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
        if (PendingRecovery is { } pending)
            return RecoveryRequired(pending);
        using var transition = TryEnterTransition();
        if (transition == null)
            return Busy();
        var recovery = AttemptRecovery(states);
        return RecoveryResult(recovery);
    }

    internal GestureResult? RecoveryBarrier() =>
        PendingRecovery is { } pending
            ? RecoveryRequired(pending)
            : null;

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
        var recovery = AttemptRecovery(active.Before);
        _active = null;
        return RecoveryResult(recovery);
    }

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
        var recovery = AttemptRecovery(baselines);
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
        IReadOnlyList<TransformTargetState> Before);
}
