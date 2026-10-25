using System;
using Poser.Documents.AutoSave;

namespace Poser.Application.AutoSave;

public enum AutoSaveCaptureStatus
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

public enum AutoSaveTerminalStatus
{
    /// <summary>No exit drain has been requested.</summary>
    Pending,
    /// <summary>Every accepted snapshot reached durable storage.</summary>
    Written,
    /// <summary>Clean-on-exit removed all snapshots after a successful drain.</summary>
    Cleaned,
    /// <summary>Capture, writing, cleanup, or worker ownership failed.</summary>
    RecoveryRequired,
    /// <summary>No final snapshot was requested.</summary>
    NotAttempted,
}

public readonly record struct AutoSaveTerminalResult(
    AutoSaveTerminalStatus Status,
    string? Detail = null)
{
    public static AutoSaveTerminalResult PendingResult =>
        new(AutoSaveTerminalStatus.Pending);

    public static AutoSaveTerminalResult Written(string? detail = null) =>
        new(AutoSaveTerminalStatus.Written, detail);

    public static AutoSaveTerminalResult Cleaned(string? detail = null) =>
        new(AutoSaveTerminalStatus.Cleaned, detail);

    public static AutoSaveTerminalResult RecoveryRequired(string detail) =>
        new(AutoSaveTerminalStatus.RecoveryRequired, detail);

    public static AutoSaveTerminalResult NotAttempted(string? detail = null) =>
        new(AutoSaveTerminalStatus.NotAttempted, detail);
}

/// <summary>
/// Result of copying pose data out of the game and offering it to the disk
/// writer. An accepted dispatch does not mean the files were written.
/// </summary>
public readonly record struct AutoSaveCaptureResult(
    AutoSaveCaptureStatus Status,
    int CapturedActors,
    string? Detail = null,
    bool DispatchAccepted = false)
{
    /// <summary>
    /// True only when pose data was copied for every eligible actor.
    /// </summary>
    public bool CaptureCompleted =>
        Status is AutoSaveCaptureStatus.Captured or AutoSaveCaptureStatus.DispatchStarted;

    public static AutoSaveCaptureResult NotCaptured(string? detail = null) =>
        new(AutoSaveCaptureStatus.NotCaptured, 0, detail);

    public static AutoSaveCaptureResult Captured(int actors, string? detail = null) =>
        new(AutoSaveCaptureStatus.Captured, actors, detail);

    /// <summary>
    /// Reports that the writer accepted the captured data. It does not confirm
    /// that writing started or completed.
    /// </summary>
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
/// Legacy timed pose autosave service. While enabled in GPose, it captures
/// actors with Poser pose edits and writes timestamped snapshots in the
/// background. Exit either queues one final capture or, when CleanOnExit is
/// enabled, waits for pending work and deletes the snapshots. The health file
/// records write and recovery status and is not counted as a snapshot.
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
    /// Result of the latest exit attempt. Pending means completion has not yet been recorded.
    /// </summary>
    AutoSaveTerminalResult LastTerminalResult { get; }

    /// <summary>
    /// Latest saved or recovered health information, if available.
    /// </summary>
    AutoSaveHealthRecord? LastHealthRecord { get; }

    /// <summary>
    /// Captures pose data once when leaving GPose, unless autosave is disabled,
    /// no actor is eligible, or CleanOnExit selects deletion instead. Repeated
    /// calls for the same exit do not capture again. Call
    /// <see cref="CompleteForExit"/> to wait for writing or deletion to finish.
    /// </summary>
    AutoSaveCaptureResult CaptureForExit();

    /// <summary>
    /// Stops accepting periodic saves, waits for the writer, and completes the
    /// final write or CleanOnExit deletion before returning.
    /// </summary>
    AutoSaveTerminalResult CompleteForExit();
}
