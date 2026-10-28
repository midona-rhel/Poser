using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Poser.Domain.Identity;

namespace Poser.Domain.Integration;

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
    // Appended so Debug telemetry keeps every existing serialized enum value.
    Cancelling,
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
/// Progress displayed for the current or last MCDF operation.
/// A null Outcome means the operation is still running.
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

/// <summary>
/// Progress update from reading, extracting, or writing a package.
/// </summary>
public readonly record struct McdfProgressStep(
    McdfPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>
/// Extraction folder and ownership marker used to check that cleanup deletes
/// the folder created for this operation, rather than an unrelated replacement.
/// </summary>
public sealed record McdfOperationDirectory(
    string Path,
    string OwnerToken,
    string? Identity,
    string? MarkerIdentity);

/// <summary>
/// Contents of an extracted MCDF package. ReplacedGamePaths maps game paths
/// to extracted files; SwappedGamePaths maps one game path to another.
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
/// Header information for the Library preview. Reading it does not extract files
/// or apply appearance to an actor. MCDF headers do not contain a thumbnail.
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
/// File path, size, MCDF payload hash and optional filesystem identity captured
/// when the export checks its files. The writer puts the hash in the package
/// header and checks the path, size and identity again before copying the file.
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
/// An inspected file replacement or game-path swap considered for export.
/// This record stores the inspection result; its constructor does not check the filesystem.
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

/// <summary>
/// Inspected export candidates and explanations for resources already skipped.
/// The application still decides which game paths belong in the package.
/// </summary>
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

/// <summary>
/// Captured appearance, file references, and swaps supplied to the MCDF writer.
/// The writer can use these without reading the actor again.
/// </summary>
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
