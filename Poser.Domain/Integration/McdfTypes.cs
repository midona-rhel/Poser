using System;
using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.Domain.Integration;

/// <summary>
/// Hard validation limits for reading an MCDF package. Exceeding any limit
/// is an explicit failure before actor mutation, never a silent trim.
/// </summary>
public sealed record McdfLimits(
    long MaxTotalBytes,
    long MaxFileBytes,
    int MaxFileCount,
    int MaxGamePathCount)
{
    /// <summary>Conservative defaults: 2 GiB expanded total, 512 MiB for a
    /// single file, 1024 file entries, 4096 game paths.</summary>
    public static McdfLimits Default => new(
        MaxTotalBytes: 2L * 1024 * 1024 * 1024,
        MaxFileBytes: 512L * 1024 * 1024,
        MaxFileCount: 1024,
        MaxGamePathCount: 4096);
}

public enum McdfOperationKind
{
    Import,
    Export,
}

public enum McdfPhase
{
    Reading,
    Validating,
    Extracting,
    Preparing,
    CapturingBaseline,
    ApplyingResources,
    ApplyingAppearance,
    AwaitingRedraw,
    ApplyingBodyProfile,
    Committing,
    CapturingExport,
    WritingPackage,
    RollingBack,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Final result of one MCDF operation.</summary>
public sealed record McdfOutcome(
    bool Success,
    bool Cancelled,
    string Detail,
    int Files,
    long UncompressedBytes,
    IReadOnlyList<string> SkippedResources);

/// <summary>
/// Immutable progress snapshot of the single running (or last finished)
/// MCDF operation. UI reads this every frame; it never mutates.
/// </summary>
public sealed record McdfProgress(
    ActorId Target,
    string FileName,
    McdfOperationKind Kind,
    McdfPhase Phase,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    bool Cancellable,
    McdfOutcome? Outcome)
{
    public bool Running => Outcome == null;
}

/// <summary>File-boundary progress callback payload.</summary>
public readonly record struct McdfProgressStep(
    McdfPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>
/// A validated, fully extracted MCDF package. Payloads live as generated
/// file names inside <see cref="OperationDirectory"/>; game paths are
/// normalized lower-case relative paths.
/// </summary>
public sealed record McdfPackage(
    string FileName,
    string Description,
    string GlamourerData,
    string CustomizePlusData,
    string ManipulationData,
    IReadOnlyDictionary<string, string> ReplacedGamePaths,
    IReadOnlyDictionary<string, string> SwappedGamePaths,
    string OperationDirectory,
    int FileCount,
    long TotalBytes)
{
    public bool HasResources =>
        ReplacedGamePaths.Count > 0 || SwappedGamePaths.Count > 0
        || ManipulationData.Length > 0;
}

/// <summary>One local file to embed on export, with every game path it replaces.</summary>
public sealed record McdfExportFile(IReadOnlyList<string> GamePaths, string LocalPath);

public enum McdfExportCandidateKind
{
    LocalFile,
    GamePath,
}

/// <summary>
/// Immutable filesystem observation supplied by the MCDF boundary. Local
/// paths are canonical real paths proven readable and contained by the mod
/// root; game-path observations retain their source text for application
/// semantic filtering and swap decisions.
/// </summary>
public sealed record McdfExportCandidate(
    string ActualPath,
    IReadOnlyList<string> GamePaths,
    McdfExportCandidateKind Kind,
    string? LocalPath,
    long Length);

/// <summary>Validated export candidates plus deterministic per-resource
/// skips. The application owns only MCDF path semantics after this point.</summary>
public sealed record McdfExportInspection(
    IReadOnlyList<McdfExportCandidate> Candidates,
    IReadOnlyList<string> Skipped);

/// <summary>Everything an MCDF export writes. Capture is complete before
/// writing starts; writing never touches the actor.</summary>
public sealed record McdfExportContent(
    string Description,
    string GlamourerData,
    string CustomizePlusData,
    string ManipulationData,
    IReadOnlyList<McdfExportFile> Files,
    IReadOnlyDictionary<string, string> Swaps);

public sealed record McdfWriteStats(int Files, long UncompressedBytes);
