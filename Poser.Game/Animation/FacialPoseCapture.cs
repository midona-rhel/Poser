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
using Poser.Game.Bindings;
using Poser.Game.Services;
using LegacyTransform = Poser.Domain.Transforms.Transform;

namespace Poser.Game.Animation;

/// <summary>
/// Bakes the expression being previewed into the pose, as one undo step. It
/// reads the face bones, puts the face animation back the way it was, lets
/// that animation play until the face stops moving, and then sets the face
/// bones on top of it so the expression stays.
/// While it waits, it checks every frame that the session, the actor and its
/// skeleton are still the same, and stops without changing anything if not.
/// An animation that keeps playing can change the face again afterwards.
/// Waiting for the face to stop moving gives up after a fixed time.
/// </summary>
public sealed class FacialPoseCapture : IDisposable, IFacialPoseCapture
{
    private readonly IFramework _framework;
    private readonly IEntityBindings _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly TransformCommands _transforms;
    private readonly TransformGestureService _gestures;
    private readonly IBonePosingService _posing;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;

    /// <summary>Frames to wait after starting before the face is first read.</summary>
    private const int CaptureDelayTicks = 2;

    /// <summary>How many frames in a row the face must read the same to count as still.</summary>
    private const int StableTicks = 2;

    /// <summary>The most frames to wait for the face to stop moving.</summary>
    private const int SettleTimeoutTicks = 20;

    /// <summary>How far a bone may move between two readings and still count as not moving.</summary>
    private const float StableEpsilon = 1e-5f;

    private enum BakePhase
    {
        /// <summary>Waiting for the game to update the face bones before reading them.</summary>
        Capturing,

        /// <summary>The face has been read and its animation put back; waiting
        /// for that animation to stop moving the face.</summary>
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

        /// <summary>Whether Poser is forcing the face animation to play at normal speed.</summary>
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
        IEntityBindings bindings,
        SceneSession scene,
        AnimationSession animation,
        TransformCommands transforms,
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

    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    /// <summary>Starts the bake. Success only means it started; a later status report says when it is done.</summary>
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
            _animation.HoldLoops(true);
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

    /// <summary>Stops the bake in progress, then undoes the temporary changes it made to the actor.</summary>
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
        _animation.HoldLoops(false);
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

        // Check the bake still belongs to this session before reading, writing or reporting anything.
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its session changed.");
            return;
        }

        // A request to update the face bones lasts one frame, so ask again every frame while waiting.
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

        // Stop forcing the face animation to play before setting the bones,
        // then wait for the face to be still again, so the bake matches what
        // the actor will show afterwards.
        if (pending.DriveHeld)
        {
            ReleaseFacialDrive(pending);
            pending.StableRuns = 0;
            return;
        }

        Complete(pending);
    }

    /// <summary>Reads the face, puts its animation back, and starts waiting for the face to be still.</summary>
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

        var restored = _animation.RestoreFacialLayer(pending.Actor);
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
    /// Makes the face animation play at normal speed while waiting, even if it
    /// was paused. The game still multiplies this by the actor's overall
    /// animation speed. Poser stops forcing it before the last reading, which
    /// the baked bones are measured against.
    /// </summary>
    private void TakeFacialDrive(PendingBake pending)
    {
        var driven = _animation.SetSlotSpeed(pending.Actor, AnimationSlot.Facial, 1f);
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
        var released = _animation.ClearSlotSpeed(pending.Actor, AnimationSlot.Facial);
        if (!released.Success)
            _log.Warning(
                $"Face bake could not release the facial layer: {released.Detail}");
    }

    /// <summary>Reads each face bone as the game's animation set it, before Poser's own changes.</summary>
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

    /// <summary>Asks the game to update the face bones' animation values for the next reading.</summary>
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
        // Check the session, actor and skeleton are still the same right before writing the bones.
        if (Revalidate(pending) is { } problem)
        {
            InvalidatePending(pending, $"Facial capture cancelled: {problem}");
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
                rawBaseline: true);
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

        // While Completing is set, cancel and shutdown both wait, so this is still
        // the same bake.
        var teardown = FinishOwnedState(pending);
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

    /// <summary>Stops forcing the face animation to play and lets animation commands run again.</summary>
    private (bool Success, string? Detail) FinishOwnedState(PendingBake pending)
    {
        try
        {
            ReleaseFacialDrive(pending);
            _animation.HoldLoops(false);
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
            // When shutdown comes from another thread, never touch the game or
            // the session from here. This object still takes no new bakes.
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
        // A shutdown that arrives while the bone writes are still running
        // must wait. It may not change the face animation, its speed or the
        // status reports until the writes notice the cancel and undo themselves.
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
        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        InvalidatePending(
            pending,
            "Facial capture was disposed.",
            applied.Recovery,
            applied.Detail);
    }
}
