using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
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

    /// <summary>Where the scene was captured, as the document recorded it.
    /// Absent on a file written before scenes recorded it — a listing groups
    /// those by their day alone rather than inventing a place for them.
    /// </summary>
    public string? PlaceName { get; }
    public uint TerritoryId { get; }

    /// <summary>The captured weather's display name; empty when the document
    /// records no environment or predates the field.</summary>
    public string WeatherName { get; } = string.Empty;

    /// <summary>The captured weather's id; 0 when the document records no
    /// environment. A viewer with the weather sheet resolves this when the
    /// name above is empty.</summary>
    public uint WeatherId { get; }

    /// <summary>Which placement anchors the document records — what decides
    /// the placement choices a viewer may offer for it.</summary>
    public bool HasCameraAnchor { get; }
    public bool HasActorAnchor { get; }

    public int ActorCount { get; }
    public int PropCount { get; }
    public int LightCount { get; }
    public int CameraCount { get; }
    public int OverlayCount { get; }
    public int WorldObjectCount { get; }

    /// <summary>The document's own format name and version, read back rather
    /// than assumed. A viewer states them beside the file so a scene's format
    /// identity is visible BEFORE a load, which is the point of a versioned
    /// format.</summary>
    public string? TypeName { get; }

    public int FileVersion { get; }

    public SceneStoreFailure? Failure { get; }

    private SceneMetadataReadOutcome(
        SceneEntryStatus status, SceneFile? scene, SceneStoreFailure? failure)
    {
        Status = status;
        Failure = failure;
        if (scene is null)
            return;
        SceneId = scene.SceneId;
        TypeName = scene.TypeName;
        FileVersion = scene.FileVersion;
        Author = scene.Author;
        Description = scene.Description;
        SavedAt = scene.SavedAt;
        PlaceName = scene.PlaceName;
        TerritoryId = scene.TerritoryId;
        WeatherName = scene.Environment?.WeatherName ?? string.Empty;
        WeatherId = scene.Environment?.WeatherId ?? 0;
        HasCameraAnchor = scene.CameraAnchor is not null;
        HasActorAnchor = scene.ActorAnchor is not null;
        ActorCount = scene.Actors.Count;
        PropCount = scene.Props.Count;
        LightCount = scene.Lights.Count;
        CameraCount = scene.Cameras.Count;
        OverlayCount = scene.Overlays?.Count ?? 0;
        WorldObjectCount = scene.WorldObjects?.Count ?? 0;
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

    /// <summary>The container entry holding the scene document itself.
    /// </summary>
    public const string DocumentEntry = "scene.json";

    /// <summary>The container folder holding appearance payloads, one entry
    /// per distinct package, named by its content hash.</summary>
    public const string AppearanceEntryPrefix = "appearance/";

    /// <summary>The entry name a payload with this digest is stored under.
    /// Content-addressed, so two actors wearing the same package share one
    /// entry instead of writing it twice.</summary>
    public static string AppearanceEntry(string contentHash) =>
        $"{AppearanceEntryPrefix}{contentHash.ToUpperInvariant()}.mcdf";

    /// <summary>
    /// Opens ONE appearance payload as a stream. The caller owns the returned
    /// stream and copies it wherever it needs the bytes — they are never
    /// materialized here, because a real package is hundreds of megabytes.
    /// Null when the container has no such entry.
    /// </summary>
    public Stream? OpenAppearance(string scenePath, string entryName)
    {
        try
        {
            var archive = ZipFile.OpenRead(scenePath);
            var entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                archive.Dispose();
                return null;
            }
            // The entry stream owns the archive: disposing what the caller was
            // handed closes the container behind it.
            return new EntryStream(archive, entry.Open());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>An entry stream that closes its container when the caller is
    /// done with it.</summary>
    private sealed class EntryStream(ZipArchive archive, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                archive.Dispose();
            }
            base.Dispose(disposing);
        }
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

            using var archive = new ZipArchive(
                stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.GetEntry(DocumentEntry) is not { } document)
            {
                return ValidationReadFailure(
                    SceneFileValidationFailure.Create(
                        SceneFileValidationFailureKind.Document,
                        "The scene file holds no scene document."),
                    path);
            }
            if (document.Length > SceneFileLimits.MaxDocumentBytes)
            {
                return ReadFailure(
                    SceneStoreFailureKind.SizeLimit,
                    $"The scene document is {document.Length} bytes " +
                    $"(limit {SceneFileLimits.MaxDocumentBytes}).",
                    path);
            }

            var bytes = new byte[(int)document.Length];
            using (var entry = document.Open())
                entry.ReadExactly(bytes);
            return Decode(bytes, path);
        }
        catch (InvalidDataException)
        {
            // Not a container at all. Every scene Poser has ever written is
            // one, so this is simply not a scene file.
            return ValidationReadFailure(
                SceneFileValidationFailure.Create(
                    SceneFileValidationFailureKind.Document,
                    "The scene file is not a Poser scene container."),
                path);
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
            if (byteCount > SceneFileLimits.MaxDocumentBytes)
            {
                return ReadFailure(
                    SceneStoreFailureKind.SizeLimit,
                    $"The scene JSON is {byteCount} bytes " +
                    $"(limit {SceneFileLimits.MaxDocumentBytes}).");
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

        if (bytes.LongLength > SceneFileLimits.MaxDocumentBytes)
        {
            return WriteFailure(
                SceneStoreFailureKind.SizeLimit,
                $"The serialized scene document is {bytes.LongLength} bytes " +
                $"(limit {SceneFileLimits.MaxDocumentBytes}).",
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
                using (var archive = new ZipArchive(
                    stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var document = archive.CreateEntry(
                        DocumentEntry, CompressionLevel.Optimal);
                    using (var entry = document.Open())
                        entry.Write(bytes);

                    // Payloads are STREAMED in, one entry per distinct
                    // package, stored rather than compressed: an MCDF is
                    // already LZ4 inside, so deflating it costs minutes and
                    // saves nothing.
                    if (WriteAppearancePayloads(scene, archive) is { } payloadError)
                    {
                        failure = SceneStoreFailure.Create(
                            SceneStoreFailureKind.TemporaryWrite,
                            payloadError,
                            temporary);
                    }
                }

                if (failure is null)
                {
                    failureKind = SceneStoreFailureKind.TemporaryFlush;
                    _fileSystem.FlushToDisk(stream);
                }
            }

            if (failure is null)
            {
                failureKind = SceneStoreFailureKind.TemporaryReopen;
                var reopened = Read(temporary);
                if (!reopened.Succeeded)
                {
                    failure = SceneStoreFailure.Create(
                        SceneStoreFailureKind.TemporaryReopen,
                        $"Reopening the atomic scene temp failed: {reopened.Failure!.Detail}",
                        temporary);
                }
                else if (StampOf(temporary) is not { } stamp)
                {
                    failure = SceneStoreFailure.Create(
                        SceneStoreFailureKind.TemporaryReopen,
                        "The atomic scene temp could not be checksummed.",
                        temporary);
                }
                else if (_fileSystem.Exists(fullDestination))
                {
                    return CommitExisting(stamp, temporary, fullDestination, backup);
                }
                else
                {
                    return CommitNew(stamp, temporary, fullDestination);
                }
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
        FileStamp stamp,
        string temporary,
        string destination,
        string backup)
    {
        try
        {
            _fileSystem.Replace(temporary, destination, backup);
            if (!Matches(destination, stamp))
            {
                return UncertainCommitFailure(
                    SceneStoreFailureKind.Replace,
                    "Replace returned without the validated bytes at the destination.",
                    destination,
                    temporary,
                    backup);
            }
            return CleanupConfirmedCommit(stamp, destination, temporary, backup);
        }
        catch (Exception ex)
        {
            if (Matches(destination, stamp))
                return CleanupConfirmedCommit(stamp, destination, temporary, backup);
            return UncertainCommitFailure(
                SceneStoreFailureKind.Replace,
                $"Atomic scene replace failed: {ex.Message}",
                destination,
                temporary,
                backup);
        }
    }

    private SceneWriteOutcome CommitNew(
        FileStamp stamp,
        string temporary,
        string destination)
    {
        try
        {
            _fileSystem.Move(temporary, destination);
            if (!Matches(destination, stamp))
            {
                return UncertainCommitFailure(
                    SceneStoreFailureKind.Move,
                    "Move returned without the validated bytes at the destination.",
                    destination,
                    temporary);
            }
            return CleanupConfirmedCommit(stamp, destination, temporary);
        }
        catch (Exception ex)
        {
            if (Matches(destination, stamp))
                return CleanupConfirmedCommit(stamp, destination, temporary);
            return UncertainCommitFailure(
                SceneStoreFailureKind.Move,
                $"Atomic scene move failed: {ex.Message}",
                destination,
                temporary);
        }
    }

    private SceneWriteOutcome CleanupConfirmedCommit(
        FileStamp stamp,
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
            if (!Matches(destination, stamp))
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

    /// <summary>
    /// Streams every portable payload into the container, one entry per
    /// DISTINCT package: two actors wearing the same file share one entry, so
    /// a scene never writes the same half-gigabyte twice.
    ///
    /// <para>Stored, not deflated — an MCDF is already compressed, so
    /// deflating it costs minutes of CPU for no bytes. Returns null on
    /// success, else what went wrong.</para>
    /// </summary>
    private static string? WriteAppearancePayloads(
        SceneFile scene, ZipArchive archive)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in scene.Actors)
        {
            if (actor?.Mcdf is not { IsPortable: true } payload)
                continue;
            if (!written.Add(payload.PackageEntry!))
                continue;
            if (payload.PackageSourcePath is not { Length: > 0 } source)
            {
                return $"Actor '{actor.Name}' states an appearance payload " +
                    "with nothing to read it from.";
            }
            try
            {
                var entry = archive.CreateEntry(
                    payload.PackageEntry!, CompressionLevel.NoCompression);
                using var target = entry.Open();
                using var reading = File.OpenRead(source);
                reading.CopyTo(target);
            }
            catch (Exception ex)
            {
                return $"Actor '{actor.Name}''s appearance payload could not " +
                    $"be written into the scene: {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>
    /// What a committed file must still be. A scene now carries payload
    /// entries that run to hundreds of megabytes, so the commit postcondition
    /// is a STREAMED length-and-digest comparison rather than a byte image
    /// held in memory — same guarantee, constant cost.
    /// </summary>
    private readonly record struct FileStamp(long Length, string Digest);

    private FileStamp? StampOf(string path)
    {
        try
        {
            using var stream = _fileSystem.OpenRead(path);
            long length = stream.Length;
            var digest = System.Security.Cryptography.SHA256.HashData(stream);
            return new FileStamp(length, Convert.ToHexString(digest));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool Matches(string path, FileStamp expected) =>
        StampOf(path) is { } actual &&
        actual.Length == expected.Length &&
        string.Equals(actual.Digest, expected.Digest, StringComparison.Ordinal);

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
