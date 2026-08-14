using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>An extraction directory together with the immutable proof that
/// this boundary exclusively created that exact directory.</summary>
public sealed record McdfOperationDirectory(
    string Path,
    string OwnerToken,
    string? Identity,
    string? MarkerIdentity);

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

/// <summary>
/// Everything an MCDF states about ITSELF, read from the header alone — no
/// extraction, no operation directory, no actor, no ownership. The format
/// carries no character name and no thumbnail, so
/// <see cref="Description"/> is the only free text in it and the rest is an
/// inventory of what the package would apply.
///
/// <para>This is what the library inspector shows beside a character file.
/// It shows an inventory rather than a rendered body because applying an
/// MCDF is a scene-ownership transaction — a Penumbra collection, a locked
/// Glamourer state and a redraw, all in the ONE slot
/// <see cref="McdfProgress"/> describes — and none of that may be spent on a
/// highlight.</para>
/// </summary>
public sealed record McdfSummary(
    string FileName,
    string Description,
    int FileCount,
    long DeclaredBytes,
    int SwapCount,
    bool HasAppearance,
    bool HasBodyProfile,
    bool HasManipulations);

/// <summary>
/// Immutable source observation captured by the file boundary. The
/// application carries this through unchanged; it never reopens or validates
/// the source path itself.
/// </summary>
public sealed record McdfExportSourceObservation
{
    public string CanonicalPath { get; }
    public string CanonicalRoot { get; }
    public long Length { get; }
    public string ContentHash { get; }
    public string? Identity { get; }

    public McdfExportSourceObservation(
        string canonicalPath,
        string canonicalRoot,
        long length,
        string contentHash,
        string? identity = null)
    {
        CanonicalPath = canonicalPath ?? throw new ArgumentNullException(nameof(canonicalPath));
        CanonicalRoot = canonicalRoot ?? throw new ArgumentNullException(nameof(canonicalRoot));
        ContentHash = contentHash ?? throw new ArgumentNullException(nameof(contentHash));
        Length = length;
        Identity = identity;
    }
}

/// <summary>One local file to embed on export, with every game path it replaces.</summary>
public sealed record McdfExportFile
{
    public IReadOnlyList<string> GamePaths { get; }
    public string LocalPath { get; }
    public McdfExportSourceObservation? Source { get; }

    public McdfExportFile(
        IReadOnlyList<string> gamePaths,
        string localPath,
        McdfExportSourceObservation? source = null)
    {
        GamePaths = new ReadOnlyCollection<string>(
            new List<string>(gamePaths ?? throw new ArgumentNullException(nameof(gamePaths))));
        LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
        Source = source;
    }

    public void Deconstruct(
        out IReadOnlyList<string> gamePaths,
        out string localPath) =>
        (gamePaths, localPath) = (GamePaths, LocalPath);

    public void Deconstruct(
        out IReadOnlyList<string> gamePaths,
        out string localPath,
        out McdfExportSourceObservation? source) =>
        (gamePaths, localPath, source) = (GamePaths, LocalPath, Source);
}

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
public sealed record McdfExportCandidate
{
    public string ActualPath { get; }
    public IReadOnlyList<string> GamePaths { get; }
    public McdfExportCandidateKind Kind { get; }
    public string? LocalPath { get; }
    public long Length { get; }
    public McdfExportSourceObservation? Source { get; }

    public McdfExportCandidate(
        string actualPath,
        IReadOnlyList<string> gamePaths,
        McdfExportCandidateKind kind,
        string? localPath,
        long length,
        McdfExportSourceObservation? source = null)
    {
        ActualPath = actualPath ?? throw new ArgumentNullException(nameof(actualPath));
        GamePaths = new ReadOnlyCollection<string>(
            new List<string>(gamePaths ?? throw new ArgumentNullException(nameof(gamePaths))));
        Kind = kind;
        LocalPath = localPath;
        Length = length;
        Source = source;
    }
}

/// <summary>Validated export candidates plus deterministic per-resource
/// skips. The application owns only MCDF path semantics after this point.</summary>
public sealed record McdfExportInspection
{
    public IReadOnlyList<McdfExportCandidate> Candidates { get; }
    public IReadOnlyList<string> Skipped { get; }

    public McdfExportInspection(
        IReadOnlyList<McdfExportCandidate> candidates,
        IReadOnlyList<string> skipped)
    {
        Candidates = new ReadOnlyCollection<McdfExportCandidate>(
            new List<McdfExportCandidate>(candidates ?? throw new ArgumentNullException(nameof(candidates))));
        Skipped = new ReadOnlyCollection<string>(
            new List<string>(skipped ?? throw new ArgumentNullException(nameof(skipped))));
    }
}

/// <summary>Everything an MCDF export writes. Capture is complete before
/// writing starts; writing never touches the actor.</summary>
public sealed record McdfExportContent
{
    public string Description { get; }
    public string GlamourerData { get; }
    public string CustomizePlusData { get; }
    public string ManipulationData { get; }
    public IReadOnlyList<McdfExportFile> Files { get; }
    public IReadOnlyDictionary<string, string> Swaps { get; }

    public McdfExportContent(
        string description,
        string glamourerData,
        string customizePlusData,
        string manipulationData,
        IReadOnlyList<McdfExportFile> files,
        IReadOnlyDictionary<string, string> swaps)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        GlamourerData = glamourerData ?? throw new ArgumentNullException(nameof(glamourerData));
        CustomizePlusData = customizePlusData ?? throw new ArgumentNullException(nameof(customizePlusData));
        ManipulationData = manipulationData ?? throw new ArgumentNullException(nameof(manipulationData));
        Files = new ReadOnlyCollection<McdfExportFile>(
            new List<McdfExportFile>(files ?? throw new ArgumentNullException(nameof(files))));
        Swaps = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                swaps ?? throw new ArgumentNullException(nameof(swaps)),
                StringComparer.Ordinal));
    }

    public void Deconstruct(
        out string description,
        out string glamourerData,
        out string customizePlusData,
        out string manipulationData,
        out IReadOnlyList<McdfExportFile> files,
        out IReadOnlyDictionary<string, string> swaps) =>
        (description, glamourerData, customizePlusData, manipulationData, files, swaps) =
        (Description, GlamourerData, CustomizePlusData, ManipulationData, Files, Swaps);
}

public sealed record McdfWriteStats(int Files, long UncompressedBytes);
