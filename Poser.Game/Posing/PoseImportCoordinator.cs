using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Posing;
using Poser.Application.Settings;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Game.Bindings;

namespace Poser.Game.Posing;

/// <summary>One import that was accepted. Work that runs later first checks that this is
/// still the current import.</summary>
public sealed class PoseImportOperation(OperationReceipt pending)
{
    public OperationReceipt Pending { get; } = pending;
}

/// <summary>Runs a pose import: pauses the actor's animation, waits for it to settle,
/// applies the pose through <see cref="PoseImportCapture"/>, then restores the speed.
/// A new import cancels one still running.</summary>
public sealed class PoseImportCoordinator(
    PoseImportCapture capture,
    StableBindingRegistry bindings,
    IAnimationPlayback animation,
    ConfigurationService configuration,
    IFramework framework,
    IPluginLog log)
{
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
    public bool IsImportBusy => _importArm != null || capture.IsPending || _holder != null;

    /// <summary>Whether an import started now would be refused or would cancel
    /// one already running. A scene load checks this for its own imports.
    /// Call only from the game's main thread.</summary>
    public bool IsSlotBusy => _importArm != null || capture.AdmissionBusy;

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
        capture.CancelActive(detail);
        arm.Restore();
        if (ReferenceEquals(_importArm, arm))
            _importArm = null;
    }

    public PoseEditResult Begin(
        ActorId actor,
        PoseImportPlan plan,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null)
    {
        options = options.Clone();
        if (plan.IsEmpty)
            return PoseEditResult.Fail(
                "Nothing in this file applies to the chosen scope.");
        if (!framework.IsInFrameworkUpdateThread)
            return PoseEditResult.Fail("Pose import must run on the framework thread.");
        if (_importArm != null || capture.IsPending)
        {
            var priorArm = _importArm;
            var cancelled = capture.CancelActive(
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
            configuration.Config.FreezeActorOnPoseImport;
        float? priorSpeed = null;
        bool pausedForImport = false;
        if (animation.IsSupported(actor))
        {
            priorSpeed = animation.OverridesFor(actor).OverallSpeed;
            // Pausing for the import is not an undo step.
            if (priorSpeed is not 0f)
                using (animation.Untracked())
                    pausedForImport = animation.Pause(actor).Success;
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
            if (!animation.IsPaused(actor))
                return;
            using var untracked = animation.Untracked();
            if (priorSpeed is { } speed)
                animation.SetSpeed(actor, speed);
            else
                animation.Resume(actor);
        }

        void ScheduleRestore()
        {
            _pendingSpeedRestore = RestorePriorSpeed;
            framework.RunOnTick(RestorePriorSpeed, delayTicks: 2);
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
                log.Warning($"Pose edit '{description}' receipt callback threw: {ex.Message}");
            }
        }

        PoseImportOperation? operation = null;
        var reserved = bindings.Resolve(actor) is { Success: true, Value: { } live }
            ? capture.Reserve(
                live,
                description,
                out operation,
                onFinished: success =>
                {
                    if (!freeze || !success)
                        ScheduleRestore();
                },
                onReceipt: PublishReceipt)
            : GestureResult.Fail("The actor is no longer available.");
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
        framework.RunOnTick(() =>
        {
            if (!ReferenceEquals(_importArm, arm) || !capture.IsCurrent(arm.Operation))
                return;
            try
            {
                var rewound = animation.RewindPausedControls(actor);
                if (!rewound.Success)
                    log.Warning($"Pose edit '{description}': settle rewind failed: {rewound.Detail}");

                var begun = capture.Begin(
                    arm.Operation,
                    plan,
                    options.AsExpression,
                    options.SuppressHistory);
                if (!begun.Success)
                {
                    log.Warning(
                        $"Pose edit '{description}' failed: {begun.Detail ?? "The pose import failed."}");
                    ScheduleRestore();
                }
            }
            catch (Exception ex)
            {
                log.Error($"Pose edit '{description}' failed while arming: {ex.Message}");
                RestorePriorSpeed();
            }
        }, delayTicks: 4);
        return PoseEditResult.Ok(plan.FileBoneCount) with
        {
            OperationReceipt = pending,
        };
    }
}
