using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Domain.Operations;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;
using Poser.Domain.Actors;
using Poser.Game.Entities;
using Poser.Game.Services;

using static Poser.Game.Posing.PoseImportPlanner;

namespace Poser.Game.Posing;

/// <summary>
/// Applies a <see cref="PoseImportPlan"/> INSIDE the apply pass — Brio's
/// interactive pose-file import (SkeletonPosingCapability.ImportSkeletonPose →
/// PoseImporter.ApplyBone, Game/Posing/PoseImporter.cs:9-87), on the same
/// transitive-action lifecycle as <see cref="IkBakeCapture"/>.
///
/// The mechanism is the point: each write's delta is diffed against
/// <c>bone.LastRawTransform</c> exactly as the pass has just refreshed it —
/// after the synchronous reset cleared the scope's stacks, and after every
/// parent already written in this same pass has moved this bone. A basis read
/// on any earlier tick (the replaced ImportEdit path) predates those parent
/// deltas, so children double-move under propagation. Partial-component
/// imports mask the DELTA (Brio PoseImporter.cs:35 → PoseInfo.Apply's applyTo,
/// PoseInfo.cs:108 calc.Filter), so an excluded component contributes nothing
/// instead of pinning the bone to a stale absolute.
///
/// One pass is not the whole story for faces. File data is exported from
/// <c>bone.LastRawTransform</c> AFTER the update phase's post-reparent
/// refresh (PoseFileService.cs:74, BonePosingService STEP 4; Brio
/// SkeletonService.cs:243), while the pass's mid-pass basis is PRE-reparent;
/// for bones of a non-zero partial the two spaces differ by the head's
/// posed-vs-animated delta, so the first diff lands the face wrong in both
/// tools. Brio converges by scheduling Snapshot at +4 ticks after its import
/// (PosingCapability.cs:249-250), which runs ReconcileHead (:316-317,
/// :323-352) into ReconcileChildren("j_kao", false) (:370-401): re-export
/// the j_kao subtree from the now POST-reparent LastRawTransform (:385) and
/// re-import it in-pass with TransformComponents.All (:380). This engine
/// ports that as a second one-shot transitive batch between the apply pass
/// and completion; the single history entry covers the CONVERGED state.
/// </summary>
public sealed class PoseImportCapture : IDisposable
{
    /// <summary>Framework ticks a registered batch is given to reach a pass
    /// before the import gives up and rolls back — same guard as
    /// <see cref="IkBakeCapture"/>: a skeleton that stops updating must not
    /// leave the import pending forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    /// <summary>Brio schedules its post-import Snapshot — the reconcile's
    /// driver — at +4 ticks (PosingCapability.cs:249-250). Poser counts from
    /// the apply batch's outcome instead of from registration; same spirit:
    /// the post-reparent refresh has settled before the subtree re-export.
    /// The single <see cref="CompletionTimeoutTicks"/> armed at Begin spans
    /// both phases.</summary>
    private const int ReconcileDelayTicks = 4;

    /// <summary>Brio's Reconcile() runs its export-reset-reimport at +2
    /// ticks (PosingCapability.cs:419,428).</summary>
    private const int FlattenDelayTicks = 2;

    private readonly IFramework _framework;
    private readonly SceneSession _scene;
    private readonly ISessionGenerationSource _sessions;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformGestureService _gestures;
    private readonly IkBakeCapture _ikBake;
    private readonly ISkeletonService _skeletons;
    private readonly IPluginLog _log;
    private readonly PoseImportPlanner _planner;
    private readonly PoseImportTerminal _terminal;

    private PendingPoseImport? _pending;
    private long _generation;
    private OperationEpoch? _lastOperationEpoch;
    private int _disposed;

    /// <summary>Raised once for each accepted operation's terminal receipt.
    /// Delivery is framework-thread-only and synchronous refusals publish
    /// nothing.</summary>
    public event Action<OperationReceipt>? ReceiptPublished
    {
        add => _terminal.ReceiptPublished += value;
        remove => _terminal.ReceiptPublished -= value;
    }

    public PoseImportCapture(
        IFramework framework,
        SceneSession scene,
        ISessionGenerationSource sessions,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ITransformRuntimePort runtime,
        EditHistory history,
        TransformGestureService gestures,
        IkBakeCapture ikBake,
        IPoseFileService poseFiles,
        ISkeletonService skeletons,
        IPluginLog log)
    {
        _framework = framework;
        _scene = scene;
        _sessions = sessions;
        _bindings = bindings;
        _posing = posing;
        _runtime = runtime;
        _gestures = gestures;
        _ikBake = ikBake;
        _skeletons = skeletons;
        _log = log;
        _planner = new PoseImportPlanner(bindings, posing, runtime, poseFiles, skeletons, log);
        _terminal = new PoseImportTerminal(history, gestures, runtime, log);
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
        _scene.SceneChanged += OnSceneChanged;
    }

    /// <summary>Whether an import is armed and has not finished: true from
    /// registration until the pass has executed the actions AND the history
    /// entry has been appended (or the whole edit rolled back).</summary>
    public bool IsPending => Volatile.Read(ref _pending) != null;

    /// <summary>The transient refusals of <see cref="Reserve"/>: a caller that
    /// waits these out instead of spending its attempt gets admitted.</summary>
    public bool AdmissionBusy =>
        IsPending || _ikBake.IsPending || _gestures.ActiveGesture != null;

    public bool IsCurrent(PoseImportOperation operation) =>
        Volatile.Read(ref _pending) is { } import &&
        ReferenceEquals(import.Operation, operation) &&
        !import.Invalidation.IsInvalidated;

    /// <summary>Framework-thread supersession path. The active operation is
    /// invalidated before its ordered restore, and publishes exactly one
    /// Cancelled (or RecoveryRequired) terminal receipt.</summary>
    public GestureResult CancelActive(string detail = "Pose import superseded.")
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail(
                "Pose import cancellation must run on the framework thread.");
        if (_pending is not { } import)
            return GestureResult.Fail("No pose import is active.");

        Invalidate(import);
        var terminal = _terminal.CreateFailureTerminal(import, detail, cancelled: true);
        _terminal.Notify(import, terminal);
        return GestureResult.Fail(detail) with
        {
            Recovery = terminal.Recovery,
            OperationReceipt = terminal,
        };
    }

    /// <summary>
    /// Arms one plan, on one tick: capture every affected target, apply the
    /// reset scope and the model transform synchronously, register the
    /// per-bone file writes for the next pass. Ok means the import is armed
    /// and its actions are queued — an in-pass failure rolls the whole edit
    /// back and logs, it does not reach this return value.
    /// </summary>
    /// <summary>Reserve-and-begin in one call. The plan is name-keyed and
    /// names no actor, so the target is stated explicitly.</summary>
    public GestureResult Begin(
        IActor actor,
        PoseImportPlan plan,
        string description,
        Action<bool>? onFinished = null,
        bool expression = false,
        Action<OperationReceipt>? onReceipt = null)
    {
        var reserved = Reserve(actor, description, out var operation, onFinished, onReceipt);
        if (!reserved.Success || reserved.OperationReceipt is not { } pending)
            return reserved;
        return Begin(
            operation!,
            plan,
            expression);
    }

    public GestureResult Reserve(
        IActor actor,
        string description,
        out PoseImportOperation? operation,
        Action<bool>? onFinished = null,
        Action<OperationReceipt>? onReceipt = null)
    {
        operation = null;
        if (Volatile.Read(ref _disposed) != 0)
            return GestureResult.Fail("Pose import is disposed.");
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("Pose import must run on the framework thread.");
        if (Volatile.Read(ref _pending) != null)
            return GestureResult.Fail("A pose import is already applying.");
        if (_ikBake.IsPending)
            return GestureResult.Fail("An IK bake is still applying.");
        if (!_gestures.TryCompleteRecovery() &&
            _gestures.PendingRecovery is { } pendingRecovery)
            return GestureResult.Fail(
                "Transform recovery must complete before another mutation.") with
            {
                Recovery = pendingRecovery,
            };
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (_bindings.GetActorId(actor) is not { } actorId ||
            _bindings.Resolve(actorId) is not { Success: true, Value: { } resolved } ||
            !ReferenceEquals(resolved, actor))
            return GestureResult.Fail("The import actor could not be resolved exactly.");
        if (_sessions.ActiveSessionGeneration is not { IsValid: true } sessionGeneration)
            return GestureResult.Fail("Pose import requires an active session.");

        var operationEpoch = _lastOperationEpoch is { } last
            ? last.Next()
            : OperationEpoch.First;
        var operationId = Guid.NewGuid();
        var pending = OperationReceipt.Pending(
            operationId,
            operationEpoch,
            sessionGeneration,
            actorId,
            description);
        operation = new PoseImportOperation(pending);
        var import = new PendingPoseImport
        {
            Generation = ++_generation,
            OperationId = operationId,
            OperationEpoch = operationEpoch,
            SessionGeneration = sessionGeneration,
            TargetActorId = actorId,
            ActorKey = actor.Id.Unique,
            Operation = operation,
            Targets = Array.Empty<TransformTargetId>(),
            Description = description,
            Slots = new List<PoseImportSlot>(),
            Order = new List<TransformTargetId>(),
            Before = new Dictionary<TransformTargetId, TransformTargetState>(),
            Resets = new HashSet<TransformTargetId>(),
            OnFinished = onFinished,
            OnReceipt = onReceipt,
            PendingReceipt = pending,
            PreviewTarget = actor.ActorKind == ActorKind.Preview,
        };
        _lastOperationEpoch = operationEpoch;
        Volatile.Write(ref _pending, import);
        return GestureResult.Ok() with { OperationReceipt = pending };
    }


    public GestureResult Begin(
        PoseImportOperation operation,
        PoseImportPlan plan,
        bool expression = false,
        bool suppressHistory = false,
        string? asset = null)
    {
        if (Volatile.Read(ref _pending) is not { } import ||
            !ReferenceEquals(import.Operation, operation) ||
            import.Invalidation.IsInvalidated)
            return GestureResult.Fail("The pose import arm is stale.");
        import.Expression = expression;
        import.SuppressHistory = suppressHistory;
        import.Asset = asset;
        if (plan.IsEmpty)
            return FailAdmitted(import, "Nothing in this file applies to the chosen scope.");

        // The target is the ADMITTED identity: the exact actor generation
        // Reserve pinned, resolved through the binding registry at this
        // moment. A wrapper object replaced for the same logical actor and
        // generation IS the same actor; only a bumped generation refuses.
        if (_bindings.Resolve(import.TargetActorId) is not
            { Success: true, Value: { } actor })
            return FailAdmitted(import, "The import actor was replaced before application.");
        // Resolve and capture EVERYTHING before mutating anything, so a
        // stale target fails synchronously with nothing to roll back.
        if (_planner.ResolveApply(import, actor, plan, expression,
                out var resetBones, out var skeletons, out var model) is { } refusal)
            return FailAdmitted(import, refusal);

        try
        {
            // Pending ownership was established by Reserve before this first
            // mutation. Every setup step shares the same exception-safe
            // transaction and therefore the same terminal receipt.
            _posing.SetIkImportSuppressed(import.ActorKey, true);
            foreach (var (bone, _) in resetBones)
            {
                import.MutationStarted = true;
                _posing.GetPoseInfo(bone.Skeleton)
                    .GetPoseInfo(bone.BoneName, bone.PartialId)
                    .ResetForImport(_posing.GetIkConfiguration(bone) is
                        { Enabled: true });
            }

            if (model is { } modelEdit &&
                !ApproximatelySame(
                    import.Before[modelEdit.Target].Transform,
                    modelEdit.Desired))
            {
                import.MutationStarted = true;
                var applied = _runtime.ApplyAbsolute(
                    import.Before[modelEdit.Target], modelEdit.Desired);
                if (!applied.Success)
                    return FailAdmitted(
                        import,
                        applied.Detail ?? "Could not apply the model transform.");
                import.Written.Add(modelEdit.Target);
            }

            if (import.Slots.Count == 0)
            {
                _framework.RunOnTick(() => Complete(import.Generation));
                return GestureResult.Ok() with
                {
                    OperationReceipt = import.PendingReceipt,
                };
            }

            foreach (var slot in import.Slots)
            {
                var scope = slot;
                // The registration takes the live skeleton resolved on THIS
                // tick, but the batch itself is keyed by (actor, slot) in
                // the posing service and the callback matches bones by
                // name — nothing pins this instance past the call.
                _posing.RegisterTransitiveAction(
                    skeletons[scope.Slot],
                    (bone, poseInfo) => _planner.ApplyBone(import, scope, bone, poseInfo));
            }

            _framework.RunOnTick(
                () => OnTimeout(import.Generation),
                delayTicks: CompletionTimeoutTicks);
            return GestureResult.Ok() with
            {
                OperationReceipt = import.PendingReceipt,
            };
        }
        catch (Exception exception)
        {
            return FailAdmitted(
                import,
                $"Pose import setup failed: {exception.Message}");
        }
    }

    /// <summary>Raised from the native hooks when the interval that owned a
    /// batch ends. Records only — the completion itself needs the framework
    /// thread.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        // Native callbacks are record-only. In particular, do not read
        // SceneSession, StableBindingRegistry, or session state here.
        if (Volatile.Read(ref _pending) is not { } import || !PoseImportPlanner.IsNativeLive(import))
            return;
        var complete = true;
        var known = false;
        foreach (var slot in import.Slots)
        {
            // The outcome's skeleton is only READ here, for its stable keys
            // — the batch identity is (actor, slot), same as the posing
            // service's own, so a mid-window skeleton replacement still
            // reports against the right batch.
            if (!slot.Ended &&
                outcome.Skeleton.Slot == slot.Slot &&
                string.Equals(
                    outcome.Skeleton.Actor.Id.Unique,
                    import.ActorKey,
                    StringComparison.Ordinal))
            {
                slot.Ended = true;
                slot.Executed = outcome.Executed;
                known = true;
            }
            if (!slot.Ended)
                complete = false;
        }
        if (!known || !complete || import.Completing)
            return;
        import.Completing = true;
        switch (import.Stage)
        {
            case PoseImportStage.Apply:
                // The apply batches have run. An expression import restores
                // the head first — Brio schedules its phase 2 at +4 ticks
                // (PosingCapability.cs:249-250, the same delay its reconcile
                // uses); everything else goes straight to the reconcile
                // decision at the same delay.
                QueueFromNative(
                    import,
                    import.HeadRestores is { Count: > 0 }
                        ? () => BeginHeadRestore(import.Generation)
                        : () => BeginReconcile(import.Generation),
                    ReconcileDelayTicks);
                break;
            case PoseImportStage.HeadRestore:
                // Brio's phase 2 runs with generateSnapshot: true, so its
                // Snapshot — the reconcile driver — fires another 4 ticks
                // after the restore pass (PosingCapability.cs:308-309,
                // :249-250).
                QueueFromNative(
                    import,
                    () => BeginReconcile(import.Generation),
                    ReconcileDelayTicks);
                break;
            case PoseImportStage.Reconcile:
                QueueFromNative(
                    import,
                    () => FinishAfterReconcile(import.Generation));
                break;
            default:
                QueueFromNative(import, () => Complete(import.Generation));
                break;
        }
    }

    private void QueueFromNative(PendingPoseImport import, Action action, int delayTicks = 0)
    {
        try
        {
            _framework.RunOnTick(action, delayTicks: delayTicks);
        }
        catch (Exception ex)
        {
            // Native hooks may only record failure. The already-armed timeout
            // performs the framework-thread rollback and terminal publication.
            import.Failure ??= $"Pose import phase scheduling failed: {ex.Message}";
        }
    }

    private bool IsLive(PendingPoseImport import) =>
        Volatile.Read(ref _disposed) == 0 &&
        !import.Invalidated &&
        !import.Invalidation.IsInvalidated &&
        ReferenceEquals(Volatile.Read(ref _pending), import);

    /// <summary>Framework-thread identity gate for every deferred phase.
    /// Identity is the session, the admitted actor GENERATION, and — for
    /// ordinary imports — exact scene membership of every captured target.
    /// No instance is pinned: the registry's own ReferenceEquals staleness
    /// check governs what its ids resolve to (issue #78), and a wrapper or
    /// skeleton object replaced for the same identity changes nothing here.
    /// </summary>
    private bool IsFrameworkCurrent(PendingPoseImport import)
    {
        if (!IsLive(import) ||
            _sessions.ActiveSessionGeneration is not { } currentSession ||
            currentSession != import.SessionGeneration ||
            _bindings.Resolve(import.TargetActorId) is not { Success: true })
            return false;

        foreach (var target in import.Targets)
        {
            if (ActorFor(target) != import.TargetActorId)
                return false;
            // Preview actors are auxiliary and intentionally absent from the
            // committed scene model; ordinary imports require exact scene
            // admission of every target generation.
            if (!import.PreviewTarget && !_scene.Contains(target))
                return false;
        }
        return true;
    }

    private bool GuardFramework(PendingPoseImport import, string detail)
    {
        if (IsFrameworkCurrent(import))
            return true;
        if (IsLive(import))
            FailAndPublish(import, detail);
        return false;
    }

    private void FailAndPublish(PendingPoseImport import, string detail)
    {
        if (!IsLive(import))
            return;
        import.Failure ??= detail;
        Invalidate(import);
        var terminal = _terminal.CreateFailureTerminal(import, detail);
        _terminal.Notify(import, terminal);
    }

    private void OnSceneChanged(SceneSnapshot _)
    {
        if (_pending is { } import)
            GuardFramework(import, "The pose import scene or target was replaced.");
    }

    /// <summary>Where the reconcile outcome goes next: a body import is
    /// done, an expression import continues into Brio's whole-pose flatten
    /// (its phase-2 Snapshot runs Reconcile(reset: true) — the phase-2
    /// ImportPose_Internal call leaves reset/reconcile at their TRUE
    /// defaults, PosingCapability.cs:308-309 vs the body path's
    /// reconcile: false at :156).</summary>
    private void FinishAfterReconcile(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import target was replaced."))
            return;
        try
        {
            if (import.Expression && import.Failure == null)
                _framework.RunOnTick(
                    () => BeginFlatten(import.Generation),
                    delayTicks: FlattenDelayTicks);
            else
                _framework.RunOnTick(() => Complete(import.Generation));
        }
        catch (Exception ex)
        {
            FailAndPublish(
                import,
                $"Pose import completion scheduling failed: {ex.Message}");
        }
    }

    private void OnTimeout(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;
        import.Failure ??= import.Stage switch
        {
            PoseImportStage.Apply => "The import never reached an apply pass.",
            PoseImportStage.HeadRestore =>
                "The head restore never reached an apply pass.",
            PoseImportStage.Flatten =>
                "The pose flatten never reached an apply pass.",
            _ => "The face reconcile never reached an apply pass.",
        };
        Complete(generation);
    }

    /// <summary>
    /// Brio's <c>Reconcile(reset: true)</c> (PosingCapability.cs:417-429),
    /// the expression chain's final stage: export the ENTIRE current pose
    /// (GeneratePoseData — per-slot post-reparent absolutes), clear every
    /// interactive stack (Reset), and re-import the export whole with every
    /// component. Whatever the head dance left behind is erased; the final
    /// state is one clean absolute re-expression of what is on screen.
    /// Brio's nested second round (Reset's own Snapshot → Reconcile(false))
    /// re-imports the same absolutes WITHOUT a reset, so every delta
    /// rejects as near-identity — one round is the entire effect. The model
    /// transform is skipped: Brio resets it and re-applies the exported
    /// difference onto the reset original, a net no-op an expression never
    /// disturbs.
    /// </summary>
    private void BeginFlatten(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;

        try
        {
        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        // The live actor is resolved on THIS tick by its admitted identity;
        // the flatten owns no instance from any earlier stage.
        if (!applied || !import.WroteCharacter ||
            _bindings.Resolve(import.TargetActorId) is not
                { Success: true, Value: { } actor })
        {
            Complete(generation);
            return;
        }

        if (_planner.BuildFlatten(import, actor,
                out var resetBones, out var flattenSlots, out var skeletons) is { } refusal)
        {
            FailAndPublish(import, refusal);
            return;
        }
        if (flattenSlots is null)
        {
            Complete(generation);
            return;
        }

        RefreshTargets(import);

        // Brio's Reset before the re-import: every interactive stack goes;
        // named service layers stay and re-drive themselves.
        foreach (var (bone, _) in resetBones)
        {
            // Mark before the reset call: a provider may mutate state before
            // reporting an exception, and rollback must remain truthful.
            import.MutationStarted = true;
            _posing.GetPoseInfo(bone.Skeleton)
                .GetPoseInfo(bone.BoneName, bone.PartialId)
                .ResetForImport(_posing.GetIkConfiguration(bone) is
                    { Enabled: true });
        }

        import.Slots = flattenSlots;
        import.Stage = PoseImportStage.Flatten;
        import.Completing = false;
        foreach (var slot in flattenSlots)
        {
            var scope = slot;
            _posing.RegisterTransitiveAction(
                skeletons[scope.Slot],
                (bone, poseInfo) => _planner.ApplyBone(import, scope, bone, poseInfo));
        }
        }
        catch (Exception exception)
        {
            FailAndPublish(import, $"Pose import flatten setup failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Brio's expressionPhase2 (PosingCapability.cs:233-247 with
    /// PoseImporter.cs:11-26), the middle stage of its expression dance: the
    /// apply stage moved the head to the FILE's head so the face landed
    /// face-local; now the head comes back. Per written j_kao instance, the
    /// phase-1 head stack pops (RemoveLastStack — exact because import
    /// writes are forceNewStack, like Brio's), and a POSITION-only restore
    /// to the pre-import absolute registers as the next batch, diffed
    /// in-pass against the post-removal basis exactly like any import
    /// write. Head rotation reverts through the pop alone; the +4-tick
    /// reconcile then re-expresses the face against the restored head.
    /// </summary>
    private void BeginHeadRestore(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;

        try
        {
        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        if (!applied || import.HeadRestores is not { Count: > 0 } restores)
        {
            Complete(generation);
            return;
        }

        // The head restore names Character-slot bones the apply stage just
        // wrote; the LIVE skeleton is resolved on this tick, not carried.
        if (_bindings.Resolve(import.TargetActorId) is not
                { Success: true, Value: { } actor } ||
            _skeletons.GetSkeleton(actor) is not { } skeleton)
        {
            FailAndPublish(import,
                "The Character skeleton is no longer present for the head restore.");
            return;
        }

        var writes = new Dictionary<(int, string),
            (TransformTargetId, Transform, TransformComponents)>(restores.Count);
        foreach (var headRestore in restores)
        {
            // Only an instance the apply stage actually wrote carries a
            // phase-1 stack to pop; the near-identity early-out means a
            // head already at the file's pose gained none. A written
            // instance's PreImport was re-expressed by that same pass in
            // its own basis (PoseImportHeadRestore.PreImport), so the write below
            // diffs two values of the SAME space and lands the head back
            // on the pre-import authored state exactly.
            if (!import.Written.Contains(headRestore.Target))
                continue;
            // The pop mutates the interactive stack before the deferred
            // restore batch is registered; mark before entering that call.
            import.MutationStarted = true;
            _posing.GetPoseInfo(skeleton)
                .GetPoseInfo(headRestore.Bone, headRestore.Partial)
                .RemoveLastInteractiveStack();
            writes[(headRestore.Partial, headRestore.Bone)] =
                (headRestore.Target, headRestore.PreImport, TransformComponents.Position);
        }

        if (writes.Count == 0)
        {
            // No instance was moved — nothing to restore, straight to the
            // reconcile decision.
            BeginReconcile(generation);
            return;
        }

        var restore = new PoseImportSlot
        {
            Slot = PoseSlot.Character,
            Writes = writes,
        };
        import.Slots = new List<PoseImportSlot> { restore };
        import.Stage = PoseImportStage.HeadRestore;
        import.Completing = false;
        _posing.RegisterTransitiveAction(
            skeleton,
            (bone, poseInfo) => _planner.ApplyBone(import, restore, bone, poseInfo));
        }
        catch (Exception exception)
        {
            FailAndPublish(import, $"Pose import head restore setup failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The reconcile decision point, on the framework thread after the apply
    /// batches ran and reparenting settled. Skips (completing the import
    /// as-is) when: the apply phase already failed or never executed; the
    /// plan wrote no face-partial bones (a body/weapon-only import's spaces
    /// agree — no second pass to burn); the actor has no j_kao (Brio
    /// ReconcileHead's null check, PosingCapability.cs:326-327); IK is armed
    /// (Brio Snapshot :316-317 runs ReconcileHead only when
    /// <c>PoseInfo.HasIKStacks</c> is false — Brio stores IK per stack,
    /// Poser per bone, so the mapped guard is
    /// <see cref="IBonePosingService.HasEnabledIk"/>); or neither j_kao nor
    /// any ancestor is overridden (:331-345 — without a posed head the
    /// pre/post-reparent spaces coincide and there is nothing to converge).
    /// Otherwise registers the subtree re-import as the second batch.
    /// </summary>
    private void BeginReconcile(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;

        try
        {
        // A failed or unexecuted apply phase completes (and rolls back) via
        // Complete's own verdict on the still-current apply slots.
        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        if (!applied)
        {
            Complete(generation);
            return;
        }

        if (_planner.BuildReconcile(import) is not { } reconcile)
        {
            // Nothing to converge — an expression import still owes the
            // flatten (Brio's Snapshot runs Reconcile(reset) whether or not
            // ReconcileHead had work).
            FinishAfterReconcile(import.Generation);
            return;
        }

        import.Slots = new List<PoseImportSlot> { reconcile.Batch };
        import.Stage = PoseImportStage.Reconcile;
        import.Completing = false;
        _posing.RegisterTransitiveAction(
            reconcile.Skeleton,
            (bone, poseInfo) => _planner.ApplyBone(import, reconcile.Batch, bone, poseInfo));
        }
        catch (Exception exception)
        {
            FailAndPublish(import, $"Pose import reconcile setup failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The framework-thread half: by now the pass has run the actions and
    /// every imported stack is in place. Capture the after-states of what
    /// actually changed and append ONE history entry — or, on any failure,
    /// restore every captured target and append nothing.
    /// </summary>
    private void Complete(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;

        var failure = import.Failure;
        if (failure == null)
        {
            foreach (var slot in import.Slots)
            {
                if (!slot.Executed)
                {
                    failure = "The apply pass never ran the import.";
                    break;
                }
            }
        }

        if (failure == null)
        {
            try
            {
                failure = _terminal.AppendHistory(import, IsFrameworkCurrent);
            }
            catch (Exception exception)
            {
                failure = $"Could not append pose import history: {exception.Message}";
            }
        }

        if (failure != null)
        {
            // Invalidate before the ordered recovery sweep. Late native,
            // timeout, and queued framework callbacks can now only no-op.
            Invalidate(import);
            _log.Warning($"Pose import failed: {failure}");
            var terminal = _terminal.CreateFailureTerminal(import, failure);
            _terminal.Notify(import, terminal);
            return;
        }

        Invalidate(import);
        _terminal.Notify(import, OperationReceipt.Applied(
            import.OperationId,
            import.OperationEpoch,
            import.SessionGeneration,
            import.TargetActorId,
            import.Description));
    }

    private GestureResult FailAdmitted(PendingPoseImport import, string detail)
    {
        if (!IsLive(import))
            return GestureResult.Fail(detail);
        import.Failure ??= detail;
        Invalidate(import);
        var terminal = _terminal.CreateFailureTerminal(import, detail);
        _terminal.Notify(import, terminal);
        return GestureResult.Fail(terminal.Detail ?? detail) with
        {
            Recovery = terminal.Recovery,
            OperationReceipt = terminal,
        };
    }

    /// <summary>Thread-safe host fallback used before session/provider teardown
    /// when framework dispatch could not run the ordinary drain.</summary>
    public void InvalidateForHostTeardown(
        string detail = "Pose import invalidated before host teardown; recovery was not attempted.")
    {
        if (Volatile.Read(ref _pending) is not { } import ||
            import.Invalidation.IsInvalidated)
            return;
        Invalidate(import);
        _terminal.Notify(import, OperationReceipt.Failed(
            import.OperationId,
            import.OperationEpoch,
            import.SessionGeneration,
            import.TargetActorId,
            detail));
    }

    private void Invalidate(PendingPoseImport import)
    {
        _posing.SetIkImportSuppressed(import.ActorKey, false);
        // This interlocked token is the native callback's only liveness read.
        // Set it before session/binding/provider teardown can begin.
        import.Invalidation.Invalidate();
        import.Invalidated = true;
        if (ReferenceEquals(Volatile.Read(ref _pending), import))
            Volatile.Write(ref _pending, null);
    }

    /// <summary>A pending import never outlives the session: its registered
    /// actions die with the posing interval and the completion it was
    /// waiting for is dropped.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (Volatile.Read(ref _pending) is { } import)
        {
            // Direct disposal has no return channel, so it publishes the
            // truthful terminal before releasing callbacks. Framework-thread
            // disposal can drain; any other thread invalidates first and
            // reports that recovery was not attempted.
            Invalidate(import);
            var terminal = _framework.IsInFrameworkUpdateThread
                ? _terminal.CreateFailureTerminal(import, "Pose import disposed.", cancelled: true)
                : OperationReceipt.Failed(
                    import.OperationId,
                    import.OperationEpoch,
                    import.SessionGeneration,
                    import.TargetActorId,
                    "Pose import invalidated during off-thread disposal; recovery was not attempted.");
            _terminal.Notify(import, terminal);
        }
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _scene.SceneChanged -= OnSceneChanged;
        Volatile.Write(ref _pending, null);
    }
}
