using System;
using System.Linq;
using Poser.Application.Lifecycle;
using Poser.Files;
using Poser.Services;

namespace Poser.Lifecycle;

internal sealed class AutoSaveFinalCapturePort : IFinalCapturePort
{
    private readonly Func<IAutoSaveService> _resolve;

    public AutoSaveFinalCapturePort(Func<IAutoSaveService> resolve)
    {
        _resolve = resolve;
    }

    /// <summary>Maps the autosave compatibility result and terminal health
    /// outcome exhaustively into the Application-owned receipt.</summary>
    public FinalCaptureResult CaptureForExit()
    {
        var service = _resolve();
        var result = service.CaptureForExit();
        var terminal = service.CompleteForExit();
        var persistence = terminal.Status switch
        {
            AutoSaveTerminalStatus.Pending => FinalPersistenceStatus.Pending,
            AutoSaveTerminalStatus.Written => FinalPersistenceStatus.Written,
            AutoSaveTerminalStatus.Cleaned => FinalPersistenceStatus.Cleaned,
            AutoSaveTerminalStatus.RecoveryRequired => FinalPersistenceStatus.RecoveryRequired,
            AutoSaveTerminalStatus.NotAttempted => FinalPersistenceStatus.NotAttempted,
            _ => throw new ArgumentOutOfRangeException(nameof(terminal.Status), terminal.Status, "Unknown autosave terminal status."),
        };
        var health = service.LastHealthRecord;
        var evidence = health is null
            ? null
            : new FinalPersistenceEvidence(
                health.OperationId,
                health.Reason,
                persistence,
                health.CreatedUtc,
                health.UpdatedUtc,
                health.IntendedActors,
                health.WrittenActors,
                health.AffectedPaths,
                health.FailurePhase,
                terminal.Detail ?? health.Detail,
                health.RecoveryEvidencePaths,
                health.RecoveryEntries.Select(entry => new FinalPersistenceRecoveryEntry(
                    entry.OperationId,
                    entry.Reason,
                    entry.Status switch
                    {
                        AutoSaveHealthStatus.Pending => FinalPersistenceStatus.Pending,
                        AutoSaveHealthStatus.Written => FinalPersistenceStatus.Written,
                        AutoSaveHealthStatus.Cleaned => FinalPersistenceStatus.Cleaned,
                        AutoSaveHealthStatus.RecoveryRequired => FinalPersistenceStatus.RecoveryRequired,
                        AutoSaveHealthStatus.Queued or AutoSaveHealthStatus.DispatchAccepted => FinalPersistenceStatus.Pending,
                        AutoSaveHealthStatus.Cancelled => FinalPersistenceStatus.Cancelled,
                        _ => throw new ArgumentOutOfRangeException(nameof(entry.Status), entry.Status, "Unknown autosave health status."),
                    },
                    entry.CreatedUtc,
                    entry.UpdatedUtc,
                    entry.IntendedActors,
                    entry.WrittenActors,
                    entry.AffectedPaths,
                    entry.FailurePhase,
                    entry.Detail,
                    entry.RecoveryEvidencePaths)),
                health.RecoveryOverflowCount);
        var mapped = result.Status switch
        {
            AutoSaveCaptureStatus.NotCaptured =>
                new FinalCaptureResult(
                    FinalCaptureStatus.NotCaptured,
                    result.CapturedActors,
                    result.Detail,
                    result.DispatchAccepted,
                    persistence,
                    terminal.Detail),
            AutoSaveCaptureStatus.Captured =>
                new FinalCaptureResult(
                    FinalCaptureStatus.Captured,
                    result.CapturedActors,
                    result.Detail,
                    result.DispatchAccepted,
                    persistence,
                    terminal.Detail),
            AutoSaveCaptureStatus.DispatchStarted =>
                new FinalCaptureResult(
                    FinalCaptureStatus.DispatchStarted,
                    result.CapturedActors,
                    result.Detail,
                    result.DispatchAccepted,
                    persistence,
                    terminal.Detail),
            AutoSaveCaptureStatus.Failure =>
                new FinalCaptureResult(
                    FinalCaptureStatus.Failure,
                    result.CapturedActors,
                    result.Detail ?? "Auto-save final capture failed.",
                    result.DispatchAccepted,
                    persistence,
                    terminal.Detail),
            _ => new FinalCaptureResult(
                FinalCaptureStatus.Failure,
                result.CapturedActors,
                "Auto-save final capture returned an unknown result.",
                result.DispatchAccepted,
                persistence,
                terminal.Detail),
        };
        return mapped with { PersistenceEvidence = evidence };
    }
}
