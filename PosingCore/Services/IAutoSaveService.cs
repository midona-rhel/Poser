using System;

namespace Poser.Services;

public enum AutoSaveCaptureStatus
{
    NotCaptured,
    Captured,
    DispatchStarted,
    Failure,
}

public readonly record struct AutoSaveCaptureResult(
    AutoSaveCaptureStatus Status,
    int CapturedActors,
    string? Detail = null,
    bool DispatchAccepted = false)
{
    public static AutoSaveCaptureResult NotCaptured(string? detail = null) =>
        new(AutoSaveCaptureStatus.NotCaptured, 0, detail);

    public static AutoSaveCaptureResult Captured(int actors, string? detail = null) =>
        new(AutoSaveCaptureStatus.Captured, actors, detail);

    public static AutoSaveCaptureResult DispatchStarted(
        int actors,
        string? detail = null) =>
        new(AutoSaveCaptureStatus.DispatchStarted, actors, detail, true);

    public static AutoSaveCaptureResult Failure(
        string detail,
        int capturedActors = 0,
        bool dispatchAccepted = false) =>
        new(AutoSaveCaptureStatus.Failure, capturedActors, detail, dispatchAccepted);
}

/// <summary>
/// Timed pose auto-save. While GPose is active and
/// <c>PoserConfiguration.AutoSave.Enabled</c> is set, every actor carrying
/// Poser-authored (unnamed-layer) edits is exported through
/// <see cref="IPoseFileService.ExportPose"/> into a timestamped folder under
/// <see cref="RootDirectory"/> on the configured interval, plus once when GPose
/// is left through the application lifecycle coordinator. Files are
/// byte-identical to a manual export; only the location and the trigger differ.
///
/// Snapshot folders older than <c>AutoSave.MaxAutoSaves</c> are pruned from disk
/// after each save, so retention survives a plugin restart.
/// </summary>
public interface IAutoSaveService : IDisposable
{
    /// <summary>
    /// Directory holding the timestamped snapshot folders
    /// (<c>&lt;pluginConfigDir&gt;/AutoSaves</c>). Stable for the service's
    /// lifetime; it may not exist if creating it failed.
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// UTC time when the last detached snapshot was dispatched to the existing
    /// worker, or null when none has been captured this session. This does not
    /// acknowledge worker completion or durable disk success.
    /// </summary>
    DateTime? LastSaveUtc { get; }

    /// <summary>
    /// Takes a periodic snapshot immediately, regardless of the interval, and
    /// returns the number of actors CAPTURED (0 when nothing had authored edits,
    /// in which case no folder is created). The disk write runs on a worker
    /// after this returns, so a captured actor can still fail to write; those
    /// failures are logged. Never throws: every capture failure is logged and
    /// the remaining actors are still attempted.
    /// </summary>
    /// <param name="reason">Short tag recorded in the log line, e.g. "interval".</param>
    int SaveNow(string reason);

    /// <summary>
    /// Performs the synchronous final capture requested on a GPose exit edge.
    /// Authored state is read and detached before this returns. The result
    /// distinguishes a skipped capture, a completed capture, worker dispatch,
    /// and failure; dispatch is not a write acknowledgement.
    /// </summary>
    AutoSaveCaptureResult CaptureForExit();
}
