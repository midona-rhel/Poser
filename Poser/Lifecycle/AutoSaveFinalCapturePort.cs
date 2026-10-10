using System;
using Poser.Application.Lifecycle;
using Poser.Services;
using Poser.Application.AutoSave;

namespace Poser.Lifecycle;

internal sealed class AutoSaveFinalCapturePort : IFinalCapturePort
{
    private readonly Func<IAutoSaveService> _resolve;

    public AutoSaveFinalCapturePort(Func<IAutoSaveService> resolve)
    {
        _resolve = resolve;
    }

    /// <summary>Maps the autosave capture result exhaustively into the
    /// Application-owned receipt.</summary>
    public FinalCaptureResult CaptureForExit()
    {
        var service = _resolve();
        var result = service.CaptureForExit();
        var terminal = service.CompleteForExit();
        // FinalCaptureResult carries the terminal status and the health
        // record unchanged; only the capture-status enum is translated.
        var persistence = terminal.Status;
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
        return mapped with { PersistenceEvidence = service.LastHealthRecord };
    }
}
