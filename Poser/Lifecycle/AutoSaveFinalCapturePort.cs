using System;
using Poser.Application.Lifecycle;
using Poser.Services;

namespace Poser.Lifecycle;

internal sealed class AutoSaveFinalCapturePort : IFinalCapturePort
{
    private readonly Func<IAutoSaveService> _resolve;

    public AutoSaveFinalCapturePort(Func<IAutoSaveService> resolve)
    {
        _resolve = resolve;
    }

    public FinalCaptureResult CaptureForExit()
    {
        var result = _resolve().CaptureForExit();
        return result.Status switch
        {
            AutoSaveCaptureStatus.NotCaptured =>
                FinalCaptureResult.NotCaptured(result.Detail),
            AutoSaveCaptureStatus.Captured =>
                new FinalCaptureResult(
                    FinalCaptureStatus.Captured,
                    result.CapturedActors,
                    result.Detail,
                    result.DispatchAccepted),
            AutoSaveCaptureStatus.DispatchStarted =>
                FinalCaptureResult.DispatchStarted(
                    result.CapturedActors,
                    result.Detail),
            AutoSaveCaptureStatus.Failure =>
                FinalCaptureResult.Failure(
                    result.Detail ?? "Auto-save final capture failed.",
                    result.CapturedActors,
                    result.DispatchAccepted),
            _ => FinalCaptureResult.Failure(
                "Auto-save final capture returned an unknown result.",
                result.CapturedActors,
                result.DispatchAccepted),
        };
    }
}
