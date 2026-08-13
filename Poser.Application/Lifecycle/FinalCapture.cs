namespace Poser.Application.Lifecycle;

/// <summary>
/// Truth about the synchronous final-capture boundary. A dispatch is not a
/// write acknowledgement: the existing persistence worker remains separate.
/// </summary>
public enum FinalCaptureStatus
{
    NotCaptured,
    Captured,
    DispatchStarted,
    Failure,
}

public readonly record struct FinalCaptureResult(
    FinalCaptureStatus Status,
    int CapturedActors,
    string? Detail = null,
    bool DispatchAccepted = false)
{
    public bool CaptureCompleted =>
        Status is FinalCaptureStatus.Captured or FinalCaptureStatus.DispatchStarted;

    public static FinalCaptureResult NotCaptured(string? detail = null) =>
        new(FinalCaptureStatus.NotCaptured, 0, detail);

    public static FinalCaptureResult Captured(int actors, string? detail = null) =>
        new(FinalCaptureStatus.Captured, actors, detail);

    public static FinalCaptureResult DispatchStarted(
        int actors,
        string? detail = null) =>
        new(FinalCaptureStatus.DispatchStarted, actors, detail, true);

    public static FinalCaptureResult Failure(
        string detail,
        int capturedActors = 0,
        bool dispatchAccepted = false) =>
        new(FinalCaptureStatus.Failure, capturedActors, detail, dispatchAccepted);
}
