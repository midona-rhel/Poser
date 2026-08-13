using System;
using System.Collections.Generic;
using System.IO;
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

internal interface IPoseFileStoreFileSystem
{
    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void FlushToDisk(Stream stream);
    bool Exists(string path);
    void Replace(string source, string destination, string backup);
    void Move(string source, string destination);
    void Delete(string path);
}

internal sealed class SystemPoseFileStoreFileSystem : IPoseFileStoreFileSystem
{
    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.SequentialScan);

    public Stream CreateNew(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.SequentialScan);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public bool Exists(string path) => File.Exists(path);
    public void Replace(string source, string destination, string backup) =>
        File.Replace(source, destination, backup);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// Typed ordinary-pose codec and same-directory atomic store. Operations are
/// synchronous and stateless. Callers must not mutate a pose during
/// <see cref="Write"/>; concurrent writes use last-successful-writer filesystem
/// semantics. Destination and parent paths are trusted inputs, and reparse
/// points follow operating-system behavior rather than a containment guarantee.
/// The optional seams are internal and feature-specific for persistence tests.
/// </summary>
public sealed class AtomicPoseFileStore
{
    public static AtomicPoseFileStore Default { get; } = new();

    private readonly IPoseFileStoreFileSystem _fileSystem;
    private readonly Action<PoseFileStorePhase, string?>? _beforePhase;

    public AtomicPoseFileStore()
        : this(new SystemPoseFileStoreFileSystem(), null)
    {
    }

    internal AtomicPoseFileStore(Action<PoseFileStorePhase, string?> beforePhase)
        : this(new SystemPoseFileStoreFileSystem(), beforePhase)
    {
    }

    internal AtomicPoseFileStore(
        IPoseFileStoreFileSystem fileSystem,
        Action<PoseFileStorePhase, string?>? beforePhase = null)
    {
        _fileSystem = fileSystem;
        _beforePhase = beforePhase;
    }

    public PoseFileReadOutcome Read(string path)
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
            return Decode(bytes, path);
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
            return Decode(Encoding.UTF8.GetBytes(json), path: null);
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

        var encoded = Decode(bytes, destination);
        if (!encoded.Succeeded)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Serialization,
                $"The serialized pose did not validate: {encoded.Failure!.Detail}",
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
                PoseFileStoreFailureKind.TemporaryCreate,
                $"Preparing the atomic pose paths failed: {ex.Message}",
                destination);
        }

        PoseFileStoreFailure? failure = null;
        var failureKind = PoseFileStoreFailureKind.TemporaryCreate;
        try
        {
            failureKind = PoseFileStoreFailureKind.TemporaryCreate;
            Before(PoseFileStorePhase.CreateTemporary, temporary);
            using (var stream = _fileSystem.CreateNew(temporary))
            {
                failureKind = PoseFileStoreFailureKind.TemporaryWrite;
                Before(PoseFileStorePhase.WriteTemporary, temporary);
                stream.Write(bytes);

                failureKind = PoseFileStoreFailureKind.TemporaryFlush;
                Before(PoseFileStorePhase.FlushTemporary, temporary);
                _fileSystem.FlushToDisk(stream);
            }

            failureKind = PoseFileStoreFailureKind.TemporaryReopen;
            Before(PoseFileStorePhase.ReopenTemporary, temporary);
            var reopened = Read(temporary);
            if (!reopened.Succeeded)
            {
                failure = PoseFileStoreFailure.Create(
                    PoseFileStoreFailureKind.TemporaryReopen,
                    $"Reopening the atomic pose temp failed: {reopened.Failure!.Detail}",
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
            failure = PoseFileStoreFailure.Create(
                failureKind,
                $"Atomic pose write failed during {failureKind}: {ex.Message}",
                temporary);
        }

        failure ??= PoseFileStoreFailure.Create(
            PoseFileStoreFailureKind.TemporaryReopen,
            "The atomic pose temp was not committed.",
            temporary);
        return CleanupPrecommitFailure(failure, temporary);
    }

    private PoseFileWriteOutcome CommitExisting(
        byte[] bytes,
        string temporary,
        string destination,
        string backup)
    {
        try
        {
            Before(PoseFileStorePhase.ReplaceDestination, destination);
            _fileSystem.Replace(temporary, destination, backup);
            if (!Matches(destination, bytes))
            {
                return UncertainCommitFailure(
                    PoseFileStoreFailureKind.Replace,
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
                PoseFileStoreFailureKind.Replace,
                $"Atomic pose replace failed: {ex.Message}",
                destination,
                temporary,
                backup);
        }
    }

    private PoseFileWriteOutcome CommitNew(
        byte[] bytes,
        string temporary,
        string destination)
    {
        try
        {
            Before(PoseFileStorePhase.MoveDestination, destination);
            _fileSystem.Move(temporary, destination);
            if (!Matches(destination, bytes))
            {
                return UncertainCommitFailure(
                    PoseFileStoreFailureKind.Move,
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
                PoseFileStoreFailureKind.Move,
                $"Atomic pose move failed: {ex.Message}",
                destination,
                temporary);
        }
    }

    private PoseFileWriteOutcome CleanupConfirmedCommit(
        ReadOnlySpan<byte> committedBytes,
        string destination,
        string temporary,
        string? backup = null)
    {
        var cleanupErrors = new List<string>();
        try
        {
            Before(PoseFileStorePhase.CleanupTemporary, temporary);
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
                    Before(PoseFileStorePhase.CleanupBackup, backup);
                    _fileSystem.Delete(backup);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"{backup}: {ex.Message}");
                }
            }
        }

        if (cleanupErrors.Count == 0)
            return PoseFileWriteOutcome.Success();

        var evidence = backup is null
            ? SurvivingCandidates(temporary)
            : SurvivingCandidates(temporary, backup);
        return PoseFileWriteOutcome.Failed(
            PoseFileStoreFailure.Create(
                PoseFileStoreFailureKind.Cleanup,
                "The pose was committed, but recovery-file cleanup failed: " +
                string.Join("; ", cleanupErrors)),
            evidence);
    }

    private PoseFileWriteOutcome CleanupPrecommitFailure(
        PoseFileStoreFailure failure,
        string temporary)
    {
        try
        {
            Before(PoseFileStorePhase.CleanupTemporary, temporary);
            _fileSystem.Delete(temporary);
        }
        catch (Exception cleanup)
        {
            failure = failure.WithDetail(
                failure.Detail + $" The temp could not be deleted: {cleanup.Message}");
        }

        return PoseFileWriteOutcome.Failed(failure, SurvivingCandidates(temporary));
    }

    private PoseFileWriteOutcome UncertainCommitFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string destination,
        params string[] recoveryCandidates) =>
        PoseFileWriteOutcome.Failed(
            PoseFileStoreFailure.Create(kind, detail, destination),
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

    private PoseFileReadOutcome Decode(ReadOnlySpan<byte> bytes, string? path)
    {
        try
        {
            var preflight = PoseFileValidation.Preflight(bytes);
            if (!preflight.Succeeded)
                return ValidationReadFailure(preflight.Failure!, path);

            var pose = JsonSerializer.Deserialize<PoseFile>(bytes, PoseFile.JsonOptions);
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

    private enum PathObservation
    {
        Missing,
        Present,
        Unknown,
    }
}
