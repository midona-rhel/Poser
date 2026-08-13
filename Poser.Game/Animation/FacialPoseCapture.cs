using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using LegacyTransform = Poser.Transform;

namespace Poser.Game.Animation;

/// <summary>
/// Two-phase capture of a previewed facial animation into one transform patch.
/// The pending token is owner-local and session-bound: framework callbacks must
/// prove the exact session, actor, skeleton, and bindings before any write or
/// restoration can happen.
///
/// The two framework ticks are intentional. Poser applies facial layers as
/// deltas, so reading and writing on the same tick observes an identity delta;
/// after ReleaseExpression the face must settle before LastRawTransform is
/// used as the raw-baseline application basis. This keeps the proven
/// Brio/Ktisis interaction contract: release only the facial preview, preserve
/// expression/gaze semantics, and let one linked-aware command own the patch.
/// </summary>
public sealed class FacialPoseCapture : IDisposable
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly TransformCommandService _transforms;
    private readonly TransformGestureService _gestures;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;

    private const int SettleTicks = 2;

    private sealed class PendingBake
    {
        public required Guid OperationId;
        public required OperationEpoch OperationEpoch;
        public required SessionGeneration SessionGeneration;
        public required ActorId Actor;
        public required SkeletonId Skeleton;
        public required float? PriorSpeed;
        public required List<(BoneId Bone, LegacyTransform Captured)> Captures;
        public int TicksRemaining = SettleTicks;
        public bool Completing;
    }

    private PendingBake? _pending;
    private OperationEpoch _operationEpoch;
    private OperationReceipt? _lastReceipt;
    private bool _disposed;
    private bool _terminating;
    private int _disposeRequested;

    private bool DisposeRequested =>
        Volatile.Read(ref _disposeRequested) != 0;

    public FacialPoseCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        SceneSession scene,
        AnimationSession animation,
        TransformCommandService transforms,
        TransformGestureService gestures,
        ISessionGenerationSource sessionGeneration,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _scene = scene;
        _animation = animation;
        _transforms = transforms;
        _gestures = gestures;
        _sessionGeneration = sessionGeneration;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    public event Action<OperationReceipt>? ReceiptChanged;

    public bool IsPending => _pending != null;

    public OperationReceipt? LastReceipt => _lastReceipt;

    /// <summary>Returns a receipt only for the exact actor generation that
    /// initiated it; selection changes cannot consume another actor's result.</summary>
    public OperationReceipt? ReceiptFor(ActorId actor) =>
        _lastReceipt is { TargetActorId: var target } && target == actor
            ? _lastReceipt
            : null;

    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    /// <summary>
    /// Arms capture and returns the legacy success/failure shape used by the
    /// pane. The immutable operation receipt is available through
    /// <see cref="LastReceipt"/> and <see cref="ReceiptChanged"/>.
    /// </summary>
    public GestureResult Begin(ActorId actor, ActorDescriptor descriptor)
    {
        if (_disposed || DisposeRequested)
            return GestureResult.Fail("Face capture is disposed.");
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail(
                "Face capture must start on the framework thread.");
        if (_pending != null || _terminating)
            return GestureResult.Fail(
                "A face capture is already pending; cancel it and preview " +
                "the expression again before retrying.");

        if (!TryPrepare(actor, descriptor, out var session, out var captures,
                out var detail))
            return GestureResult.Fail(detail!);

        float? priorSpeed = _animation.OverridesFor(actor).OverallSpeed;
        if (!IsCurrentSession(session))
            return GestureResult.Fail(
                "The application session changed while arming capture.");
        bool setupTouchedSpeed = false;
        try
        {
            if (priorSpeed is not 0f)
            {
                setupTouchedSpeed = true;
                var paused = _animation.Pause(actor);
                if (!paused.Success)
                    return SetupFailure(actor, session, priorSpeed, setupTouchedSpeed,
                        paused.Detail ?? "Could not pause the actor.");
            }

            if (!IsCurrentSession(session))
                return SetupFailure(
                    actor,
                    session,
                    priorSpeed,
                    setupTouchedSpeed,
                    "The application session changed while pausing capture.");

            var stopped = _animation.ReleaseExpression(actor);
            if (!stopped.Success)
                return SetupFailure(actor, session, priorSpeed, setupTouchedSpeed,
                    stopped.Detail ?? "Could not stop the facial preview.");

            if (!IsCurrentSession(session))
                return SetupFailure(
                    actor,
                    session,
                    priorSpeed,
                    setupTouchedSpeed,
                    "The application session changed while stopping the preview.");

            _animation.SuspendCommands();
            if (!IsCurrentSession(session))
                return SetupFailure(actor, session, priorSpeed, setupTouchedSpeed,
                    "The application session changed while arming capture.");

            var epoch = NextEpoch();
            var operationId = Guid.NewGuid();
            var pending = new PendingBake
            {
                OperationId = operationId,
                OperationEpoch = epoch,
                SessionGeneration = session,
                Actor = actor,
                Skeleton = descriptor.CharacterSkeleton!.Id,
                PriorSpeed = priorSpeed,
                Captures = captures,
            };
            _pending = pending;
            Publish(OperationReceipt.Pending(
                operationId,
                epoch,
                session,
                actor,
                "Facial capture is settling."));
            return GestureResult.Ok();
        }
        catch (Exception exception)
        {
            return SetupFailure(
                actor,
                session,
                priorSpeed,
                setupTouchedSpeed,
                $"Could not arm facial capture: {exception.Message}");
        }
    }

    /// <summary>Invalidates the current operation before restoring its owned
    /// guard and speed. A late callback therefore cannot revive the token.</summary>
    public OperationReceipt? CancelPending(
        string detail = "Facial capture was cancelled.")
    {
        if (DisposeRequested)
            return _lastReceipt;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning(
                "Face capture cancellation was refused off the framework thread.");
            return _lastReceipt;
        }
        if (_pending is not { } pending)
            return _lastReceipt;
        if (pending.Completing)
            return _lastReceipt;
        InvalidatePending(pending, detail);
        return _lastReceipt;
    }

    private GestureResult SetupFailure(
        ActorId actor,
        SessionGeneration session,
        float? priorSpeed,
        bool setupTouchedSpeed,
        string detail)
    {
        if (_animation.CommandsSuspended)
            _animation.ResumeCommands();
        var restored = (setupTouchedSpeed || priorSpeed is not null) &&
                CanRestoreSetupIdentity(actor, session)
            ? RestoreSpeed(actor, priorSpeed)
            : AnimationResult.Ok();
        if (!restored.Success)
            detail = $"{detail} Speed restore failed: {restored.Detail}";
        return GestureResult.Fail(detail);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DisposeRequested && !_disposed &&
            _pending is not { Completing: true })
        {
            DisposeOnFrameworkThread();
            return;
        }
        if (_disposed || _pending is not { } pending)
            return;
        if (pending.Completing)
            return;

        // This check precedes the tick decrement and every possible write,
        // restore, or terminal publication from this callback.
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its session changed.");
            return;
        }

        if (--pending.TicksRemaining > 0)
            return;

        Complete(pending);
    }

    private void Complete(PendingBake pending)
    {
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;
        if (pending.Completing)
            return;
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its token became stale.");
            return;
        }

        if (Revalidate(pending) is { } problem)
        {
            InvalidatePending(pending, $"Facial capture cancelled: {problem}");
            return;
        }

        // Revalidate immediately before handing control to the one atomic
        // transform authority; the authority records one history patch.
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its token became stale.");
            return;
        }

        pending.Completing = true;

        var writes = new List<(
            TransformTargetId Target,
            Poser.Domain.Transforms.PoseTransform Desired)>(pending.Captures.Count);
        foreach (var (boneId, captured) in pending.Captures)
            writes.Add((
                TransformTargetId.ForBone(boneId),
                new Poser.Domain.Transforms.PoseTransform(
                    captured.Position, captured.Rotation, captured.Scale)));

        GestureResult applied;
        try
        {
            applied = _transforms.SetAbsoluteMany(
                writes,
                "Apply facial animation to pose",
                rawBaseline: true,
                cancellationRequested: () =>
                    DisposeRequested ||
                    !ReferenceEquals(_pending, pending) ||
                    !IsCurrentToken(pending));
        }
        catch (Exception exception)
        {
            applied = GestureResult.Fail(
                $"Facial capture apply threw: {exception.Message}");
        }

        if (DisposeRequested && !applied.Success)
        {
            pending.Completing = false;
            DisposeAfterCancelledCommand(pending, applied);
            return;
        }

        // Synchronous history/port observers may dispose this owner while the
        // command is committing. Only the still-owned exact token may tear
        // down or publish a terminal receipt.
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;
        var teardown = FinishOwnedState(pending);
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;
        if (!applied.Success)
        {
            var state = applied.Recovery is { Complete: false }
                ? OperationReceiptState.RecoveryRequired
                : applied.Recovery is not null
                    ? OperationReceiptState.RolledBack
                    : OperationReceiptState.Failed;
            var detail = applied.Detail ?? "Facial capture apply failed.";
            if (!teardown.Success)
            {
                state = applied.Recovery is { Complete: false }
                    ? OperationReceiptState.RecoveryRequired
                    : OperationReceiptState.Failed;
                detail = $"{detail} Teardown failed: {teardown.Detail}";
            }
            PublishCompletionTerminal(pending, CreateTerminal(
                pending,
                state,
                detail,
                applied.Recovery));
            return;
        }

        if (!teardown.Success)
        {
            PublishCompletionTerminal(pending, CreateTerminal(
                pending,
                OperationReceiptState.Failed,
                $"Facial pose applied, but teardown failed: {teardown.Detail}"));
            return;
        }

        PublishCompletionTerminal(pending, CreateTerminal(
            pending,
            OperationReceiptState.Applied,
            "Facial pose applied."));
    }

    private void InvalidatePending(
        PendingBake pending,
        string detail,
        TransformRecoveryReceipt? recovery = null,
        string? recoveryDetail = null)
    {
        if (!ReferenceEquals(_pending, pending))
            return;

        _pending = null;
        _terminating = true;
        try
        {
            _operationEpoch = NextEpoch();
            var teardown = FinishOwnedState(pending);
            var state = recovery is { Complete: false }
                ? OperationReceiptState.RecoveryRequired
                : teardown.Success
                    ? OperationReceiptState.Cancelled
                    : OperationReceiptState.Failed;
            if (recovery is { Complete: false } && recoveryDetail is not null)
                detail = $"{detail} {recoveryDetail}";
            Publish(CreateTerminal(
                pending,
                state,
                teardown.Success
                    ? detail
                    : $"{detail} Teardown failed: {teardown.Detail}",
                recovery));
        }
        finally
        {
            _terminating = false;
            if (DisposeRequested)
                DisposeOnFrameworkThread();
        }
    }

    private (bool Success, string? Detail) FinishOwnedState(PendingBake pending)
    {
        try
        {
            if (_animation.CommandsSuspended)
                _animation.ResumeCommands();
            // Speed ownership is actor-local. A session or skeleton change
            // invalidates bone writes, but the same exact ActorId is still the
            // only safe and required target for restoring the pre-capture speed.
            if (!CanRestoreSpeedIdentity(pending.Actor))
                return (
                    false,
                    "The exact actor is no longer available for playback speed restore.");
            var restored = RestoreSpeed(pending.Actor, pending.PriorSpeed);
            return restored.Success
                ? (true, null)
                : (false, restored.Detail ?? "Could not restore playback speed.");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private AnimationResult RestoreSpeed(ActorId actor, float? priorSpeed)
    {
        var restored = priorSpeed is { } speed
            ? _animation.SetSpeed(actor, speed)
            : _animation.ClearSpeed(actor);
        if (!restored.Success)
            _log.Warning($"Face capture could not restore playback speed: {restored.Detail}");
        return restored;
    }

    private bool TryPrepare(
        ActorId actor,
        ActorDescriptor descriptor,
        out SessionGeneration session,
        out List<(BoneId Bone, LegacyTransform Captured)> captures,
        out string? detail)
    {
        captures = new();
        detail = null;
        session = default;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            detail = "Face capture must start on the framework thread.";
            return false;
        }
        if (_gestures.ActiveGesture != null)
        {
            detail = "Finish the current transform gesture first.";
            return false;
        }
        if (_gestures.PendingRecovery != null)
        {
            detail = "Transform recovery must complete before face capture.";
            return false;
        }
        if (_sessionGeneration.ActiveSessionGeneration is not { } active)
        {
            detail = "Face capture requires an active application session.";
            return false;
        }
        if (descriptor.Id != actor)
        {
            detail = "The actor descriptor is stale.";
            return false;
        }
        if (FindActor(actor)?.CharacterSkeleton is not { } skeleton)
        {
            detail = "This actor has no current character skeleton.";
            return false;
        }
        if (descriptor.CharacterSkeleton is not { } described ||
            described.Id != skeleton.Id)
        {
            detail = "The actor character skeleton is stale.";
            return false;
        }
        if (_bindings.Resolve(actor) is not { Success: true })
        {
            detail = "The actor is not currently bound.";
            return false;
        }

        foreach (var bone in skeleton.Bones)
        {
            if (!IsFaceBone(bone.Id.CanonicalName))
                continue;
            if (_bindings.Resolve(bone.Id) is not { Success: true, Value: { } live })
            {
                detail = $"Face bone {bone.Id.CanonicalName} is not currently bound.";
                return false;
            }
            captures.Add((bone.Id, live.LastRawTransform));
        }

        if (captures.Count == 0)
        {
            detail = "This actor has no face bones to capture.";
            return false;
        }
        session = active;
        return true;
    }

    private ActorDescriptor? FindActor(ActorId actor)
    {
        foreach (var candidate in _scene.Snapshot.Actors)
            if (candidate.Id == actor)
                return candidate;
        return null;
    }

    private string? Revalidate(PendingBake pending)
    {
        if (!IsCurrentToken(pending))
            return "its session is no longer active";
        var actor = FindActor(pending.Actor);
        if (actor?.CharacterSkeleton is not { } skeleton)
            return "the character skeleton is gone";
        if (skeleton.Id != pending.Skeleton)
            return "the character skeleton was replaced";
        if (_bindings.Resolve(pending.Actor) is not { Success: true })
            return "the actor binding is stale";
        foreach (var (boneId, _) in pending.Captures)
            if (_bindings.Resolve(boneId) is not { Success: true })
                return $"bone {boneId.CanonicalName} was rebound";
        return null;
    }

    private bool CanRestoreSpeedIdentity(ActorId actor) =>
        FindActor(actor) is not null &&
        _bindings.Resolve(actor) is { Success: true };

    private bool CanRestoreSetupIdentity(
        ActorId actor,
        SessionGeneration session) =>
        IsCurrentSession(session) &&
        FindActor(actor) is not null &&
        _bindings.Resolve(actor) is { Success: true };

    private bool IsCurrentToken(PendingBake pending) =>
        !_disposed &&
        _sessionGeneration.ActiveSessionGeneration is { } active &&
        active == pending.SessionGeneration &&
        _operationEpoch == pending.OperationEpoch;

    private bool IsCurrentSession(SessionGeneration session) =>
        !_disposed &&
        _sessionGeneration.ActiveSessionGeneration is { } active &&
        active == session;

    private OperationEpoch NextEpoch() =>
        _operationEpoch = _operationEpoch.IsValid
            ? _operationEpoch.Next()
            : OperationEpoch.First;

    private OperationReceipt CreateTerminal(
        PendingBake pending,
        OperationReceiptState state,
        string detail,
        TransformRecoveryReceipt? recovery = null) =>
        OperationReceipt.Create(
            pending.OperationId,
            pending.OperationEpoch,
            pending.SessionGeneration,
            pending.Actor,
            state,
            detail,
            recovery);

    private void Publish(OperationReceipt receipt)
    {
        _lastReceipt = receipt;
        try
        {
            ReceiptChanged?.Invoke(receipt);
        }
        catch (Exception exception)
        {
            _log.Warning($"Facial capture receipt observer failed: {exception.Message}");
        }
    }

    private void PublishCompletionTerminal(
        PendingBake pending,
        OperationReceipt receipt)
    {
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;
        _pending = null;
        _terminating = true;
        try
        {
            Publish(receipt);
        }
        finally
        {
            _terminating = false;
            if (DisposeRequested)
                DisposeOnFrameworkThread();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;
        GC.SuppressFinalize(this);
        if (_framework.IsInFrameworkUpdateThread)
        {
            DisposeOnFrameworkThread();
            return;
        }

        try
        {
            _ = _framework.RunOnFrameworkThread(DisposeOnFrameworkThread);
        }
        catch (Exception exception)
        {
            // Never fall back to native/session writes on the provider's
            // off-thread shutdown path. The owner remains closed to new work.
            _log.Warning(
                $"Face capture disposal could not reach the framework thread: " +
                exception.Message);
        }
    }

    private void DisposeOnFrameworkThread()
    {
        if (_disposed)
            return;
        if (!_framework.IsInFrameworkUpdateThread)
            return;
        // A reentrant request from ApplyAbsolute cannot touch the facial
        // guard, speed, or receipts until SetAbsoluteMany observes the
        // cancellation and returns from its exhaustive rollback.
        if (_pending is { Completing: true } || _terminating)
            return;

        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        if (_pending is { } pending)
            InvalidatePending(pending, "Facial capture was disposed.");
    }

    private void DisposeAfterCancelledCommand(
        PendingBake pending,
        GestureResult applied)
    {
        if (_disposed || !ReferenceEquals(_pending, pending))
            return;
        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        InvalidatePending(
            pending,
            "Facial capture was disposed.",
            applied.Recovery,
            applied.Detail);
    }
}
