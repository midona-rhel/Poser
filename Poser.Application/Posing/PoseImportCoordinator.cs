using Poser.Application.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Documents.Files;

namespace Poser.Application.Posing;

/// <summary>Runs a pose import: pauses the actor's animation, waits for it to settle,
/// applies the pose, then restores the speed. A new import cancels one still running.</summary>
public sealed class PoseImportCoordinator(IPoseImportRuntime runtime, IAnimationPlayback animation)
{
    private readonly IPoseImportRuntime _runtime = runtime;
    private readonly IAnimationPlayback _animation = animation;
    private ImportArm? _importArm;
    private Action? _pendingSpeedRestore;

    private sealed class ImportArm
    {
        public required PoseImportOperation Operation;
        public required Action Restore;
    }

    private object? _holder;

    /// <summary>True while a pose import runs and also while a scene loads, so
    /// features that wait for this, such as the pose preview, wait for the
    /// scene load to finish instead of failing.</summary>
    public bool IsImportBusy => _importArm != null || _runtime.IsPending || _holder != null;

    /// <summary>Whether an import started now would be refused or would cancel
    /// one already running. A scene load checks this for its own imports.
    /// Call only from the game's main thread.</summary>
    public bool IsSlotBusy => _importArm != null || _runtime.AdmissionBusy;

    /// <summary>
    /// A scene load calls this when it starts changing the game and releases
    /// it when it ends, so <see cref="IsImportBusy"/> stays true for the whole
    /// load. Call only from the game's main thread.
    /// </summary>
    public void Hold(object holder) => _holder = holder;

    public void Release(object holder)
    {
        if (ReferenceEquals(_holder, holder))
            _holder = null;
    }

    /// <summary>Cancels the running import if it is still the one with this
    /// <paramref name="operationId"/>. The import tells whoever started it that
    /// it was cancelled. Call only from the game's main thread.</summary>
    public void Cancel(Guid operationId, string detail)
    {
        if (_importArm is not { } arm || arm.Operation.Pending.OperationId != operationId)
            return;
        _runtime.CancelActive(detail);
        arm.Restore();
        if (ReferenceEquals(_importArm, arm))
            _importArm = null;
    }

    public PoseEditResult Begin(
        ActorId actor,
        IPreparedPoseImport plan,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null)
    {
        options = options.Clone();
        if (plan.IsEmpty)
            return PoseEditResult.Fail(
                "Nothing in this file applies to the chosen scope.");
        if (!_runtime.IsFrameworkThread)
            return PoseEditResult.Fail("Pose import must run on the framework thread.");
        if (_importArm != null || _runtime.IsPending)
        {
            var priorArm = _importArm;
            var cancelled = _runtime.CancelActive(
                "Pose import superseded by a newer request.");
            priorArm?.Restore();
            if (ReferenceEquals(_importArm, priorArm))
                _importArm = null;
            if (cancelled.OperationReceipt is not { State: OperationReceiptState.Cancelled })
                return PoseEditResult.Fail(cancelled.Detail ??
                    "The previous pose import could not be cancelled safely.") with
                {
                    Recovery = cancelled.Recovery,
                    OperationReceipt = cancelled.OperationReceipt,
                };
        }

        // A finished import may still be waiting two frames to restore the
        // speed. Restore it now, before reading the speed to return to.
        _pendingSpeedRestore?.Invoke();

        bool freeze = options.FreezeOnImport ||
            _runtime.FreezeOnImport;
        float? priorSpeed = null;
        bool pausedForImport = false;
        if (_animation.IsSupported(actor))
        {
            priorSpeed = _animation.OverridesFor(actor).OverallSpeed;
            // Pausing for the import is not an undo step.
            if (priorSpeed is not 0f)
                using (_animation.Untracked())
                    pausedForImport = _animation.Pause(actor).Success;
        }

        var restored = false;
        void RestorePriorSpeed()
        {
            if (_pendingSpeedRestore == RestorePriorSpeed)
                _pendingSpeedRestore = null;
            if (restored)
                return;
            restored = true;
            if (!pausedForImport)
                return;
            if (!_animation.IsPaused(actor))
                return;
            using var untracked = _animation.Untracked();
            if (priorSpeed is { } speed)
                _animation.SetSpeed(actor, speed);
            else
                _animation.Resume(actor);
        }

        void ScheduleRestore()
        {
            _pendingSpeedRestore = RestorePriorSpeed;
            try
            {
                _runtime.Schedule(RestorePriorSpeed, ticks: 2);
            }
            catch (Exception ex)
            {
                _runtime.Report(
                    $"Pose edit '{description}' restore scheduling failed: {ex.Message}");
                RestorePriorSpeed();
            }
        }

        ImportArm? arm = null;
        void PublishReceipt(OperationReceipt receipt)
        {
            if (receipt.State != OperationReceiptState.Pending &&
                ReferenceEquals(_importArm, arm))
                _importArm = null;
            try
            {
                onReceipt?.Invoke(receipt);
            }
            catch (Exception ex)
            {
                _runtime.Report(
                    $"Pose edit '{description}' receipt callback threw: {ex.Message}");
            }
        }

        var reserved = _runtime.Reserve(
            actor,
            description,
            out var operation,
            onFinished: success =>
            {
                if (!freeze || !success)
                    ScheduleRestore();
            },
            onReceipt: PublishReceipt);
        if (!reserved.Success || operation == null ||
            reserved.OperationReceipt is not { } pending)
        {
            RestorePriorSpeed();
            return PoseEditResult.Fail(
                reserved.Detail ?? "The pose import could not be admitted.") with
            {
                Recovery = reserved.Recovery,
                OperationReceipt = reserved.OperationReceipt,
            };
        }
        arm = new ImportArm
        {
            Operation = operation,
            Restore = RestorePriorSpeed,
        };
        _importArm = arm;
        PublishReceipt(pending);

        // As in Brio: pause, wait four frames for the animation to settle,
        // rewind it, then apply the pose. The speed comes back two frames
        // after the import finishes.
        try
        {
            _runtime.Schedule(() =>
            {
                if (!ReferenceEquals(_importArm, arm) ||
                    !_runtime.IsCurrent(arm.Operation))
                    return;
                try
                {
                    var rewound = _animation.RewindPausedControls(actor);
                    if (!rewound.Success)
                        _runtime.Report(
                            $"Pose edit '{description}': settle rewind failed: {rewound.Detail}");

                    var begun = _runtime.Begin(
                        arm.Operation,
                        plan,
                        expression: options.AsExpression,
                        suppressHistory: options.SuppressHistory);
                    if (!begun.Success)
                    {
                        _runtime.Report(
                            $"Pose edit '{description}' failed: {begun.Detail ?? "The pose import failed."}");
                        ScheduleRestore();
                    }
                }
                catch (Exception ex)
                {
                    _runtime.Report(
                        $"Pose edit '{description}' failed while arming: {ex.Message}", error: true);
                    RestorePriorSpeed();
                }
            }, ticks: 4);
        }
        catch (Exception ex)
        {
            var cancelled = _runtime.CancelActive(
                $"Pose import arm scheduling failed: {ex.Message}");
            RestorePriorSpeed();
            if (ReferenceEquals(_importArm, arm))
                _importArm = null;
            return PoseEditResult.Fail(
                cancelled.Detail ?? "The pose import could not be scheduled.") with
            {
                Recovery = cancelled.Recovery,
                OperationReceipt = cancelled.OperationReceipt,
            };
        }
        return PoseEditResult.Ok(plan.FileBoneCount) with
        {
            OperationReceipt = pending,
        };
    }

}
