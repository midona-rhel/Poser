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

namespace Poser.Game.Posing;

/// <summary>
/// A pose import's terminal half: the one history entry an applied import
/// appends, the ordered rollback a failed or cancelled one gets, and the
/// single delivery of its terminal receipt to the caller's callbacks.
/// </summary>
internal sealed class PoseImportTerminal
{
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly ITransformRuntimePort _runtime;
    private readonly IPluginLog _log;

    public PoseImportTerminal(
        TransformHistory history,
        TransformGestureService gestures,
        ITransformRuntimePort runtime,
        IPluginLog log)
    {
        _history = history;
        _gestures = gestures;
        _runtime = runtime;
        _log = log;
    }

    /// <summary>Raised once for each accepted operation's terminal receipt.
    /// Delivery is framework-thread-only and synchronous refusals publish
    /// nothing.</summary>
    public event Action<OperationReceipt>? ReceiptPublished;

    /// <summary>The callback runs application code (the facade's speed
    /// restore); a throw there must not escape into the framework tick or
    /// the pass bookkeeping.</summary>
    public void Notify(PendingPoseImport import, OperationReceipt terminal)
    {
        if (import.TerminalPublished)
            return;
        import.TerminalPublished = true;
        try
        {
            import.OnFinished?.Invoke(terminal.State == OperationReceiptState.Applied);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose import completion callback threw: {ex.Message}");
        }

        try
        {
            import.OnReceipt?.Invoke(terminal);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose import receipt callback threw: {ex.Message}");
        }

        try
        {
            ReceiptPublished?.Invoke(terminal);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose import receipt subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    /// One undoable entry covering exactly what the import changed: the
    /// bones whose authored stacks the reset cleared, and the targets an
    /// action or the model edit wrote. Unlike the bake, an import that
    /// changed nothing is a legitimate outcome (the file matched the pose)
    /// and appends no entry rather than failing.
    /// </summary>
    public string? AppendHistory(PendingPoseImport import, Func<PendingPoseImport, bool> isFrameworkCurrent)
    {
        // Preview-body imports happen once per browsed file: recording them
        // would bury the user's real edits under scenery entries. A
        // suppressed import is the other case that may not spend the stack:
        // it IS an undo, walking that very stack.
        if (import.PreviewTarget || import.SuppressHistory)
            return null;

        var before = new List<TransformTargetState>();
        var after = new List<TransformTargetState>();
        foreach (var target in import.Order)
        {
            if (!isFrameworkCurrent(import))
                return "The pose import session or target was replaced.";
            var state = import.Before[target];
            // HasOverride is set by the capture to "this target had authored
            // layers", i.e. exactly what the reset cleared.
            var wasReset = import.Resets.Contains(target) && state.HasOverride;
            if (!wasReset && !import.Written.Contains(target))
                continue;
            var captured = _runtime.Capture(target);
            if (!captured.Success || captured.State is not { } current)
                return captured.Detail ?? $"Could not capture {target}.";
            before.Add(state);
            after.Add(current);
        }

        if (before.Count > 0)
            _history.Append(new TransformPatch(import.Description, before, after)
            {
                RequiredAsset = import.Asset,
            });
        return null;
    }

    public OperationReceipt CreateFailureTerminal(
        PendingPoseImport import,
        string detail,
        bool cancelled = false)
    {
        if (!import.MutationStarted)
            return cancelled
                ? OperationReceipt.Cancelled(
                    import.OperationId,
                    import.OperationEpoch,
                    import.SessionGeneration,
                    import.TargetActorId,
                    detail)
                : OperationReceipt.Failed(
                    import.OperationId,
                    import.OperationEpoch,
                    import.SessionGeneration,
                    import.TargetActorId,
                    detail);

        var restored = _gestures.RestoreForOperation(
            import.Order.Select(target => import.Before[target]).ToArray());
        if (restored.Recovery is not { } recovery)
        {
            return OperationReceipt.Failed(
                import.OperationId,
                import.OperationEpoch,
                import.SessionGeneration,
                import.TargetActorId,
                $"{detail} Rollback could not start: " +
                (restored.Detail ?? "no recovery evidence was produced."));
        }
        if (!recovery.Complete)
            return OperationReceipt.RecoveryRequired(
                import.OperationId,
                import.OperationEpoch,
                import.SessionGeneration,
                import.TargetActorId,
                TransformRecoveryDetail(detail, recovery),
                recovery);
        return cancelled
            ? OperationReceipt.Cancelled(
                import.OperationId,
                import.OperationEpoch,
                import.SessionGeneration,
                import.TargetActorId,
                detail,
                recovery)
            : OperationReceipt.RolledBack(
                import.OperationId,
                import.OperationEpoch,
                import.SessionGeneration,
                import.TargetActorId,
                detail,
                recovery);
    }

    private static string TransformRecoveryDetail(
        string primaryFailure,
        TransformRecoveryReceipt recovery) =>
        recovery.Complete
            ? primaryFailure
            : $"{primaryFailure} Rollback also failed: " +
              string.Join(
                      "; ",
                  recovery.Failures.Select(failure =>
                      failure.Detail ??
                       $"Could not restore {failure.RequestedState.Target}."));
}
