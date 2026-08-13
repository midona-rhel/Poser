namespace Poser.Application.Lifecycle;

/// <summary>
/// Truth about the synchronous final-capture boundary. A dispatch is not a
/// write acknowledgement: the existing persistence worker remains separate.
/// </summary>
public enum FinalCaptureStatus
{
    /// <summary>No immutable actor data was captured.</summary>
    NotCaptured,
    /// <summary>All eligible actor data was detached; no dispatch was accepted.</summary>
    Captured,
    /// <summary>All eligible actor data was detached and dispatch was accepted.</summary>
    DispatchStarted,
    /// <summary>The attempt failed; captured actors may be partial.</summary>
    Failure,
}

/// <summary>
/// Result of one final-capture attempt. <see cref="DispatchAccepted"/> means
/// only that the existing worker dispatcher accepted detached data; it does
/// not mean the worker ran or that a file was written. A partial failure is not
/// capture-complete even when <see cref="CapturedActors"/> is non-zero.
/// </summary>
public readonly record struct FinalCaptureResult(
    FinalCaptureStatus Status,
    int CapturedActors,
    string? Detail = null,
    bool DispatchAccepted = false)
{
    /// <summary>
    /// True only when every eligible actor in the attempt was detached. A
    /// partial <see cref="FinalCaptureStatus.Failure"/> remains false.
    /// </summary>
    public bool CaptureCompleted =>
        Status is FinalCaptureStatus.Captured or FinalCaptureStatus.DispatchStarted;

    public static FinalCaptureResult NotCaptured(string? detail = null) =>
        new(FinalCaptureStatus.NotCaptured, 0, detail);

    public static FinalCaptureResult Captured(int actors, string? detail = null) =>
        new(FinalCaptureStatus.Captured, actors, detail);

    /// <summary>
    /// Reports that the existing dispatcher accepted the detached snapshot;
    /// the status name does not acknowledge worker execution or disk writing.
    /// </summary>
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
