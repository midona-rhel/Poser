using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Application.Lifecycle;

/// <summary>
/// Truth about the synchronous final-capture boundary. A dispatch is not a
/// write acknowledgement: the existing persistence worker remains separate.
/// Terminal durability is represented by <see cref="FinalPersistenceStatus"/>;
/// the host may retain additional immutable health evidence behind the port.
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

public enum FinalPersistenceStatus
{
    NotAttempted,
    Pending,
    Written,
    Cleaned,
    RecoveryRequired,
    Cancelled,
}

/// <summary>Immutable Application projection of one autosave recovery obligation.</summary>
public sealed class FinalPersistenceRecoveryEntry
{
    public FinalPersistenceRecoveryEntry(
        string operationId,
        string reason,
        FinalPersistenceStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors,
        int writtenActors,
        IEnumerable<string>? affectedPaths,
        string? failurePhase,
        string? detail,
        IEnumerable<string>? recoveryEvidencePaths)
    {
        OperationId = Limit(operationId, 128);
        Reason = Limit(reason, 128);
        Status = status;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        IntendedActors = Math.Clamp(intendedActors, 0, 8192);
        WrittenActors = Math.Clamp(writtenActors, 0, IntendedActors);
        AffectedPaths = Freeze(affectedPaths);
        FailurePhase = failurePhase is null ? null : Limit(failurePhase, 128);
        Detail = detail is null ? null : Limit(detail, 4096);
        RecoveryEvidencePaths = Freeze(recoveryEvidencePaths);
    }

    public string OperationId { get; }
    public string Reason { get; }
    public FinalPersistenceStatus Status { get; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; }
    public int IntendedActors { get; }
    public int WrittenActors { get; }
    public IReadOnlyList<string> AffectedPaths { get; }
    public string? FailurePhase { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    private static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IReadOnlyList<string> Freeze(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(256)
            .Select(static value => Limit(value, 1024))
            .ToArray());
}

/// <summary>Immutable additive evidence for terminal final persistence.</summary>
public sealed class FinalPersistenceEvidence
{
    public FinalPersistenceEvidence(
        string operationId,
        string reason,
        FinalPersistenceStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors,
        int writtenActors,
        IEnumerable<string>? affectedPaths,
        string? failurePhase,
        string? detail,
        IEnumerable<string>? recoveryEvidencePaths,
        IEnumerable<FinalPersistenceRecoveryEntry>? recoveryEntries = null,
        int recoveryOverflowCount = 0)
    {
        OperationId = operationId;
        Reason = reason;
        Status = status;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        IntendedActors = intendedActors;
        WrittenActors = writtenActors;
        AffectedPaths = Freeze(affectedPaths);
        FailurePhase = failurePhase is null ? null : Limit(failurePhase, 128);
        Detail = detail is null ? null : Limit(detail, 4096);
        RecoveryEvidencePaths = Freeze(recoveryEvidencePaths);
        var incoming = (recoveryEntries ?? Array.Empty<FinalPersistenceRecoveryEntry>()).ToArray();
        RecoveryEntries = Array.AsReadOnly(incoming.Take(MaxRecoveryEntries).ToArray());
        var discarded = Math.Max(0, incoming.Length - MaxRecoveryEntries);
        RecoveryOverflowCount = (int)Math.Min(int.MaxValue,
            (long)Math.Max(0, recoveryOverflowCount) + discarded);
    }

    public string OperationId { get; }
    public string Reason { get; }
    public FinalPersistenceStatus Status { get; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; }
    public int IntendedActors { get; }
    public int WrittenActors { get; }
    public IReadOnlyList<string> AffectedPaths { get; }
    public string? FailurePhase { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }
    public IReadOnlyList<FinalPersistenceRecoveryEntry> RecoveryEntries { get; }
    public int RecoveryOverflowCount { get; }

    private const int MaxRecoveryEntries = 4;

    private static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IReadOnlyList<string> Freeze(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(256)
            .Select(static value => Limit(value, 1024))
            .ToArray());
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
    FinalPersistenceStatus Persistence = FinalPersistenceStatus.NotAttempted,
    string? PersistenceDetail = null)
{
    /// <summary>
    /// True only when every eligible actor in the attempt was detached. A
    /// partial <see cref="FinalCaptureStatus.Failure"/> remains false.
    /// </summary>
    public bool CaptureCompleted =>
        Status is FinalCaptureStatus.Captured or FinalCaptureStatus.DispatchStarted;

    public bool DurableSuccess =>
        Persistence == FinalPersistenceStatus.Written;

    /// <summary>Additive persistence evidence; excluded from legacy positional
    /// equality and deconstruction.</summary>
    public FinalPersistenceEvidence? PersistenceEvidence { get; init; }

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
