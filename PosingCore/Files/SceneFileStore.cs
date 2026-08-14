using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Poser.Files;

public enum SceneStoreFailureKind
{
    Read,
    SizeLimit,
    Json,
    Validation,
    /// <summary>The document is valid JSON but was written by a newer Poser.</summary>
    FutureVersion,
    Serialization,
    TemporaryCreate,
    TemporaryWrite,
    TemporaryFlush,
    TemporaryReopen,
    Replace,
    Move,
    Cleanup,
}

public sealed class SceneStoreFailure
{
    public SceneStoreFailureKind Kind { get; }
    public string Detail { get; }
    public string? Path { get; }
    public SceneFileValidationFailure? ValidationFailure { get; }

    private SceneStoreFailure(
        SceneStoreFailureKind kind,
        string detail,
        string? path,
        SceneFileValidationFailure? validationFailure)
    {
        Kind = kind;
        Detail = detail;
        Path = path;
        ValidationFailure = validationFailure;
    }

    internal static SceneStoreFailure Create(
        SceneStoreFailureKind kind,
        string detail,
        string? path = null,
        SceneFileValidationFailure? validationFailure = null) =>
        new(kind, detail, path, validationFailure);

    internal SceneStoreFailure WithDetail(string detail) =>
        new(Kind, detail, Path, ValidationFailure);
}

public sealed class SceneReadOutcome
{
    public bool Succeeded { get; }
    public SceneFile? Scene { get; }
    public SceneStoreFailure? Failure { get; }

    private SceneReadOutcome(SceneFile? scene, SceneStoreFailure? failure)
    {
        Succeeded = scene is not null;
        Scene = scene;
        Failure = failure;
    }

    internal static SceneReadOutcome Success(SceneFile scene) => new(scene, null);
    internal static SceneReadOutcome Failed(SceneStoreFailure failure) =>
        new(null, failure);
}

/// <summary>Typed status for one scene entry in a listing. The codec
/// validated the complete document before reporting <see cref="Valid"/>.</summary>
public enum SceneEntryStatus
{
    Valid,
    Corrupt,
    Future,
    Oversized,
}

/// <summary>Bounded typed metadata observation for one scene file, for
/// recent-scene listings. Reuses the full codec — a listing can never
/// advertise a scene the load would reject.</summary>
public sealed class SceneMetadataReadOutcome
{
    public SceneEntryStatus Status { get; }
    public bool Succeeded => Status == SceneEntryStatus.Valid;
    public Guid SceneId { get; }

    /// <summary>Who authored the scene, when the document names anyone. Poser's
    /// own capture names nobody, so this is normally absent — it is NOT the
    /// description under another name.</summary>
    public string? Author { get; }

    public string? Description { get; }
    public DateTimeOffset? SavedAt { get; }
    public int ActorCount { get; }
    public int PropCount { get; }
    public int LightCount { get; }
    public int CameraCount { get; }
    public SceneStoreFailure? Failure { get; }

    private SceneMetadataReadOutcome(
        SceneEntryStatus status, SceneFile? scene, SceneStoreFailure? failure)
    {
        Status = status;
        Failure = failure;
        if (scene is null)
            return;
        SceneId = scene.SceneId;
        Author = scene.Author;
        Description = scene.Description;
        SavedAt = scene.SavedAt;
        ActorCount = scene.Actors.Count;
        PropCount = scene.Props.Count;
        LightCount = scene.Lights.Count;
        CameraCount = scene.Cameras.Count;
    }

    internal static SceneMetadataReadOutcome Success(SceneFile scene) =>
        new(SceneEntryStatus.Valid, scene, null);

    internal static SceneMetadataReadOutcome Failed(SceneStoreFailure failure)
    {
        var status = failure.Kind switch
        {
            SceneStoreFailureKind.FutureVersion => SceneEntryStatus.Future,
            SceneStoreFailureKind.SizeLimit => SceneEntryStatus.Oversized,
            _ => SceneEntryStatus.Corrupt,
        };
        return new(status, null, failure);
    }
}

public sealed class SceneWriteOutcome
{
    public bool Succeeded { get; }
    public SceneStoreFailure? Failure { get; }

    /// <summary>Surviving temp/backup files after a failed or uncertain
    /// commit; never empty-handed about bytes that may still matter.</summary>
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    private SceneWriteOutcome(
        bool succeeded,
        SceneStoreFailure? failure,
        IReadOnlyList<string> recoveryEvidencePaths)
    {
        Succeeded = succeeded;
        Failure = failure;
        RecoveryEvidencePaths = recoveryEvidencePaths;
    }

    internal static SceneWriteOutcome Success() =>
        new(true, null, Array.Empty<string>());

    internal static SceneWriteOutcome Failed(
        SceneStoreFailure failure,
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

/// <summary>
/// Typed scene codec and same-directory atomic store — the ordinary pose
/// store's discipline applied to the whole-scene document. Operations are
/// synchronous and stateless; callers must not mutate a scene during
/// <see cref="Write"/>, and concurrent writers get last-successful-writer
/// filesystem semantics. Every read validates the complete bounded document;
/// every write validates and serializes completely, durably flushes a unique
/// same-directory temp, reopens and validates it, replaces/moves atomically
/// with a unique backup for existing destinations, confirms the committed
/// bytes before deleting anything, and reports every surviving temp/backup as
/// recovery evidence.
/// </summary>
public sealed class SceneFileStore
{
    public static SceneFileStore Default { get; } = new();

    private readonly IPoseFileStoreFileSystem _fileSystem;

    public SceneFileStore()
        : this(new SystemPoseFileStoreFileSystem())
    {
    }

    internal SceneFileStore(IPoseFileStoreFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public SceneReadOutcome Read(string path)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            if (stream.Length <= 0)
            {
                return ValidationReadFailure(
                    SceneFileValidationFailure.Create(
                        SceneFileValidationFailureKind.Document,
                        "The scene file is empty."),
                    path);
            }
            if (stream.Length > SceneFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    SceneStoreFailureKind.SizeLimit,
                    $"The scene file is {stream.Length} bytes " +
                    $"(limit {SceneFileLimits.MaxFileBytes}).",
                    path);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return ReadFailure(
                    SceneStoreFailureKind.SizeLimit,
                    "The scene file changed while it was being read.",
                    path);
            }
            return Decode(bytes, path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                SceneStoreFailureKind.Read,
                $"Reading the scene file failed: {ex.Message}",
                path);
        }
    }

    /// <summary>Reads and fully validates the document, returning only its
    /// header metadata and counts, with a typed Valid/Corrupt/Future/Oversized
    /// status for listings.</summary>
    public SceneMetadataReadOutcome ReadMetadata(string path)
    {
        var read = Read(path);
        return read.Succeeded
            ? SceneMetadataReadOutcome.Success(read.Scene!)
            : SceneMetadataReadOutcome.Failed(read.Failure!);
    }

    public SceneReadOutcome Parse(string json)
    {
        if (json is null)
        {
            return ReadFailure(
                SceneStoreFailureKind.Json,
                "The scene JSON is null.");
        }

        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > SceneFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    SceneStoreFailureKind.SizeLimit,
                    $"The scene JSON is {byteCount} bytes " +
                    $"(limit {SceneFileLimits.MaxFileBytes}).");
            }
            return Decode(Encoding.UTF8.GetBytes(json), path: null);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                SceneStoreFailureKind.Json,
                $"Parsing the scene JSON failed: {ex.Message}");
        }
    }

    public SceneWriteOutcome Write(SceneFile scene, string destination)
    {
        var validation = SceneFileValidation.Validate(scene);
        if (!validation.Succeeded)
            return ValidationWriteFailure(validation.Failure!, destination);

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(scene, SceneFile.JsonOptions);
        }
        catch (Exception ex)
        {
            return WriteFailure(
                SceneStoreFailureKind.Serialization,
                $"Serializing the scene failed: {ex.Message}",
                destination);
        }

        if (bytes.LongLength > SceneFileLimits.MaxFileBytes)
        {
            return WriteFailure(
                SceneStoreFailureKind.SizeLimit,
                $"The serialized scene is {bytes.LongLength} bytes " +
                $"(limit {SceneFileLimits.MaxFileBytes}).",
                destination);
        }

        var encoded = Decode(bytes, destination);
        if (!encoded.Succeeded)
        {
            return WriteFailure(
                SceneStoreFailureKind.Serialization,
                $"The serialized scene did not validate: {encoded.Failure!.Detail}",
                destination);
        }

        string fullDestination;
        string temporary;
        string backup;
        try
        {
            fullDestination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(fullDestination)
                ?? throw new IOException("The destination has no parent directory.");
            var fileName = Path.GetFileName(fullDestination);
            temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            backup = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.bak");
        }
        catch (Exception ex)
        {
            return WriteFailure(
                SceneStoreFailureKind.TemporaryCreate,
                $"Preparing the atomic scene paths failed: {ex.Message}",
                destination);
        }

        SceneStoreFailure? failure = null;
        var failureKind = SceneStoreFailureKind.TemporaryCreate;
        try
        {
            failureKind = SceneStoreFailureKind.TemporaryCreate;
            using (var stream = _fileSystem.CreateNew(temporary))
            {
                failureKind = SceneStoreFailureKind.TemporaryWrite;
                stream.Write(bytes);

                failureKind = SceneStoreFailureKind.TemporaryFlush;
                _fileSystem.FlushToDisk(stream);
            }

            failureKind = SceneStoreFailureKind.TemporaryReopen;
            var reopened = Read(temporary);
            if (!reopened.Succeeded)
            {
                failure = SceneStoreFailure.Create(
                    SceneStoreFailureKind.TemporaryReopen,
                    $"Reopening the atomic scene temp failed: {reopened.Failure!.Detail}",
                    temporary);
            }
            else if (_fileSystem.Exists(fullDestination))
            {
                return CommitExisting(bytes, temporary, fullDestination, backup);
            }
            else
            {
                return CommitNew(bytes, temporary, fullDestination);
            }
        }
        catch (Exception ex)
        {
            failure = SceneStoreFailure.Create(
                failureKind,
                $"Atomic scene write failed during {failureKind}: {ex.Message}",
                temporary);
        }

        failure ??= SceneStoreFailure.Create(
            SceneStoreFailureKind.TemporaryReopen,
            "The atomic scene temp was not committed.",
            temporary);
        return CleanupPrecommitFailure(failure, temporary);
    }

    private SceneWriteOutcome CommitExisting(
        byte[] bytes,
        string temporary,
        string destination,
        string backup)
    {
        try
        {
            _fileSystem.Replace(temporary, destination, backup);
            if (!Matches(destination, bytes))
            {
                return UncertainCommitFailure(
                    SceneStoreFailureKind.Replace,
                    "Replace returned without the validated bytes at the destination.",
                    destination,
                    temporary,
                    backup);
            }
            return CleanupConfirmedCommit(bytes, destination, temporary, backup);
        }
        catch (Exception ex)
        {
            if (Matches(destination, bytes))
                return CleanupConfirmedCommit(bytes, destination, temporary, backup);
            return UncertainCommitFailure(
                SceneStoreFailureKind.Replace,
                $"Atomic scene replace failed: {ex.Message}",
                destination,
                temporary,
                backup);
        }
    }

    private SceneWriteOutcome CommitNew(
        byte[] bytes,
        string temporary,
        string destination)
    {
        try
        {
            _fileSystem.Move(temporary, destination);
            if (!Matches(destination, bytes))
            {
                return UncertainCommitFailure(
                    SceneStoreFailureKind.Move,
                    "Move returned without the validated bytes at the destination.",
                    destination,
                    temporary);
            }
            return CleanupConfirmedCommit(bytes, destination, temporary);
        }
        catch (Exception ex)
        {
            if (Matches(destination, bytes))
                return CleanupConfirmedCommit(bytes, destination, temporary);
            return UncertainCommitFailure(
                SceneStoreFailureKind.Move,
                $"Atomic scene move failed: {ex.Message}",
                destination,
                temporary);
        }
    }

    private SceneWriteOutcome CleanupConfirmedCommit(
        ReadOnlySpan<byte> committedBytes,
        string destination,
        string temporary,
        string? backup = null)
    {
        var cleanupErrors = new List<string>();
        try
        {
            _fileSystem.Delete(temporary);
        }
        catch (Exception ex)
        {
            cleanupErrors.Add($"{temporary}: {ex.Message}");
        }

        if (backup is not null)
        {
            if (!Matches(destination, committedBytes))
            {
                cleanupErrors.Add(
                    $"{backup}: destination postcondition changed before backup cleanup");
            }
            else
            {
                try
                {
                    _fileSystem.Delete(backup);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"{backup}: {ex.Message}");
                }
            }
        }

        if (cleanupErrors.Count == 0)
            return SceneWriteOutcome.Success();

        var evidence = backup is null
            ? SurvivingCandidates(temporary)
            : SurvivingCandidates(temporary, backup);
        return SceneWriteOutcome.Failed(
            SceneStoreFailure.Create(
                SceneStoreFailureKind.Cleanup,
                "The scene was committed, but recovery-file cleanup failed: " +
                string.Join("; ", cleanupErrors)),
            evidence);
    }

    private SceneWriteOutcome CleanupPrecommitFailure(
        SceneStoreFailure failure,
        string temporary)
    {
        try
        {
            _fileSystem.Delete(temporary);
        }
        catch (Exception cleanup)
        {
            failure = failure.WithDetail(
                failure.Detail + $" The temp could not be deleted: {cleanup.Message}");
        }

        return SceneWriteOutcome.Failed(failure, SurvivingCandidates(temporary));
    }

    private SceneWriteOutcome UncertainCommitFailure(
        SceneStoreFailureKind kind,
        string detail,
        string destination,
        params string[] recoveryCandidates) =>
        SceneWriteOutcome.Failed(
            SceneStoreFailure.Create(kind, detail, destination),
            SurvivingCandidates(recoveryCandidates));

    private IReadOnlyList<string> SurvivingCandidates(params string[] candidates)
    {
        var surviving = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (Observe(candidate) is not PathObservation.Missing)
                surviving.Add(candidate);
        }
        return surviving;
    }

    private bool Matches(string path, ReadOnlySpan<byte> expected)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            if (stream.Length != expected.Length)
                return false;
            var buffer = new byte[8192];
            var offset = 0;
            while (offset < expected.Length)
            {
                var count = Math.Min(buffer.Length, expected.Length - offset);
                stream.ReadExactly(buffer.AsSpan(0, count));
                if (!buffer.AsSpan(0, count).SequenceEqual(expected.Slice(offset, count)))
                    return false;
                offset += count;
            }
            return stream.ReadByte() == -1;
        }
        catch
        {
            return false;
        }
    }

    private PathObservation Observe(string path)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            return PathObservation.Present;
        }
        catch (FileNotFoundException)
        {
            return PathObservation.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathObservation.Missing;
        }
        catch
        {
            return PathObservation.Unknown;
        }
    }

    private static SceneReadOutcome Decode(ReadOnlySpan<byte> bytes, string? path)
    {
        try
        {
            var scene = JsonSerializer.Deserialize<SceneFile>(
                bytes, SceneFile.JsonOptions);
            var validation = SceneFileValidation.Validate(scene);
            if (!validation.Succeeded)
            {
                return validation.Failure!.Kind ==
                    SceneFileValidationFailureKind.FutureVersion
                    ? SceneReadOutcome.Failed(SceneStoreFailure.Create(
                        SceneStoreFailureKind.FutureVersion,
                        validation.Failure.Detail,
                        path,
                        validation.Failure))
                    : ValidationReadFailure(validation.Failure!, path);
            }
            return SceneReadOutcome.Success(scene!);
        }
        catch (JsonException ex)
        {
            return ReadFailure(
                SceneStoreFailureKind.Json,
                $"The scene JSON is invalid: {ex.Message}",
                path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                SceneStoreFailureKind.Json,
                $"The scene JSON could not be decoded: {ex.Message}",
                path);
        }
    }

    private static SceneReadOutcome ValidationReadFailure(
        SceneFileValidationFailure validation,
        string? path) =>
        SceneReadOutcome.Failed(SceneStoreFailure.Create(
            SceneStoreFailureKind.Validation,
            validation.Detail,
            path,
            validation));

    private static SceneWriteOutcome ValidationWriteFailure(
        SceneFileValidationFailure validation,
        string? path) =>
        SceneWriteOutcome.Failed(SceneStoreFailure.Create(
            SceneStoreFailureKind.Validation,
            validation.Detail,
            path,
            validation));

    private static SceneReadOutcome ReadFailure(
        SceneStoreFailureKind kind,
        string detail,
        string? path = null) =>
        SceneReadOutcome.Failed(SceneStoreFailure.Create(kind, detail, path));

    private static SceneWriteOutcome WriteFailure(
        SceneStoreFailureKind kind,
        string detail,
        string? path = null) =>
        SceneWriteOutcome.Failed(SceneStoreFailure.Create(kind, detail, path));

    private enum PathObservation
    {
        Missing,
        Present,
        Unknown,
    }
}
