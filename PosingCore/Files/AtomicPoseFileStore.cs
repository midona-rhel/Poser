using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Poser.Files.Converters;

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
    public string? Version { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool HasThumbnail { get; }
    public PoseFileStoreFailure? Failure { get; }

    private PoseFileMetadataReadOutcome(
        string? author,
        string? version,
        IReadOnlyList<string> tags,
        bool hasThumbnail,
        PoseFileStoreFailure? failure)
    {
        Succeeded = failure is null;
        Author = author;
        Version = version;
        Tags = tags;
        HasThumbnail = hasThumbnail;
        Failure = failure;
    }

    internal static PoseFileMetadataReadOutcome Success(PoseFile pose) =>
        Success(pose, !string.IsNullOrEmpty(pose.Base64Image));

    internal static PoseFileMetadataReadOutcome Success(
        PoseFile pose,
        bool hasThumbnail) =>
        new(
            pose.Author,
            pose.Version,
            Array.AsReadOnly((pose.Tags ?? []).ToArray()),
            hasThumbnail,
            null);

    internal static PoseFileMetadataReadOutcome Failed(PoseFileStoreFailure failure) =>
        new(null, null, Array.Empty<string>(), false, failure);
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
    internal const int MetadataBufferSize = 64 * 1024;

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

    /// <summary>
    /// Reads and fully validates a bounded pose document, returning only its
    /// metadata. This is the shared seam for indexing and thumbnail probes;
    /// it deliberately reuses the ordinary pose codec's limits and typed
    /// validation rather than maintaining a second JSON contract.
    /// </summary>
    public PoseFileMetadataReadOutcome ReadMetadata(string path)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            if (stream.Length <= 0)
            {
                return PoseFileMetadataReadOutcome.Failed(
                    ValidationReadFailure(
                        PoseFileValidationFailure.Create(
                            PoseFileValidationFailureKind.Document,
                            "The pose file is empty."),
                        path).Failure!);
            }
            if (stream.Length > PoseFileLimits.MaxFileBytes)
            {
                return PoseFileMetadataReadOutcome.Failed(
                    PoseFileStoreFailure.Create(
                        PoseFileStoreFailureKind.SizeLimit,
                        $"The pose file is {stream.Length} bytes " +
                        $"(limit {PoseFileLimits.MaxFileBytes}).",
                        path));
            }

            return DecodeMetadata(stream, stream.Length, path);
        }
        catch (Exception ex)
        {
            return PoseFileMetadataReadOutcome.Failed(
                PoseFileStoreFailure.Create(
                    PoseFileStoreFailureKind.Read,
                    $"Reading the pose metadata failed: {ex.Message}",
                    path));
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

    private PoseFileMetadataReadOutcome DecodeMetadata(
        Stream stream,
        long expectedLength,
        string path)
    {
        try
        {
            var projection = new StreamingMetadataProjection(stream).Read();
            if (projection.BytesRead != expectedLength)
            {
                return PoseFileMetadataReadOutcome.Failed(
                    PoseFileStoreFailure.Create(
                        PoseFileStoreFailureKind.SizeLimit,
                        "The pose file changed while it was being read.",
                        path));
            }
            var validation = PoseFileValidation.Validate(projection.Pose);
            if (!validation.Succeeded)
            {
                return PoseFileMetadataReadOutcome.Failed(
                    ValidationReadFailure(validation.Failure!, path).Failure!);
            }

            return PoseFileMetadataReadOutcome.Success(
                projection.Pose,
                projection.HasThumbnail);
        }
        catch (MetadataValidationException ex)
        {
            return PoseFileMetadataReadOutcome.Failed(
                ValidationReadFailure(ex.Failure, path).Failure!);
        }
        catch (InvalidDataException ex)
        {
            return PoseFileMetadataReadOutcome.Failed(
                PoseFileStoreFailure.Create(
                    PoseFileStoreFailureKind.SizeLimit,
                    ex.Message,
                    path));
        }
        catch (JsonException ex)
        {
            return PoseFileMetadataReadOutcome.Failed(
                PoseFileStoreFailure.Create(
                    PoseFileStoreFailureKind.Json,
                    $"The pose JSON is invalid: {ex.Message}",
                    path));
        }
        catch (Exception ex)
        {
            return PoseFileMetadataReadOutcome.Failed(
                PoseFileStoreFailure.Create(
                    PoseFileStoreFailureKind.Json,
                    $"The pose JSON could not be decoded: {ex.Message}",
                    path));
        }
    }

    private sealed class MetadataValidationException : Exception
    {
        public MetadataValidationException(PoseFileValidationFailure failure)
            : base(failure.Detail)
        {
            Failure = failure;
        }

        public PoseFileValidationFailure Failure { get; }
    }

    private sealed class StreamingMetadataProjection
    {
        // The metadata path is intentionally a forward-only projection:
        // retaining the document or its image string would recreate the
        // allocation that indexing is meant to avoid.
        private readonly Stream _stream;
        private readonly byte[] _buffer =
            ArrayPool<byte>.Shared.Rent(MetadataBufferSize);
        private int _offset;
        private int _count;
        private int _peeked = -2;
        private long _totalBytes;
        private long _collectionEntries;
        private readonly PoseFile _pose = new();
        private bool _hasThumbnail;

        public StreamingMetadataProjection(Stream stream) => _stream = stream;

        public MetadataProjection Read()
        {
            try
            {
                SkipWhitespace();
                ReadObject(ParseTopProperty, 1);
                SkipWhitespace();
                if (Peek() != -1)
                    throw new JsonException("Trailing content follows the pose document.");
                return new MetadataProjection(_pose, _hasThumbnail, _totalBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }

        private void ParseTopProperty(StringValue property)
        {
            switch (property.Text)
            {
                case nameof(PoseFile.TypeName):
                    ParseNullableStringLength();
                    break;
                case nameof(PoseFile.Author):
                    _pose.Author = ParseNullableString();
                    break;
                case nameof(PoseFile.Description):
                    ParseNullableStringLength();
                    break;
                case nameof(PoseFile.Version):
                    _pose.Version = ParseNullableString();
                    break;
                case nameof(PoseFile.Base64Image):
                    _hasThumbnail = ParseNullableStringLength() > 0;
                    break;
                case nameof(PoseFile.Tags):
                    ParseTags(2);
                    break;
                case nameof(PoseFile.ModelDifference):
                    _pose.ModelDifference = ParseBoneData(2);
                    break;
                case nameof(PoseFile.ModelAbsoluteValues):
                    _pose.ModelAbsoluteValues = ParseBoneData(2);
                    break;
                case nameof(PoseFile.Bones):
                    ParseCollection(nameof(PoseFile.Bones), 2);
                    break;
                case nameof(PoseFile.MainHand):
                    ParseCollection(nameof(PoseFile.MainHand), 2);
                    break;
                case nameof(PoseFile.OffHand):
                    ParseCollection(nameof(PoseFile.OffHand), 2);
                    break;
                case nameof(PoseFile.Prop):
                    ParseCollection(nameof(PoseFile.Prop), 2);
                    break;
                case nameof(PoseFile.Ornament):
                    ParseCollection(nameof(PoseFile.Ornament), 2);
                    break;
                case nameof(PoseFile.Position):
                    _pose.Position = ParseVector3();
                    break;
                case nameof(PoseFile.Rotation):
                    _pose.Rotation = ParseQuaternion();
                    break;
                case nameof(PoseFile.Scale):
                    _pose.Scale = ParseVector3();
                    break;
                default:
                    ParseValue(2);
                    break;
            }
        }

        private void ParseTags(int depth)
        {
            if (Peek() == 'n')
            {
                ReadLiteral("null");
                _pose.Tags = null;
                return;
            }

            _pose.Tags = [];
            ReadArray(index =>
            {
                if (index >= PoseFileLimits.MaxTags)
                {
                    throw Validation(
                        PoseFileValidationFailureKind.TagCount,
                        $"Tags contains more than {PoseFileLimits.MaxTags} raw entries.");
                }

                if (Peek() == '"')
                {
                    var tag = ReadString(true, PoseFileLimits.MaxTagCharacters + 1);
                    EnsureTagLength(tag, index);
                    if (tag.Text is not null)
                        _pose.Tags.Add(tag.Text);
                }
                else if (Peek() == '{')
                {
                    ParseTagObject(index, depth + 1);
                }
                else if (Peek() == 'n')
                {
                    ReadLiteral("null");
                }
                else
                {
                    ParseValue(depth + 1);
                }
            }, depth);
        }

        private void ParseTagObject(int tagIndex, int depth)
        {
            string? name = null;
            string? displayName = null;
            ReadObject(property =>
            {
                if (string.Equals(property.Text, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (Peek() == '"')
                    {
                        var value = ReadString(true, PoseFileLimits.MaxTagCharacters + 1);
                        EnsureTagLength(value, tagIndex);
                        name = value.Text;
                    }
                    else
                    {
                        ParseValue(depth + 1);
                    }
                }
                else if (string.Equals(property.Text, "DisplayName", StringComparison.OrdinalIgnoreCase))
                {
                    if (Peek() == '"')
                    {
                        var value = ReadString(true, PoseFileLimits.MaxTagCharacters + 1);
                        EnsureTagLength(value, tagIndex);
                        displayName = value.Text;
                    }
                    else
                    {
                        ParseValue(depth + 1);
                    }
                }
                else
                {
                    ParseValue(depth + 1);
                }
            }, depth);
            if (name is not null || displayName is not null)
                _pose.Tags!.Add(name ?? displayName!);
        }

        private readonly Dictionary<string, string> _boneAliases = new();

        private void ParseCollection(string collectionName, int depth)
        {
            if (Peek() == 'n')
                throw Validation(PoseFileValidationFailureKind.Document, $"{collectionName} must be an object.");

            ReadObject(property =>
            {
                if (property.CharacterCount > PoseFileLimits.MaxBoneNameCharacters)
                {
                    throw Validation(
                        PoseFileValidationFailureKind.BoneName,
                        $"{collectionName} bone name exceeds " +
                        $"{PoseFileLimits.MaxBoneNameCharacters} characters.");
                }

                _collectionEntries++;
                if (_collectionEntries > PoseFileLimits.MaxTotalEntries)
                {
                    throw Validation(
                        PoseFileValidationFailureKind.TotalEntries,
                        "Pose collections contain more than " +
                        $"{PoseFileLimits.MaxTotalEntries} raw entries.");
                }

                if (++_currentCollectionEntries > PoseFileLimits.MaxEntriesPerCollection)
                {
                    throw Validation(
                        PoseFileValidationFailureKind.CollectionSize,
                        $"{collectionName} contains more than " +
                        $"{PoseFileLimits.MaxEntriesPerCollection} raw entries.");
                }

                var bone = ParseBoneData(depth + 1);
                if (property.Text is not null)
                {
                    ValidateBoneData(collectionName, property.Text, bone);
                    if (collectionName == nameof(PoseFile.Bones))
                    {
                        var target = AnamnesisBoneNameConverter.ToGame(property.Text);
                        if (_boneAliases.TryGetValue(target, out var previous) &&
                            !string.Equals(previous, property.Text, StringComparison.Ordinal))
                        {
                            throw Validation(
                                PoseFileValidationFailureKind.AliasCollision,
                                $"Bones '{previous}' and '{property.Text}' both map to '{target}'.");
                        }
                        _boneAliases[target] = property.Text;
                    }
                }
            }, depth, before: () => _currentCollectionEntries = 0);
        }

        private int _currentCollectionEntries;

        private PoseFile.BoneData ParseBoneData(int depth)
        {
            if (Peek() == 'n')
                throw Validation(
                    PoseFileValidationFailureKind.Document,
                    "A pose transform has no value.");

            var bone = new PoseFile.BoneData();
            ReadObject(property =>
            {
                switch (property.Text)
                {
                    case nameof(PoseFile.BoneData.Position):
                        bone.Position = ParseVector3();
                        break;
                    case nameof(PoseFile.BoneData.Rotation):
                        bone.Rotation = ParseQuaternion();
                        break;
                    case nameof(PoseFile.BoneData.Scale):
                        bone.Scale = ParseVector3();
                        break;
                    default:
                        ParseValue(depth + 1);
                        break;
                }
            }, depth);
            return bone;
        }

        private static void ValidateBoneData(
            string collectionName,
            string boneName,
            PoseFile.BoneData bone)
        {
            if (!float.IsFinite(bone.Position.X) ||
                !float.IsFinite(bone.Position.Y) ||
                !float.IsFinite(bone.Position.Z) ||
                !float.IsFinite(bone.Rotation.X) ||
                !float.IsFinite(bone.Rotation.Y) ||
                !float.IsFinite(bone.Rotation.Z) ||
                !float.IsFinite(bone.Rotation.W) ||
                !float.IsFinite(bone.Scale.X) ||
                !float.IsFinite(bone.Scale.Y) ||
                !float.IsFinite(bone.Scale.Z))
            {
                throw Validation(
                    PoseFileValidationFailureKind.NonFiniteNumeric,
                    $"{collectionName} '{boneName}' contains NaN or infinity.");
            }

            if (bone.Rotation.LengthSquared() < PoseFileLimits.MinQuaternionLengthSquared)
            {
                throw Validation(
                    PoseFileValidationFailureKind.DegenerateQuaternion,
                    $"{collectionName} '{boneName}' rotation is degenerate.");
            }
        }

        private Vector3 ParseVector3()
        {
            var values = ParseNumericComponents(3, nameof(Vector3));
            return new Vector3(values[0], values[1], values[2]);
        }

        private Quaternion ParseQuaternion()
        {
            var values = ParseNumericComponents(4, nameof(Quaternion));
            return new Quaternion(values[0], values[1], values[2], values[3]);
        }

        private float[] ParseNumericComponents(int count, string typeName)
        {
            if (Peek() != '"')
                throw new JsonException($"{typeName} must be a string.");
            var parser = new NumericComponentsParser(count, typeName);
            ReadString(false, -1, parser.Append);
            return parser.Complete();
        }

        private string? ParseNullableString()
        {
            if (Peek() == 'n')
            {
                ReadLiteral("null");
                return null;
            }
            if (Peek() != '"')
                throw new JsonException(
                    "A pose string property must be a string or null.");
            return ReadString(true, -1).Text;
        }

        private int ParseNullableStringLength()
        {
            if (Peek() == 'n')
            {
                ReadLiteral("null");
                return 0;
            }
            if (Peek() != '"')
                throw new JsonException(
                    "A pose string property must be a string or null.");
            return ReadString(false, -1).CharacterCount;
        }

        private void ParseValue(int depth)
        {
            switch (Peek())
            {
                case '{':
                    ReadObject(_ => ParseValue(depth + 1), depth);
                    break;
                case '[':
                    ReadArray(_ => ParseValue(depth + 1), depth);
                    break;
                case '"':
                    ReadString(false, -1);
                    break;
                case 't':
                    ReadLiteral("true");
                    break;
                case 'f':
                    ReadLiteral("false");
                    break;
                case 'n':
                    ReadLiteral("null");
                    break;
                default:
                    ParseNumber();
                    break;
            }
        }

        private void ReadObject(
            Action<StringValue> propertyHandler,
            int depth,
            Action? before = null)
        {
            EnsureDepth(depth);
            Expect('{');
            before?.Invoke();
            if (Peek() == '}')
            {
                ReadByte();
                return;
            }

            while (true)
            {
                SkipWhitespace();
                var property = ReadString(true, 512);
                Expect(':');
                SkipWhitespace();
                propertyHandler(property);
                SkipWhitespace();
                var next = Peek();
                if (next == '}')
                {
                    ReadByte();
                    return;
                }
                if (next != ',')
                    throw new JsonException("A JSON object requires commas between properties.");
                ReadByte();
                if (Peek() == '}')
                {
                    ReadByte();
                    return;
                }
            }
        }

        private void ReadArray(Action<int> itemHandler, int depth)
        {
            EnsureDepth(depth);
            Expect('[');
            if (Peek() == ']')
            {
                ReadByte();
                return;
            }

            var index = 0;
            while (true)
            {
                SkipWhitespace();
                itemHandler(index++);
                SkipWhitespace();
                var next = Peek();
                if (next == ']')
                {
                    ReadByte();
                    return;
                }
                if (next != ',')
                    throw new JsonException("A JSON array requires commas between values.");
                ReadByte();
                if (Peek() == ']')
                {
                    ReadByte();
                    return;
                }
            }
        }

        private StringValue ReadString(
            bool capture,
            int maxCapture,
            Action<char>? onCharacter = null)
        {
            Expect('"');
            var builder = capture ? new StringBuilder() : null;
            var characterCount = 0;
            var truncated = false;
            while (true)
            {
                var value = ReadRequired();
                if (value == '"')
                    return new StringValue(
                        truncated ? null : builder?.ToString(),
                        characterCount);
                if (value < 0x20)
                    throw new JsonException("A JSON string contains a control character.");

                if (value == '\\')
                {
                    var escaped = ReadRequired();
                    if (escaped == 'u')
                    {
                        var codeUnit = 0;
                        for (var i = 0; i < 4; i++)
                        {
                            var hex = ReadRequired();
                            var digit = UriHexValue(hex);
                            if (digit < 0)
                                throw new JsonException("A JSON unicode escape is invalid.");
                            codeUnit = (codeUnit << 4) | digit;
                        }
                        Append(
                            codeUnit,
                            builder,
                            maxCapture,
                            ref characterCount,
                            ref truncated,
                            onCharacter);
                        continue;
                    }

                    var escapedCharacter = escaped switch
                    {
                        '"' or '\\' or '/' => escaped,
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw new JsonException("A JSON string escape is invalid."),
                    };
                    Append(
                        escapedCharacter,
                        builder,
                        maxCapture,
                        ref characterCount,
                        ref truncated,
                        onCharacter);
                    continue;
                }

                if (value < 0x80)
                {
                    Append(
                        value,
                        builder,
                        maxCapture,
                        ref characterCount,
                        ref truncated,
                        onCharacter);
                    continue;
                }

                var scalar = ReadUtf8Scalar(value);
                var chars = scalar > 0xffff
                    ? char.ConvertFromUtf32(scalar)
                    : ((char)scalar).ToString();
                characterCount += chars.Length;
                if (onCharacter is not null)
                    foreach (var character in chars)
                        onCharacter(character);
                if (builder is not null && !truncated)
                {
                    if (maxCapture >= 0 && builder.Length + chars.Length > maxCapture)
                        truncated = true;
                    else
                        builder.Append(chars);
                }
            }
        }

        private static void Append(
            int value,
            StringBuilder? builder,
            int maxCapture,
            ref int characterCount,
            ref bool truncated,
            Action<char>? onCharacter)
        {
            characterCount++;
            onCharacter?.Invoke((char)value);
            if (builder is null || truncated)
                return;
            if (maxCapture >= 0 && builder.Length + 1 > maxCapture)
            {
                truncated = true;
                return;
            }
            builder.Append((char)value);
        }

        private void ParseNumber()
        {
            if (Peek() == '-')
                ReadByte();
            if (Peek() == '0')
                ReadByte();
            else
            {
                RequireDigit();
                while (IsDigit(Peek()))
                    ReadByte();
            }
            if (Peek() == '.')
            {
                ReadByte();
                RequireDigit();
                while (IsDigit(Peek()))
                    ReadByte();
            }
            if (Peek() is 'e' or 'E')
            {
                ReadByte();
                if (Peek() is '+' or '-')
                    ReadByte();
                RequireDigit();
                while (IsDigit(Peek()))
                    ReadByte();
            }
        }

        private void ReadLiteral(string literal)
        {
            foreach (var expected in literal)
                if (ReadRequired() != expected)
                    throw new JsonException("A JSON literal is invalid.");
        }

        private void SkipWhitespace()
        {
            while (Peek() is 0x20 or 0x09 or 0x0a or 0x0d)
                ReadByte();
        }

        private void Expect(int expected)
        {
            SkipWhitespace();
            if (ReadRequired() != expected)
                throw new JsonException($"Expected JSON character '{(char)expected}'.");
        }

        private int Peek()
        {
            if (_peeked == -2)
                _peeked = ReadRaw();
            return _peeked;
        }

        private int ReadByte()
        {
            var value = Peek();
            _peeked = -2;
            return value;
        }

        private int ReadRequired()
        {
            var value = ReadByte();
            if (value < 0)
                throw new JsonException("The pose JSON ended unexpectedly.");
            return value;
        }

        private int ReadRaw()
        {
            if (_offset == _count)
            {
                _count = _stream.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_count == 0)
                    return -1;
                _totalBytes += _count;
                if (_totalBytes > PoseFileLimits.MaxFileBytes)
                    throw new InvalidDataException("The pose file exceeded its size limit while being read.");
            }
            return _buffer[_offset++];
        }

        private int ReadUtf8Scalar(int first)
        {
            var length = first switch
            {
                >= 0xc2 and <= 0xdf => 1,
                >= 0xe0 and <= 0xef => 2,
                >= 0xf0 and <= 0xf4 => 3,
                _ => throw new JsonException("A JSON string contains invalid UTF-8."),
            };
            var scalar = first & ((1 << (7 - length)) - 1);
            for (var i = 0; i < length; i++)
            {
                var next = ReadRequired();
                if (next is < 0x80 or > 0xbf)
                    throw new JsonException("A JSON string contains invalid UTF-8.");
                scalar = (scalar << 6) | (next & 0x3f);
            }
            if ((length == 2 && scalar < 0x800) ||
                (length == 3 && scalar < 0x10000) ||
                scalar is > 0x10ffff or >= 0xd800 and <= 0xdfff)
            {
                throw new JsonException("A JSON string contains invalid UTF-8.");
            }
            return scalar;
        }

        private static int UriHexValue(int value) =>
            value is >= '0' and <= '9' ? value - '0' :
            value is >= 'a' and <= 'f' ? value - 'a' + 10 :
            value is >= 'A' and <= 'F' ? value - 'A' + 10 :
            -1;

        private static bool IsDigit(int value) => value is >= '0' and <= '9';

        private void RequireDigit()
        {
            if (!IsDigit(Peek()))
                throw new JsonException("A JSON number is invalid.");
        }

        private static void EnsureDepth(int depth)
        {
            if (depth > PoseFileLimits.MaxJsonDepth)
                throw new JsonException(
                    $"The pose JSON exceeds depth {PoseFileLimits.MaxJsonDepth}.");
        }

        private static void EnsureTagLength(StringValue value, int tagIndex)
        {
            if (value.CharacterCount > PoseFileLimits.MaxTagCharacters)
                throw Validation(
                    PoseFileValidationFailureKind.TagLength,
                    $"Tag {tagIndex + 1} exceeds {PoseFileLimits.MaxTagCharacters} characters.");
        }

        private static MetadataValidationException Validation(
            PoseFileValidationFailureKind kind,
            string detail) =>
            new(PoseFileValidationFailure.Create(kind, detail));

        private readonly record struct StringValue(string? Text, int CharacterCount);
    }

    private readonly record struct MetadataProjection(
        PoseFile Pose,
        bool HasThumbnail,
        long BytesRead);

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
