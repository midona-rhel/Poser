using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Poser.Documents.Files;

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

    /// <summary>Entities the read left out of <see cref="Scene"/> because
    /// their own data is invalid; the load names each one.</summary>
    public IReadOnlyList<SceneEntityRefusal> Refusals { get; }

    private SceneReadOutcome(
        SceneFile? scene, SceneStoreFailure? failure,
        IReadOnlyList<SceneEntityRefusal> refusals)
    {
        Succeeded = scene is not null;
        Scene = scene;
        Failure = failure;
        Refusals = refusals;
    }

    internal static SceneReadOutcome Success(
        SceneFile scene, IReadOnlyList<SceneEntityRefusal>? refusals = null) =>
        new(scene, null, refusals ?? Array.Empty<SceneEntityRefusal>());
    internal static SceneReadOutcome Failed(SceneStoreFailure failure) =>
        new(null, failure, Array.Empty<SceneEntityRefusal>());
}

/// <summary>A saved appearance opened for reading, or why it could not be
/// opened. The caller closes the stream.</summary>
public sealed record SceneAppearanceOpen(Stream? Stream, string? Error);

/// <summary>Whether a scene file in the library can be loaded.
/// <see cref="Valid"/> means the whole file was read and checked.</summary>
public enum SceneEntryStatus
{
    Valid,
    Corrupt,
    Future,
    Oversized,
}

/// <summary>What the library shows about one scene file. The file gets the
/// same full check as a load, so the library never offers a scene that would
/// fail to load.</summary>
public sealed class SceneMetadataReadOutcome
{
    public SceneEntryStatus Status { get; }
    public bool Succeeded => Status == SceneEntryStatus.Valid;
    public Guid SceneId { get; }

    /// <summary>Who made the scene, if the file names anyone. Poser does not
    /// fill this in when saving, so it is usually absent. It is not the
    /// description.</summary>
    public string? Author { get; }

    public string? Description { get; }
    public DateTimeOffset? SavedAt { get; }

    /// <summary>Where the scene was saved, as the file records it. Absent in
    /// older files; the library groups those by day only.
    /// </summary>
    public string? PlaceName { get; }
    public uint TerritoryId { get; }

    /// <summary>The saved weather's name; empty when the file has no
    /// environment or is older than this field.</summary>
    public string WeatherName { get; } = string.Empty;

    /// <summary>The saved weather's id; 0 when the file has no environment.
    /// When the name is empty, the interface looks the name up from this
    /// id.</summary>
    public uint WeatherId { get; }

    /// <summary>Whether the file records where the camera and the actor
    /// stood when saving. These decide which placement choices are offered
    /// for it.</summary>
    public bool HasCameraAnchor { get; }
    public bool HasActorAnchor { get; }

    public int ActorCount { get; }
    public int PropCount { get; }
    public int LightCount { get; }
    public int CameraCount { get; }
    public int OverlayCount { get; }
    public int WorldObjectCount { get; }
    public int GroupCount { get; }

    /// <summary>Actors whose saved look is an MCDF loaded onto them.</summary>
    public int McdfCount { get; }

    /// <summary>Actors whose scene appearance is saved in the file.</summary>
    public int SceneAppearanceCount { get; }

    /// <summary>The saved time of day in minutes after midnight; null when the scene
    /// saved no environment.</summary>
    public int? MinuteOfDay { get; }

    /// <summary>Whether any actor's animation is saved.</summary>
    public bool AnimationSaved { get; }

    /// <summary>The Poser install that saved the scene; null for older scenes.</summary>
    public Guid? SavedBy { get; }

    /// <summary>The file's format name and version, as read from the file.
    /// The library shows them so the format is visible before loading.</summary>
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
        GroupCount = scene.Groups?.Count ?? 0;
        McdfCount = scene.Actors.Count(scene.HasLoadedMcdf);
        SceneAppearanceCount = scene.Actors.Count(actor => actor.Mcdf is not null && !scene.HasLoadedMcdf(actor));
        MinuteOfDay = scene.Environment?.MinuteOfDay;
        AnimationSaved = scene.Actors.Any(actor => actor.Animation is not null);
        SavedBy = scene.SavedBy;
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

    /// <summary>Temporary and backup files left behind when a save failed or
    /// may not have finished. They are reported so data that may still
    /// matter can be found.</summary>
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
/// Reads and writes scene files. A scene file is a zip archive that holds
/// the scene's JSON and any saved appearances. Every read checks the whole
/// file.
///
/// A save writes a temporary file in the same folder, reads it back to check
/// it, then swaps it into place. A file it replaces is kept as a backup until
/// the new one is confirmed. If something goes wrong, the leftover temporary
/// and backup files are reported. Do not change a scene while
/// <see cref="Write"/> runs; when two saves write the same file, the last one
/// to finish wins.
/// </summary>
public sealed class SceneFileStore
{
    public static SceneFileStore Default { get; } = new();

    private readonly IAtomicFileSystem _fileSystem;

    public SceneFileStore()
        : this(new SystemAtomicFileSystem())
    {
    }

    internal SceneFileStore(IAtomicFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>The entry in the zip that holds the scene's JSON.
    /// </summary>
    public const string DocumentEntry = "scene.json";

    /// <summary>The folder in the zip that holds saved appearances, one
    /// entry per different MCDF, named by its content hash.</summary>
    public const string AppearanceEntryPrefix = "appearance/";

    /// <summary>The entry name for an MCDF with this hash. Two actors with
    /// the same MCDF share one entry instead of storing it twice.</summary>
    public static string AppearanceEntry(string contentHash) =>
        $"{AppearanceEntryPrefix}{contentHash.ToUpperInvariant()}.mcdf";

    /// <summary>
    /// Opens one saved appearance for reading. The caller closes the stream
    /// and copies the data where it needs it. It is never read into memory
    /// here, because a real MCDF can be hundreds of megabytes. When it cannot
    /// be opened, the reason says whether the entry is missing or the scene
    /// file could not be read.
    /// </summary>
    public SceneAppearanceOpen OpenAppearance(string scenePath, string entryName)
    {
        Stream? container = null;
        try
        {
            container = _fileSystem.OpenRead(scenePath);
            var archive = new ZipArchive(container, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry(entryName);
            if (entry is null)
            {
                archive.Dispose();
                return new(null, "the scene holds no such payload.");
            }
            // Closing the stream handed to the caller also closes the scene
            // file.
            return new(new EntryStream(archive, entry.Open()), null);
        }
        catch (Exception ex)
        {
            container?.Dispose();
            return new(null, ex.Message);
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

            // Older .xivl and .xivc files are plain JSON, not zip archives (a
            // zip starts with "PK"). Such a file is read as a light or camera
            // and wrapped as a scene with that one entity. Poser only writes
            // the zip form.
            if (IsLegacyEntryExtension(path))
            {
                int signature = stream.ReadByte();
                stream.Position = 0;
                if (signature != 'P')
                    return DecodeLegacyEntry(stream, path);
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
            // Not a zip archive. Every scene Poser writes is one, so this is
            // not a scene file.
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

    /// <summary>Reads and fully checks the file, and returns only what the
    /// library shows about it, including whether it is valid, damaged, too
    /// new or too large.</summary>
    public SceneMetadataReadOutcome ReadMetadata(string path)
    {
        var read = Read(path);
        return read.Succeeded
            ? SceneMetadataReadOutcome.Success(read.Scene!)
            : SceneMetadataReadOutcome.Failed(read.Failure!);
    }

    public SceneWriteOutcome Write(SceneFile scene, string destination)
    {
        // The file is written in this version's format, whatever version it
        // was read from, so it gets this version's number.
        scene.FileVersion = SceneFile.CurrentVersion;
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

        var written = AtomicFile.Write(_fileSystem, destination, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var document = archive.CreateEntry(DocumentEntry, CompressionLevel.Optimal);
            using (var entry = document.Open())
                entry.Write(bytes);

            if (WriteAppearancePayloads(scene, archive) is { } payloadError)
                throw new AtomicWriteRefusedException(payloadError);
        }, new AtomicWriteOptions
        {
            Subject = "scene",
            KeepTemporaryOnFailure = true,
            VerifyTemporary = temporary => Read(temporary) is { Succeeded: false } reopened
                ? $"Reopening the atomic scene temp failed: {reopened.Failure!.Detail}"
                : null,
        });
        if (written.Succeeded)
            return SceneWriteOutcome.Success();
        return SceneWriteOutcome.Failed(
            SceneStoreFailure.Create(FailureKind(written.Phase!.Value), written.Detail!, written.Path),
            written.RecoveryEvidencePaths);
    }

    internal static SceneStoreFailureKind FailureKind(AtomicWritePhase phase) => phase switch
    {
        AtomicWritePhase.CreateTemporary => SceneStoreFailureKind.TemporaryCreate,
        AtomicWritePhase.WriteTemporary => SceneStoreFailureKind.TemporaryWrite,
        AtomicWritePhase.FlushTemporary => SceneStoreFailureKind.TemporaryFlush,
        AtomicWritePhase.ReopenTemporary => SceneStoreFailureKind.TemporaryReopen,
        AtomicWritePhase.ReplaceDestination => SceneStoreFailureKind.Replace,
        AtomicWritePhase.MoveDestination => SceneStoreFailureKind.Move,
        _ => SceneStoreFailureKind.Cleanup,
    };

    /// <summary>
    /// Copies every saved appearance into the zip, piece by piece, one entry
    /// per different MCDF: two actors with the same file share one entry.
    ///
    /// <para>Entries are stored without compression. An MCDF is already
    /// compressed, so compressing it again takes minutes and saves nothing.
    /// Returns null on success, or what went wrong.</para>
    /// </summary>
    private string? WriteAppearancePayloads(
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
                using var reading = _fileSystem.OpenRead(source);
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

    private static SceneReadOutcome Decode(ReadOnlySpan<byte> bytes, string? path)
    {
        try
        {
            return Validated(JsonSerializer.Deserialize<SceneFile>(
                Utf8Bom.Strip(bytes), SceneFile.JsonOptions), path);
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

    private static SceneReadOutcome Validated(SceneFile? scene, string? path)
    {
        var validation = SceneFileValidation.ValidateForLoad(scene, out var refusals);
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
        return SceneReadOutcome.Success(scene!, refusals);
    }

    private static bool IsLegacyEntryExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(SceneFile.LightEntryExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(SceneFile.CameraEntryExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wraps an old plain-JSON light or camera file as a scene with
    /// that one entity. The camera loads as a new live camera, not the
    /// default one, as older Poser versions loaded it.</summary>
    private static SceneReadOutcome DecodeLegacyEntry(Stream stream, string path)
    {
        if (stream.Length > SceneFileLimits.MaxDocumentBytes)
        {
            return ReadFailure(
                SceneStoreFailureKind.SizeLimit,
                $"The document is {stream.Length} bytes " +
                $"(limit {SceneFileLimits.MaxDocumentBytes}).",
                path);
        }
        try
        {
            var scene = new SceneFile { SceneId = Guid.NewGuid() };
            if (Path.GetExtension(path).Equals(
                    SceneFile.LightEntryExtension, StringComparison.OrdinalIgnoreCase))
            {
                var light = JsonSerializer.Deserialize<LightFile>(stream, SceneFile.JsonOptions);
                scene.CameraAnchor = light?.CameraAnchor;
                scene.ActorAnchor = light?.ActorAnchor;
                scene.Lights.Add(new SceneLight { Key = Guid.NewGuid(), Light = light });
            }
            else
            {
                var camera = JsonSerializer.Deserialize<CameraFile>(stream, SceneFile.JsonOptions);
                scene.CameraAnchor = camera?.CameraAnchor;
                scene.ActorAnchor = camera?.ActorAnchor;
                scene.Cameras.Add(new SceneCamera
                {
                    Key = Guid.NewGuid(), Camera = camera, IsLive = true,
                });
            }
            return Validated(scene, path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                SceneStoreFailureKind.Json,
                $"The document JSON is invalid: {ex.Message}",
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
}
