using System;
using Poser.Documents.AutoSave;
using Poser.Application.AutoSave;

namespace Poser.Application.Lifecycle;

/// <summary>
/// Result of copying pose data before actors are removed. Accepting a writer
/// job does not mean a file was saved; AutoSaveTerminalStatus reports that separately.
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
    bool DispatchAccepted = false,
    AutoSaveTerminalStatus Persistence = AutoSaveTerminalStatus.NotAttempted,
    string? PersistenceDetail = null)
{
    /// <summary>
    /// True only when every eligible actor in the attempt was detached. A
    /// partial <see cref="FinalCaptureStatus.Failure"/> remains false.
    /// </summary>
    public bool CaptureCompleted =>
        Status is FinalCaptureStatus.Captured or FinalCaptureStatus.DispatchStarted;

    /// <summary>Last observed autosave health record, which may predate Persistence.
    /// Excluded from equality, hashing, and deconstruction.</summary>
    public AutoSaveHealthRecord? PersistenceEvidence { get; init; }

    public bool Equals(FinalCaptureResult other) =>
        Status == other.Status &&
        CapturedActors == other.CapturedActors &&
        Detail == other.Detail &&
        DispatchAccepted == other.DispatchAccepted &&
        Persistence == other.Persistence &&
        PersistenceDetail == other.PersistenceDetail;

    public override int GetHashCode() =>
        HashCode.Combine(Status, CapturedActors, Detail, DispatchAccepted, Persistence, PersistenceDetail);

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
