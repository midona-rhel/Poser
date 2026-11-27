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

/// <summary>Writes a pose file onto an actor during the game's bone update, measuring
/// each bone after its parents have changed. A face import can also put the head back,
/// line the face up with the head, and merge the result into one edit before the undo
/// step is recorded. If anything fails, the import is undone, and the result says if
/// the undo did not fully work.</summary>
public sealed class PoseImportCapture : IDisposable
{
    /// <summary>How many frames a queued batch of bone writes gets to run before
    /// the import gives up and is undone, the same limit as
    /// <see cref="IkBakeCapture"/>. A skeleton that stops updating must not leave
    /// the import waiting forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    /// <summary>How many frames to wait before lining the face up with the head.
    /// Brio waits 4 frames before the same step. Poser counts from when the bone
    /// writes ran, not from when they were queued, so the bones have settled
    /// before the face bones are read again. The one
    /// <see cref="CompletionTimeoutTicks"/> limit set at the start covers both
    /// steps.</summary>
    private const int ReconcileDelayTicks = 4;

    /// <summary>How many frames to wait before the last step, which reads the
    /// pose, clears it and imports it again. Brio waits the same.</summary>
    private const int FlattenDelayTicks = 2;
    /// <summary>Corrections for bones the game moves on its own, such as an
    /// eyelid that follows the eye: at most this many rounds (a lid and the bones
    /// under it), each on a later frame, so a child is measured after its parent
    /// is corrected.</summary>
    private const int MaxDrivenRounds = 4;
    private const int DrivenRoundTicks = 2;

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

    /// <summary>Raised once with each accepted import's final result. Normally
    /// this happens on the game's main thread. When Poser shuts down, or this is
    /// disposed from another thread, it happens on that thread.</summary>
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

    /// <summary>Whether an import is still waiting or applying.</summary>
    public bool IsPending => Volatile.Read(ref _pending) != null;

    /// <summary>True while another import or an IK bake is applying. A scene
    /// load waits for this to clear before importing its next actor's pose.</summary>
    public bool AdmissionBusy => IsPending || _ikBake.IsPending;

    public bool IsCurrent(PoseImportOperation operation) =>
        Volatile.Read(ref _pending) is { } import &&
        ReferenceEquals(import.Operation, operation) &&
        !import.Invalidation.IsInvalidated;

    /// <summary>Cancels the running import, for example when another one
    /// replaces it. Runs on the game's main thread. The import is marked dead
    /// before its changes are undone in order, and it publishes exactly one final
    /// result: Cancelled, or RecoveryRequired if the undo did not fully work.</summary>
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
        bool suppressHistory = false)
    {
        if (Volatile.Read(ref _pending) is not { } import ||
            !ReferenceEquals(import.Operation, operation) ||
            import.Invalidation.IsInvalidated)
            return GestureResult.Fail("The pose import arm is stale.");
        import.Expression = expression;
        import.SuppressHistory = suppressHistory;
        if (plan.IsEmpty)
            return FailAdmitted(import, "Nothing in this file applies to the chosen scope.");

        // The target is the actor Reserve recorded, looked up again now. A new
        // wrapper object for the same actor is still the same actor. Only an
        // actor the game has rebuilt since then is turned down.
        if (_bindings.Resolve(import.TargetActorId) is not
            { Success: true, Value: { } actor })
            return FailAdmitted(import, "The import actor was replaced before application.");
        // Look up and read everything before changing anything, so a target
        // that has gone fails right away with nothing to undo.
        if (_planner.ResolveApply(import, actor, plan, expression,
                out var resetBones, out var skeletons, out var model) is { } refusal)
            return FailAdmitted(import, refusal);

        try
        {
            // Reserve claimed the import before this first change. Every setup
            // step runs inside this one try block, so any failure ends in the
            // same final result.
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
                // The skeleton is the one looked up this frame, but the posing
                // service files the batch by actor and slot, and the callback
                // finds bones by name. So nothing here needs this skeleton
                // object to last.
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

    /// <summary>Called from inside the game's bone update when the update that
    /// ran a batch ends. It only records what happened; the next step is queued
    /// for Poser's normal per-frame update.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        // Code called from the game's bone update only records. In particular,
        // do not read SceneSession, StableBindingRegistry or the session here.
        if (Volatile.Read(ref _pending) is not { } import || !PoseImportPlanner.IsNativeLive(import))
            return;
        var complete = true;
        var known = false;
        foreach (var slot in import.Slots)
        {
            // The skeleton is only read here, for its actor and slot. A batch
            // is filed by actor and slot, as in the posing service, so a
            // skeleton replaced partway through still reports to the right
            // batch.
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
                // The bone writes have run. An expression import puts the head
                // back first; every other import goes straight to lining up the
                // face. Both wait 4 frames, as Brio does.
                QueueFromNative(
                    import,
                    import.HeadRestores is { Count: > 0 }
                        ? () => BeginHeadRestore(import.Generation)
                        : () => BeginReconcile(import.Generation),
                    ReconcileDelayTicks);
                break;
            case PoseImportStage.HeadRestore:
                // Brio lines up the face another 4 frames after putting the
                // head back. Poser does the same.
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
            // Code called from the game's bone update may only record a failure.
            // The time limit set earlier undoes the import and publishes the result.
            import.Failure ??= $"Pose import phase scheduling failed: {ex.Message}";
        }
    }

    private bool IsLive(PendingPoseImport import) =>
        Volatile.Read(ref _disposed) == 0 &&
        !import.Invalidated &&
        !import.Invalidation.IsInvalidated &&
        ReferenceEquals(Volatile.Read(ref _pending), import);

    /// <summary>Checked on the game's main thread before every later step: is
    /// this still the same GPose session, the same actor (not one the game has
    /// rebuilt), and, for normal imports, is every bone target still in the
    /// scene? No object is held on to. The binding registry decides what each id
    /// points at now, so a replaced wrapper or skeleton object for the same actor
    /// changes nothing here.
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
            // Preview actors are helpers and are deliberately left out of the
            // scene. A normal import needs every target to be in the scene.
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

    /// <summary>What comes after lining up the face. A body import is done. An
    /// expression import goes on to the last step, which merges the whole pose
    /// into one edit. Brio also does that last step for expression imports
    /// only.</summary>
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
        // The bone writes ran; only the corrections for bones the game moves
        // itself are still going. Finish with what they reached rather than
        // undo the import.
        if (import.DrivenRounds > 0 && import.Failure == null)
        {
            import.DrivenRounds = MaxDrivenRounds;
            Complete(generation);
            return;
        }
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

    /// <summary>Reads the current pose, clears the edit layers made by hand within
    /// the import's scope, and queues the pose to be imported again as one edit.
    /// Layers that other features keep under their own name stay. Brio ends an
    /// expression import the same way.</summary>
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
        // The actor is looked up again this frame by the id the import started
        // with; this step keeps no object from earlier steps.
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

        // Clear the pose before importing it again, as Brio does: every layer
        // made by hand goes; layers other features keep stay and reapply
        // themselves.
        foreach (var (bone, _) in resetBones)
        {
            // Mark the change before the reset: the reset can change a bone and
            // then throw, and the undo must know a change was made.
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
    /// The middle step of an expression import, as in Brio. The first step moved
    /// the head to the head in the file, so the face landed right relative to the
    /// head. Now the head goes back. For each head bone (j_kao) the first step
    /// wrote, the first step's edit layer is removed. That is exact, because every
    /// import write starts a new layer, as in Brio. Then the head's position from
    /// before the import is queued as the next batch, measured like any import
    /// write. The head's rotation goes back by removing the layer alone. Lining up
    /// the face, 4 frames later, then fits the face to the restored head.
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

        // The head restore names body bones the first step just wrote. The
        // skeleton is looked up again this frame, not carried over.
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
            // Only a head bone the first step actually wrote has a layer to
            // remove. A head already at the file's pose was skipped, so it has
            // none. For a written head bone, the first step stored its value from
            // before the import in that bone's own terms. So the write below
            // compares two values measured the same way, and puts the head back
            // exactly where it was.
            if (!import.Written.Contains(headRestore.Target))
                continue;
            // Removing the layer changes the bone before the restore batch is
            // queued, so mark the change first.
            import.MutationStarted = true;
            _posing.GetPoseInfo(skeleton)
                .GetPoseInfo(headRestore.Bone, headRestore.Partial)
                .RemoveLastInteractiveStack();
            writes[(headRestore.Partial, headRestore.Bone)] =
                (headRestore.Target, headRestore.PreImport, TransformComponents.Position);
        }

        if (writes.Count == 0)
        {
            // No head bone was moved, so there is nothing to put back. Go
            // straight to lining up the face.
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
    /// Decides whether to line the face up with the head. Runs on the game's main
    /// thread after the bone writes ran and the bones settled. It skips this, and
    /// finishes the import as it is, when: the bone writes failed or never ran;
    /// the import wrote no face bones (a body or weapon import needs no second
    /// step); the actor has no head bone j_kao (Brio checks the same); IK is on
    /// (Brio only lines up the face without IK; Brio keeps IK per edit layer and
    /// Poser per bone, so Poser checks
    /// <see cref="IBonePosingService.HasEnabledIk"/>); or neither j_kao nor any
    /// bone above it is posed (then the face is already in the right place).
    /// Otherwise it queues the face bones to be imported again, as a second batch.
    /// </summary>
    private void BeginReconcile(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        if (!GuardFramework(import, "The pose import session or target was replaced."))
            return;

        try
        {
        // Bone writes that failed or never ran are finished, and undone, by
        // Complete, which checks the same batches.
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
            // The face needs no lining up. An expression import still needs
            // its last merge step; Brio runs it whether or not the face needed
            // lining up.
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

    /// <summary>Checks that the batches ran, corrects bones the game moves on its
    /// own, and records the undo step. On failure it tries to undo the import and
    /// publishes how far that got.</summary>
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

        if (failure == null && import.DrivenRounds < MaxDrivenRounds)
        {
            int corrected = 0;
            try
            {
                corrected = CorrectDrivenBones(import);
            }
            catch (Exception exception)
            {
                failure = $"Could not correct the driven bones: {exception.Message}";
            }
            if (failure == null && corrected > 0)
            {
                import.DrivenRounds++;
                _framework.RunOnTick(() => Complete(generation), delayTicks: DrivenRoundTicks);
                return;
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
            // Mark the import dead before undoing it. Late calls from the
            // game's bone update, the time limit or queued frame updates then
            // do nothing.
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

    /// <summary>
    /// Corrects bones the game moves on its own (through its KineDriver), such as
    /// an eyelid that follows the eye. Such a bone is measured in its own turn of
    /// the bone update, before the game moves it to follow the imported eye. The
    /// game moves it after every other bone, so without this correction the
    /// game's movement would be added twice, and the lid would close a little
    /// more on every save and load. Once the game has run on the imported pose,
    /// each such bone the file names, and each file bone under one, is set to its
    /// value in the file, in the same undo step. A bone whose parent moved this
    /// round waits for the next round. Returns how many bones moved.
    /// </summary>
    private int CorrectDrivenBones(PendingPoseImport import)
    {
        int corrected = 0;
        if (import.Stage == PoseImportStage.HeadRestore
            || _bindings.Resolve(import.TargetActorId) is not { Success: true, Value: { } actor })
            return 0;
        foreach (var skeleton in _skeletons.GetSkeletons(actor))
        {
            if (skeleton is not Skeleton live || live.DrivenBones.Count == 0)
                continue;
            var poseInfo = _posing.GetPoseInfo(skeleton);
            foreach (var slot in import.ApplySlots)
            {
                if (slot.Slot != skeleton.Slot)
                    continue;
                var moved = new HashSet<IBone>();
                foreach (var bone in skeleton.Bones)
                {
                    if (!slot.Writes.TryGetValue((bone.PartialId, bone.BoneName), out var entry))
                        continue;
                    bool underDriver = false, ancestorMoved = false;
                    for (IBone? each = bone; each != null && each.PartialId == bone.PartialId; each = each.ParentBone)
                    {
                        underDriver |= live.DrivenBones.Contains((each.PartialId, each.BoneIndex));
                        ancestorMoved |= !ReferenceEquals(each, bone) && moved.Contains(each);
                    }
                    if (!underDriver || ancestorMoved)
                        continue;
                    var desired = _posing.ToApplySpace(bone, entry.File);
                    var basis = _posing.ToApplySpace(bone, bone.LastRawTransform);
                    var delta = BonePoseInfo.FilterDelta(BonePoseInfo.Diff(desired, basis), entry.Components);
                    if (TransformMath.IsApproximatelyIdentityDelta(delta))
                        continue;
                    import.MutationStarted = true;
                    if (poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).Apply(
                            desired, basis, TransformComponents.All, entry.Components,
                            forceNewStack: true, drivesIk: false) != null)
                    {
                        import.Written.Add(entry.Target);
                        moved.Add(bone);
                        corrected++;
                    }
                }
            }
        }
        return corrected;
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

    /// <summary>Used when Poser shuts down and the game's main thread cannot
    /// finish the import. This runs on another thread, so the caller must stop
    /// every other callback first.</summary>
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
        // This thread-safe flag is the only thing the game's bone update reads to
        // see whether the import is still running. Set it before anything else
        // is torn down.
        import.Invalidation.Invalidate();
        import.Invalidated = true;
        if (ReferenceEquals(Volatile.Read(ref _pending), import))
            Volatile.Write(ref _pending, null);
    }

    /// <summary>Marks a running import dead and publishes its final result. Only
    /// disposing on the game's main thread tries to undo the import.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (Volatile.Read(ref _pending) is { } import)
        {
            // Disposing has no way to return a result, so it publishes the true
            // final result before releasing callbacks. On the game's main thread
            // the import can be undone. On any other thread it is only marked
            // dead, and the result says no undo was tried.
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
