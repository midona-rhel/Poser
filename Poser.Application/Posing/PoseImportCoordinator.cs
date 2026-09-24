using Poser.Application.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Files;

namespace Poser.Application.Posing;

/// <summary>Owns the shared import pause, supersession, settle and completion workflow.</summary>
public sealed class PoseImportCoordinator(IPoseImportRuntime runtime, AnimationSession animation)
{
    private readonly IPoseImportRuntime _runtime = runtime;
    private readonly AnimationSession _animation = animation;
    private ImportArm? _importArm;
    private Action? _pendingSpeedRestore;

    private sealed class ImportArm
    {
        public required PoseImportOperation Operation;
        public required Action Restore;
    }

    public bool IsImportBusy => _importArm != null || _runtime.IsPending;

    public PoseEditResult Begin(
        ActorId actor,
        IPreparedPoseImport plan,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null,
        string? asset = null)
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

        // A completed import can still own its two-tick restore. Finish that
        // ownership before capturing the next import's speed baseline.
        _pendingSpeedRestore?.Invoke();

        bool freeze = options.FreezeOnImport ||
            _runtime.FreezeOnImport;
        float? priorSpeed = null;
        bool pausedForImport = false;
        if (_animation.IsSupported(actor))
        {
            priorSpeed = _animation.OverridesFor(actor).OverallSpeed;
            if (priorSpeed is not 0f)
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

        // Brio pauses, settles for four ticks, then rewinds before applying.
        // Restore only after our in-pass completion, with its two-tick settle delay.
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
                        suppressHistory: options.SuppressHistory,
                        asset: asset);
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
