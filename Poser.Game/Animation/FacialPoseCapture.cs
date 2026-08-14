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
/// Bakes the previewed expression into the pose as one transform patch — the
/// end state Ktisis reaches with <c>DoPoseExpression</c>, expressed in Poser's
/// mechanism. The pending token is owner-local and session-bound: framework
/// callbacks must prove the exact session, actor, skeleton, and bindings
/// before any write or restoration can happen.
///
/// THE MECHANISM, and why each step exists.
///
/// Ktisis' pose store is absolute model space and its model-space sync is
/// hooked to do nothing, so syncing the played face into the frozen pose makes
/// the pose OWN the face outright (PosingManager.SyncFaceModelSpace,
/// PosingManager.cs:131-152; PosingModule.cs:86-91). Poser's pose is a per-bone
/// DELTA applied over whatever the animation gives that frame, so the same
/// end state is only expressible as
/// <c>stack = Diff(face-with-expression, face-after-the-facial-layer-returns)</c>.
/// That makes the released facial layer's own output a REQUIRED measurement,
/// which dictates everything below:
///
///   * The bake NEVER changes playback speed. Poser's pause writes
///     <c>PlaybackSpeed = 0</c> to every Havok control
///     (AnimationRuntimePort.ApplySpeedNow), so pausing before the release
///     freezes the very state the delta is measured against — the delta comes
///     out identity, the pose owns nothing, and the face collapses the moment
///     speed is handed back. An actor the USER paused is equally untouched:
///     nothing evaluates, the raw face stays on the expression, and an
///     identity delta is then the truthful answer.
///   * The teardown is <see cref="AnimationSession.RestoreFacialLayer"/>, not
///     ReleaseExpression: Brio's release ends with idle (3) on the BASE slot
///     and would reset the actor's body animation on every bake.
///   * The face is read from the apply pass's caches, so the pass is asked to
///     visit this skeleton on every tick the bake waits
///     (<see cref="IBonePosingService.RequestRawTransformRefresh"/>). Nothing
///     else refreshes <c>LastRawTransform</c>, and a skeleton nobody has posed
///     yet is not in the pass at all.
///   * The facial layer is DRIVEN at speed 1 across the settle, because a
///     paused actor's layer cannot otherwise reach the state being measured
///     (see <see cref="TakeFacialDrive"/>), and handed back before the patch.
///   * The settle waits for the face to STOP MOVING rather than counting a
///     fixed number of frames: a blend takes as long as it takes.
///
/// WHAT THE BAKED FACE IS EXACT AGAINST. The stored delta reproduces the
/// captured face only while the facial layer's own output stays on the frame
/// the settle ended on. That is precisely true for a paused actor — which is
/// why the bake goes to the trouble of leaving one frozen — and it is an
/// approximation that drifts with the animation for an actor that is running,
/// where the face is a moving target and no delta can be exact against it.
/// This is inherent to a delta pose over a live animation, not a defect of the
/// measurement.
/// </summary>
public sealed class FacialPoseCapture : IDisposable
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

    /// <summary>Ticks between arming and reading the face. The refresh lease
    /// asked for during the button press is consumed by the NEXT tick's
    /// rebuild, and the pass that acts on it runs after that tick's framework
    /// update — so the second tick is the first one whose caches were written
    /// by a pass that knew about this skeleton.</summary>
    private const int CaptureDelayTicks = 2;

    /// <summary>Consecutive equal readings that prove the facial layer has
    /// finished moving. Two, as Ktisis awaits two syncs.</summary>
    private const int StableTicks = 2;

    /// <summary>Upper bound on the settle. A face that never stops moving (a
    /// running idle animation blinks) is baked against the frame it is on
    /// rather than pending forever.</summary>
    private const int SettleTimeoutTicks = 20;

    /// <summary>Model-space epsilon for "this bone did not move". Below the
    /// scale of any facial blend and above float noise.</summary>
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

        /// <summary>Whether the facial layer is currently being driven at
        /// speed 1 for the settle. Released before the patch, and defensively
        /// again on every terminal path.</summary>
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
            // The barrier closes with the PRESS, not with the reading two
            // ticks later: an action-unit drag or a second pick in that window
            // would change the very face this bake is about to quote.
            _animation.SuspendCommands();
            // The first lease: the pass this asks for is what makes the
            // reading two ticks from now describe the live face rather than
            // the value the skeleton was built with.
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

        // This check precedes every possible read, write, restore, or
        // terminal publication from this callback.
        if (!IsCurrentToken(pending))
        {
            InvalidatePending(
                pending,
                "Facial capture was cancelled because its session changed.");
            return;
        }

        // Renewed every tick the bake waits: the lease is one rebuild long,
        // and both the capture and the settle read what the pass wrote.
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

        // The layer has arrived where it is going. Hand it back BEFORE the
        // patch, and settle once more: the frame the bake diffs against has to
        // be the frame the actor keeps, not one the drive was still turning.
        if (pending.DriveHeld)
        {
            ReleaseFacialDrive(pending);
            pending.StableRuns = 0;
            return;
        }

        Complete(pending);
    }

    /// <summary>
    /// The one tick that owns the whole measurement: read the face the user is
    /// looking at, put the facial layer back, and drive that layer so it can
    /// actually get there — in that order, because the reading must precede
    /// everything that changes what is being read.
    /// </summary>
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
    /// Holds the facial layer at speed 1 for the length of the settle — THE
    /// step that makes a bake work on a paused actor.
    ///
    /// A Poser pause is an ENFORCED container speed: the overall-speed detour
    /// returns true whenever it has an override (AnimationRuntimePort
    /// .OverallSpeedDetour), which is the game's "this changed, re-apply"
    /// signal, and the game then pushes that container speed down into every
    /// Havok control. Brio proves the propagation from the other side: it
    /// writes only the container and, four ticks later, finds
    /// <c>control-&gt;PlaybackSpeed == 0</c> on every control it walks
    /// (ActionTimelineCapability.StopSpeedAndResetTimeline, ATC:110-165).
    /// So a control-level write cannot open the layer back up — the game
    /// closes it again on its next recalculation.
    ///
    /// The per-slot speed can, because the game applies it through
    /// <c>SetSlotSpeed</c>, which the slot hook intercepts and REPLACES with
    /// the owned override (AnimationRuntimePort.SlotSpeedDetour). Brio's
    /// expression pin is this same lever pointed the other way: facial 0 while
    /// the container runs at 1. Pointed at 1 while the container is at 0, it
    /// lets the restored facial timeline blend in, and nothing else about the
    /// actor moves — the body stays exactly as frozen as the user left it.
    ///
    /// On an actor that is not paused this is the value the layer already had,
    /// so it changes nothing. It is released before the patch is written, so
    /// the face is measured on the frame it will keep.
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
        // A refused drive is not a failed bake: without it the settle simply
        // measures a layer that cannot move, which is the honest answer for
        // an actor whose slot speeds are unavailable.
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

    /// <summary>Every bone's current raw model-space transform, as the apply
    /// pass last wrote it. A bone that no longer resolves ends the bake: the
    /// captured absolutes describe a skeleton that is gone.</summary>
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

    /// <summary>Whether the face is holding still between two readings.</summary>
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

    /// <summary>
    /// Keeps this bake's skeleton in the apply pass for one frame, so that the
    /// raw caches it reads are the ones that pass just wrote.
    ///
    /// ONE skeleton, by construction: <see cref="TryPrepare"/> takes the face
    /// bones from the descriptor's character skeleton alone, and
    /// <see cref="Revalidate"/> ends the bake if that skeleton is replaced. So
    /// any bone that still resolves names the skeleton the whole bake is
    /// about; the loop exists because a single bone can be rebound while its
    /// skeleton is fine, not because the set could straddle two skeletons.
    /// </summary>
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

    /// <summary>
    /// Releases what the bake owns: the facial drive (already handed back on
    /// the success path, still held on every cancelled one) and the command
    /// barrier. The bake never took OVERALL speed, so it has none to hand
    /// back — the actor plays exactly as fast after the bake as before it.
    /// </summary>
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
