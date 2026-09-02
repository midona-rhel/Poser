using Poser.Domain.Transforms;
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
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
/// Bakes the previewed expression into one transform patch. The capture reads
/// the face, restores the facial layer, drives that layer when needed, waits
/// for stable readings, and applies a delta against the restored layer.
/// Pending operations are bound to the current session, actor, skeleton, and
/// bindings; callbacks revalidate those identities before writing or restoring.
/// The result is exact for the captured facial output and can drift while a
/// running animation continues to change that output.
/// </summary>
public sealed class FacialPoseCapture : IDisposable, IFacialPoseCapture
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly TransformCommandService _transforms;
    private readonly TransformGestureService _gestures;
    private readonly IBonePosingService _posing;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;

    /// <summary>Ticks between arming and the first face read.</summary>
    private const int CaptureDelayTicks = 2;

    /// <summary>Consecutive equal readings required before settling.</summary>
    private const int StableTicks = 2;

    /// <summary>Maximum ticks spent waiting for a stable face.</summary>
    private const int SettleTimeoutTicks = 20;

    /// <summary>Model-space tolerance used when comparing readings.</summary>
    private const float StableEpsilon = 1e-5f;

    private enum BakePhase
    {
        /// <summary>Waiting for a pass to refresh the raw caches.</summary>
        Capturing,

        /// <summary>Face captured, facial layer restored, waiting for the
        /// layer's own output to settle.</summary>
        Settling,
    }

    private sealed class PendingBake
    {
        public required Guid OperationId;
        public required OperationEpoch OperationEpoch;
        public required SessionGeneration SessionGeneration;
        public required ActorId Actor;
        public required SkeletonId Skeleton;
        public required List<BoneId> Bones;
        public BakePhase Phase = BakePhase.Capturing;
        public int CaptureDelay = CaptureDelayTicks;
        public List<(BoneId Bone, LegacyTransform Captured)> Captures = new();
        public List<LegacyTransform> LastReading = new();
        public int StableRuns;
        public int SettleTicks;

        /// <summary>Whether the facial slot speed override is held.</summary>
        public bool DriveHeld;
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
        IBonePosingService posing,
        ISessionGenerationSource sessionGeneration,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _scene = scene;
        _animation = animation;
        _transforms = transforms;
        _gestures = gestures;
        _posing = posing;
        _sessionGeneration = sessionGeneration;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    public event Action<OperationReceipt>? ReceiptChanged;

    public bool IsPending => _pending != null;

    public OperationReceipt? LastReceipt => _lastReceipt;

    /// <summary>Returns the last receipt when it belongs to the actor.</summary>
    public OperationReceipt? ReceiptFor(ActorId actor) =>
        _lastReceipt is { TargetActorId: var target } && target == actor
            ? _lastReceipt
            : null;

    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    /// <summary>Arms capture and returns the operation's success or failure.</summary>
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

        if (!TryPrepare(actor, descriptor, out var session, out var bones,
                out var detail))
            return GestureResult.Fail(detail!);

        if (!IsCurrentSession(session))
            return GestureResult.Fail(
                "The application session changed while arming capture.");
        try
        {
            var epoch = NextEpoch();
            var operationId = Guid.NewGuid();
            var pending = new PendingBake
            {
                OperationId = operationId,
                OperationEpoch = epoch,
                SessionGeneration = session,
                Actor = actor,
                Skeleton = descriptor.CharacterSkeleton!.Id,
                Bones = bones,
            };
            _pending = pending;
            // Freeze other animation commands for the whole capture window.
            _animation.SuspendCommands();
            RequestRawRefresh(pending);
            Publish(OperationReceipt.Pending(
                operationId,
                epoch,
                session,
                actor,
                "Reading the face."));
            return GestureResult.Ok();
        }
        catch (Exception exception)
        {
            return SetupFailure(
                actor,
                session,
                $"Could not arm facial capture: {exception.Message}");
        }
    }

    /// <summary>Invalidates the current operation before restoring owned state.</summary>
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
        string detail)
    {
        if (_animation.CommandsSuspended)
            _animation.ResumeCommands();
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

        // Validate before any callback-side read, write, restore, or publication.
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its session changed.");
            return;
        }

        // The refresh lease lasts one rebuild, so renew it while waiting.
        RequestRawRefresh(pending);

        if (pending.Phase == BakePhase.Capturing)
        {
            if (--pending.CaptureDelay > 0)
                return;
            CaptureAndRestore(pending);
            return;
        }

        if (!TryReadFace(pending.Bones, out var reading, out var problem))
        {
            InvalidatePending(pending, $"Facial capture cancelled: {problem}");
            return;
        }

        pending.StableRuns = Settled(reading, pending.LastReading)
            ? pending.StableRuns + 1
            : 0;
        pending.LastReading = reading;
        bool arrived = pending.StableRuns >= StableTicks ||
            ++pending.SettleTicks >= SettleTimeoutTicks;
        if (!arrived)
            return;

        // Release the temporary drive before applying the patch, then settle
        // once more against the frame the actor will keep.
        if (pending.DriveHeld)
        {
            ReleaseFacialDrive(pending);
            pending.StableRuns = 0;
            return;
        }

        Complete(pending);
    }

    /// <summary>Reads the face, restores the facial layer, and starts settling.</summary>
    private void CaptureAndRestore(PendingBake pending)
    {
        if (!TryReadFace(pending.Bones, out var reading, out var problem))
        {
            InvalidatePending(pending, $"Facial capture cancelled: {problem}");
            return;
        }

        var captures =
            new List<(BoneId Bone, LegacyTransform Captured)>(pending.Bones.Count);
        for (var i = 0; i < pending.Bones.Count; i++)
            captures.Add((pending.Bones[i], reading[i]));

        var restored = WithBarrierLifted(
            () => _animation.RestoreFacialLayer(pending.Actor));
        if (!restored.Success)
        {
            InvalidatePending(
                pending,
                "Facial capture cancelled: " +
                (restored.Detail ?? "the facial layer could not be restored."));
            return;
        }

        TakeFacialDrive(pending);
        pending.Captures = captures;
        pending.LastReading = reading;
        pending.Phase = BakePhase.Settling;
        Publish(OperationReceipt.Pending(
            pending.OperationId,
            pending.OperationEpoch,
            pending.SessionGeneration,
            pending.Actor,
            "Facial capture is settling."));
    }

    /// <summary>
    /// Temporarily drives the facial slot at speed 1 so the layer can settle
    /// even when the actor's overall speed is paused. Released before the patch
    /// is written so the patch is measured against the final frame.
    /// </summary>
    private void TakeFacialDrive(PendingBake pending)
    {
        var driven = WithBarrierLifted(() =>
            _animation.SetSlotSpeed(pending.Actor, AnimationSlot.Facial, 1f));
        if (driven.Success)
        {
            pending.DriveHeld = true;
            return;
        }
        _log.Warning(
            $"Face bake could not drive the facial layer: {driven.Detail}");
    }

    private void ReleaseFacialDrive(PendingBake pending)
    {
        if (!pending.DriveHeld)
            return;
        pending.DriveHeld = false;
        var released = WithBarrierLifted(() =>
            _animation.ClearSlotSpeed(pending.Actor, AnimationSlot.Facial));
        if (!released.Success)
            _log.Warning(
                $"Face bake could not release the facial layer: {released.Detail}");
    }

    /// <summary>
    /// Runs one of the bake's OWN animation steps. The bake holds the command
    /// barrier from the button press onward, and the barrier exists to refuse
    /// everyone else — its owner's teardown is not "another command". The lift
    /// spans a single statement on the framework thread, so nothing can enter
    /// through the gap.
    /// </summary>
    private T WithBarrierLifted<T>(Func<T> step)
    {
        bool suspended = _animation.CommandsSuspended;
        if (suspended)
            _animation.ResumeCommands();
        try
        {
            return step();
        }
        finally
        {
            if (suspended)
                _animation.SuspendCommands();
        }
    }

    /// <summary>Reads the current raw transform for each face bone.</summary>
    private bool TryReadFace(
        IReadOnlyList<BoneId> bones,
        out List<LegacyTransform> reading,
        out string? problem)
    {
        reading = new List<LegacyTransform>(bones.Count);
        problem = null;
        foreach (var bone in bones)
        {
            if (_bindings.Resolve(bone) is not { Success: true, Value: { } live })
            {
                problem = $"bone {bone.CanonicalName} is no longer bound";
                return false;
            }
            reading.Add(live.LastRawTransform);
        }
        return true;
    }

    private static bool Settled(
        IReadOnlyList<LegacyTransform> current,
        IReadOnlyList<LegacyTransform> previous)
    {
        if (previous.Count != current.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = previous[i];
            if (Math.Abs(a.Position.X - b.Position.X) > StableEpsilon ||
                Math.Abs(a.Position.Y - b.Position.Y) > StableEpsilon ||
                Math.Abs(a.Position.Z - b.Position.Z) > StableEpsilon ||
                Math.Abs(a.Rotation.X - b.Rotation.X) > StableEpsilon ||
                Math.Abs(a.Rotation.Y - b.Rotation.Y) > StableEpsilon ||
                Math.Abs(a.Rotation.Z - b.Rotation.Z) > StableEpsilon ||
                Math.Abs(a.Rotation.W - b.Rotation.W) > StableEpsilon ||
                Math.Abs(a.Scale.X - b.Scale.X) > StableEpsilon ||
                Math.Abs(a.Scale.Y - b.Scale.Y) > StableEpsilon ||
                Math.Abs(a.Scale.Z - b.Scale.Z) > StableEpsilon)
                return false;
        }
        return true;
    }

    /// <summary>Requests a raw-transform refresh for the captured face bones.</summary>
    private void RequestRawRefresh(PendingBake pending)
    {
        foreach (var bone in pending.Bones)
        {
            if (_bindings.Resolve(bone) is
                { Success: true, Value: { Skeleton: { } skeleton } })
            {
                _posing.RequestRawTransformRefresh(skeleton);
                return;
            }
        }
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

        // Revalidate before the atomic transform write.
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
                "Bake expression into the pose",
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

    /// <summary>Releases the facial drive and command barrier owned by the bake.</summary>
    private (bool Success, string? Detail) FinishOwnedState(PendingBake pending)
    {
        try
        {
            ReleaseFacialDrive(pending);
            if (_animation.CommandsSuspended)
                _animation.ResumeCommands();
            return (true, null);
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private bool TryPrepare(
        ActorId actor,
        ActorDescriptor descriptor,
        out SessionGeneration session,
        out List<BoneId> captures,
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
        if (!_gestures.TryCompleteRecovery())
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
            if (_bindings.Resolve(bone.Id) is not { Success: true })
            {
                detail = $"Face bone {bone.Id.CanonicalName} is not currently bound.";
                return false;
            }
            captures.Add(bone.Id);
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
        foreach (var boneId in pending.Bones)
            if (_bindings.Resolve(boneId) is not { Success: true })
                return $"bone {boneId.CanonicalName} was rebound";
        return null;
    }

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
