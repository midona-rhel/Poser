using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Poser.Files;

public enum PoseFileStoreFailureKind
{
    Read,
    SizeLimit,
    Json,
    Validation,
    Serialization,
    TemporaryCreate,
    TemporaryWrite,
    TemporaryFlush,
    TemporaryReopen,
    Replace,
    Move,
    Cleanup,
}

public sealed class PoseFileStoreFailure
{
    public PoseFileStoreFailureKind Kind { get; }
    public string Detail { get; }
    public string? Path { get; }
    public PoseFileValidationFailure? ValidationFailure { get; }

    private PoseFileStoreFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path,
        PoseFileValidationFailure? validationFailure)
    {
        Kind = kind;
        Detail = detail;
        Path = path;
        ValidationFailure = validationFailure;
    }

    internal static PoseFileStoreFailure Create(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path = null,
        PoseFileValidationFailure? validationFailure = null) =>
        new(kind, detail, path, validationFailure);

    internal PoseFileStoreFailure WithDetail(string detail) =>
        new(Kind, detail, Path, ValidationFailure);
}

public sealed class PoseFileReadOutcome
{
    public bool Succeeded { get; }
    public PoseFile? Pose { get; }
    public PoseFileStoreFailure? Failure { get; }

    private PoseFileReadOutcome(PoseFile? pose, PoseFileStoreFailure? failure)
    {
        Succeeded = pose is not null;
        Pose = pose;
        Failure = failure;
    }

    internal static PoseFileReadOutcome Success(PoseFile pose) => new(pose, null);
    internal static PoseFileReadOutcome Failed(PoseFileStoreFailure failure) => new(null, failure);
}

/// <summary>
/// Bounded, typed metadata observation for a <c>.pose</c> file. The codec
/// validates the complete document before exposing these header values, so a
/// library index cannot advertise metadata from a file that import would
/// reject.
/// </summary>
public sealed class PoseFileMetadataReadOutcome
{
    public bool Succeeded { get; }
    public string? Author { get; }

    /// <summary>The author's free-text pose version (Anamnesis/Brio
    /// metadata), not a format version.</summary>
    public string? Version { get; }

    /// <summary>The document's format version (<see cref="PoseFile.FileVersion"/>).</summary>
    public int FileVersion { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool HasThumbnail { get; }

    /// <summary>Where the document says it was captured, or null when it
    /// records no place.</summary>
    public string? PlaceName { get; }

    public PoseFileStoreFailure? Failure { get; }

    private PoseFileMetadataReadOutcome(
        string? author,
        string? version,
        int fileVersion,
        IReadOnlyList<string> tags,
        bool hasThumbnail,
        string? placeName,
        PoseFileStoreFailure? failure)
    {
        Succeeded = failure is null;
        Author = author;
        Version = version;
        FileVersion = fileVersion;
        Tags = tags;
        HasThumbnail = hasThumbnail;
        PlaceName = placeName;
        Failure = failure;
    }

    internal static PoseFileMetadataReadOutcome Success(PoseFile pose) =>
        new(
            pose.Author,
            pose.Version,
            pose.FileVersion,
            Array.AsReadOnly((pose.Tags ?? []).ToArray()),
            !string.IsNullOrEmpty(pose.Base64Image),
            pose.PlaceName,
            null);

    internal static PoseFileMetadataReadOutcome Failed(PoseFileStoreFailure failure) =>
        new(null, null, 0, Array.Empty<string>(), false, null, failure);
}

public sealed class PoseFileWriteOutcome
{
    public bool Succeeded { get; }
    public PoseFileStoreFailure? Failure { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    private PoseFileWriteOutcome(
        bool succeeded,
        PoseFileStoreFailure? failure,
        IReadOnlyList<string> recoveryEvidencePaths)
    {
        Succeeded = succeeded;
        Failure = failure;
        RecoveryEvidencePaths = recoveryEvidencePaths;
    }

    internal static PoseFileWriteOutcome Success() =>
        new(true, null, Array.Empty<string>());

    internal static PoseFileWriteOutcome Failed(
        PoseFileStoreFailure failure,
        IEnumerable<string>? recoveryEvidencePaths = null) =>
        new(
            false,
            failure,
            recoveryEvidencePaths is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(recoveryEvidencePaths
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
}

internal enum PoseFileStorePhase
{
    Serialize,
    CreateTemporary,
    WriteTemporary,
    FlushTemporary,
    ReopenTemporary,
    ReplaceDestination,
    MoveDestination,
    CleanupTemporary,
    CleanupBackup,
}

/// <summary>
/// Typed ordinary-pose codec and same-directory atomic store. Operations are
/// synchronous and stateless. Callers must not mutate a pose during
/// <see cref="Write"/>; concurrent writes use last-successful-writer filesystem
/// semantics. Destination and parent paths are trusted inputs, and reparse
/// points follow operating-system behavior rather than a containment guarantee.
/// The optional seams are internal, for persistence tests.
/// </summary>
public sealed class AtomicPoseFileStore
{
    public static AtomicPoseFileStore Default { get; } = new();

    private readonly IAtomicFileSystem _fileSystem;
    private readonly Action<PoseFileStorePhase, string?>? _beforePhase;

    public AtomicPoseFileStore()
        : this(new SystemAtomicFileSystem(), null)
    {
    }

    internal AtomicPoseFileStore(Action<PoseFileStorePhase, string?> beforePhase)
        : this(new SystemAtomicFileSystem(), beforePhase)
    {
    }

    internal AtomicPoseFileStore(
        IAtomicFileSystem fileSystem,
        Action<PoseFileStorePhase, string?>? beforePhase = null)
    {
        _fileSystem = fileSystem;
        _beforePhase = beforePhase;
    }

    public PoseFileReadOutcome Read(string path) =>
        ReadFile(path, PoseFile.JsonOptions);

    /// <summary>
    /// Reads and fully validates a bounded pose document, returning only its
    /// metadata. This is the shared seam for indexing and thumbnail probes;
    /// it reuses the ordinary codec's limits and typed validation rather than
    /// maintaining a second JSON contract. Only the thumbnail is not
    /// materialized (<see cref="PoseFile.MetadataJsonOptions"/>).
    /// </summary>
    public PoseFileMetadataReadOutcome ReadMetadata(string path)
    {
        var read = ReadFile(path, PoseFile.MetadataJsonOptions);
        return read.Succeeded
            ? PoseFileMetadataReadOutcome.Success(read.Pose!)
            : PoseFileMetadataReadOutcome.Failed(read.Failure!);
    }

    private PoseFileReadOutcome ReadFile(string path, JsonSerializerOptions options)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            if (stream.Length <= 0)
            {
                return ValidationReadFailure(
                    PoseFileValidationFailure.Create(
                        PoseFileValidationFailureKind.Document,
                        "The pose file is empty."),
                    path);
            }
            if (stream.Length > PoseFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    $"The pose file is {stream.Length} bytes " +
                    $"(limit {PoseFileLimits.MaxFileBytes}).",
                    path);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    "The pose file changed while it was being read.",
                    path);
            }
            return Decode(bytes, path, options);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Read,
                $"Reading the pose file failed: {ex.Message}",
                path);
        }
    }

    public PoseFileReadOutcome Parse(string json)
    {
        if (json is null)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                "The pose JSON is null.");
        }

        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > PoseFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    $"The pose JSON is {byteCount} bytes " +
                    $"(limit {PoseFileLimits.MaxFileBytes}).");
            }
            return Decode(Encoding.UTF8.GetBytes(json), path: null, PoseFile.JsonOptions);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"Parsing the pose JSON failed: {ex.Message}");
        }
    }

    public PoseFileWriteOutcome Write(PoseFile pose, string destination)
    {
        var validation = PoseFileValidation.Validate(pose);
        if (!validation.Succeeded)
            return ValidationWriteFailure(validation.Failure!, destination);

        byte[] bytes;
        try
        {
            Before(PoseFileStorePhase.Serialize, destination);
            bytes = JsonSerializer.SerializeToUtf8Bytes(pose, PoseFile.JsonOptions);
        }
        catch (Exception ex)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Serialization,
                $"Serializing the pose failed: {ex.Message}",
                destination);
        }

        if (bytes.LongLength > PoseFileLimits.MaxFileBytes)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.SizeLimit,
                $"The serialized pose is {bytes.LongLength} bytes " +
                $"(limit {PoseFileLimits.MaxFileBytes}).",
                destination);
        }

        var encoded = Decode(bytes, destination, PoseFile.JsonOptions);
        if (!encoded.Succeeded)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Serialization,
                $"The serialized pose did not validate: {encoded.Failure!.Detail}",
                destination);
        }

        var written = AtomicFile.Write(_fileSystem, destination, bytes, new AtomicWriteOptions
        {
            Subject = "pose",
            KeepTemporaryOnFailure = true,
            VerifyTemporary = temporary => Read(temporary) is { Succeeded: false } reopened
                ? $"Reopening the atomic pose temp failed: {reopened.Failure!.Detail}"
                : null,
            BeforePhase = _beforePhase is null
                ? null
                : (phase, path) => _beforePhase(StorePhase(phase), path),
        });
        if (written.Succeeded)
            return PoseFileWriteOutcome.Success();
        return PoseFileWriteOutcome.Failed(
            PoseFileStoreFailure.Create(FailureKind(written.Phase!.Value), written.Detail!, written.Path),
            written.RecoveryEvidencePaths);
    }

    private static PoseFileStoreFailureKind FailureKind(AtomicWritePhase phase) => phase switch
    {
        AtomicWritePhase.CreateTemporary => PoseFileStoreFailureKind.TemporaryCreate,
        AtomicWritePhase.WriteTemporary => PoseFileStoreFailureKind.TemporaryWrite,
        AtomicWritePhase.FlushTemporary => PoseFileStoreFailureKind.TemporaryFlush,
        AtomicWritePhase.ReopenTemporary => PoseFileStoreFailureKind.TemporaryReopen,
        AtomicWritePhase.ReplaceDestination => PoseFileStoreFailureKind.Replace,
        AtomicWritePhase.MoveDestination => PoseFileStoreFailureKind.Move,
        _ => PoseFileStoreFailureKind.Cleanup,
    };

    private static PoseFileStorePhase StorePhase(AtomicWritePhase phase) => phase switch
    {
        AtomicWritePhase.CreateTemporary => PoseFileStorePhase.CreateTemporary,
        AtomicWritePhase.WriteTemporary => PoseFileStorePhase.WriteTemporary,
        AtomicWritePhase.FlushTemporary => PoseFileStorePhase.FlushTemporary,
        AtomicWritePhase.ReopenTemporary => PoseFileStorePhase.ReopenTemporary,
        AtomicWritePhase.ReplaceDestination => PoseFileStorePhase.ReplaceDestination,
        AtomicWritePhase.MoveDestination => PoseFileStorePhase.MoveDestination,
        AtomicWritePhase.CleanupTemporary => PoseFileStorePhase.CleanupTemporary,
        _ => PoseFileStorePhase.CleanupBackup,
    };

    private static PoseFileReadOutcome Decode(
        ReadOnlySpan<byte> bytes,
        string? path,
        JsonSerializerOptions options)
    {
        try
        {
            bytes = Utf8Bom.Strip(bytes);
            var preflight = PoseFileValidation.Preflight(bytes);
            if (!preflight.Succeeded)
                return ValidationReadFailure(preflight.Failure!, path);

            var pose = JsonSerializer.Deserialize<PoseFile>(bytes, options);
            var validation = PoseFileValidation.Validate(pose);
            if (!validation.Succeeded)
                return ValidationReadFailure(validation.Failure!, path);
            return PoseFileReadOutcome.Success(pose!);
        }
        catch (JsonException ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"The pose JSON is invalid: {ex.Message}",
                path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"The pose JSON could not be decoded: {ex.Message}",
                path);
        }
    }

    private void Before(PoseFileStorePhase phase, string? path) =>
        _beforePhase?.Invoke(phase, path);

    private static PoseFileReadOutcome ValidationReadFailure(
        PoseFileValidationFailure validation,
        string? path) =>
        PoseFileReadOutcome.Failed(PoseFileStoreFailure.Create(
            PoseFileStoreFailureKind.Validation,
            validation.Detail,
            path,
            validation));

    private static PoseFileWriteOutcome ValidationWriteFailure(
        PoseFileValidationFailure validation,
        string? path) =>
        PoseFileWriteOutcome.Failed(PoseFileStoreFailure.Create(
            PoseFileStoreFailureKind.Validation,
            validation.Detail,
            path,
            validation));

    private static PoseFileReadOutcome ReadFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path = null) =>
        PoseFileReadOutcome.Failed(PoseFileStoreFailure.Create(kind, detail, path));

    private static PoseFileWriteOutcome WriteFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path = null) =>
        PoseFileWriteOutcome.Failed(PoseFileStoreFailure.Create(kind, detail, path));
}
