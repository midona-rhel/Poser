using System;
using System.Collections.Generic;
using System.IO;
using Poser.Files;

namespace Poser.Library;

/// <summary>What a library file action was asked to do.</summary>
public enum PoseLibraryFileActionKind : byte
{
    Rename,
    Move,
    Delete,
    Quarantine,
    Restore,
    Probe,
    EditMetadata,
}

/// <summary>
/// What a metadata edit does to a pose's stored preview image. The default is
/// "leave it alone", so a caller that shows no image control cannot erase one
/// by omission — the same rule the description follows.
/// </summary>
public readonly record struct PosePreviewImageEdit(bool Remove, string? Base64)
{
    /// <summary>Leave whatever the file already carries.</summary>
    public static PosePreviewImageEdit Keep => default;

    /// <summary>Drop the stored image.</summary>
    public static PosePreviewImageEdit Cleared => new(true, null);

    /// <summary>Store this base64 image, adding or replacing.</summary>
    public static PosePreviewImageEdit Set(string base64) => new(false, base64);
}

/// <summary>
/// The typed answer of one library file action. Every action answers one of
/// these — a refusal carries its reason in <see cref="Detail"/> and never a
/// thrown exception, because the browser states the outcome on its own status
/// line rather than tearing down the frame.
/// </summary>
public sealed class PoseLibraryFileActionResult
{
    public required PoseLibraryFileActionKind Kind { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>Where the file lives after a successful rename, move,
    /// quarantine, or restore; null for the other kinds and for refusals.
    /// </summary>
    public string? ResultPath { get; init; }

    /// <summary>The refusal reason, or a short success note. Never null.
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Probe only: the re-read's typed metadata status.</summary>
    public PoseLibraryMetadataStatus? ProbeStatus { get; init; }
}

/// <summary>
/// Synchronous, stateless disk actions on individual library files: the
/// recovery verbs (quarantine, restore, retry-probe, delete), the authoring
/// verbs (rename, move, edit metadata), each with a typed result. Nothing here
/// mutates a published snapshot — the immutable-complete-pass contract stays
/// with <see cref="PoseLibraryService"/>; a caller acts on disk and then
/// requests a rescan.
///
/// <para>Metadata editing goes through the atomic pose store's own bounded
/// read and same-directory atomic write, so a corrupt, future, or oversized
/// file is REFUSED rather than being partially rewritten, a rewritten file
/// keeps every root member Poser does not model, and a successful edit leaves
/// a file the codec fully validated.</para>
/// </summary>
public sealed class PoseLibraryFileActions
{
    /// <summary>The per-directory folder quarantined files move into. The
    /// library scan skips folders of this name, which is what takes a
    /// quarantined file out of the browser without deleting the evidence.
    /// </summary>
    public const string QuarantineFolderName = ".quarantine";

    /// <summary>Collision suffix attempts before a quarantine/restore gives
    /// up; matches the auto-save collision convention (" (2)", " (3)"…).
    /// </summary>
    private const int MaxCollisionSuffix = 100;

    public static PoseLibraryFileActions Default { get; } = new();

    private readonly AtomicPoseFileStore _store;
    private readonly SceneFileStore _sceneStore;

    public PoseLibraryFileActions()
        : this(AtomicPoseFileStore.Default)
    {
    }

    internal PoseLibraryFileActions(
        AtomicPoseFileStore store, SceneFileStore? sceneStore = null)
    {
        _store = store;
        _sceneStore = sceneStore ?? SceneFileStore.Default;
    }

    /// <summary>Maps a metadata read to the library's typed entry status —
    /// the ONE mapping the scan and the retry probe both answer with.
    /// </summary>
    public static (PoseLibraryMetadataStatus Status, string Detail) Classify(
        PoseFileMetadataReadOutcome metadata)
    {
        if (metadata.Succeeded)
        {
            return string.IsNullOrWhiteSpace(metadata.Version)
                ? (PoseLibraryMetadataStatus.Valid, string.Empty)
                : (PoseLibraryMetadataStatus.Future,
                    $"Pose version '{metadata.Version}' is not supported.");
        }

        return (
            metadata.Failure?.Kind == PoseFileStoreFailureKind.SizeLimit
                ? PoseLibraryMetadataStatus.Oversized
                : PoseLibraryMetadataStatus.Corrupt,
            metadata.Failure?.Detail ?? "The pose metadata could not be read.");
    }

    /// <summary>The same mapping for a SCENE, which is a different document
    /// read by a different codec — the ONE mapping the scan and the retry
    /// probe both answer an <c>.xivs</c> scene with.</summary>
    public static (PoseLibraryMetadataStatus Status, string Detail) Classify(
        SceneMetadataReadOutcome metadata)
    {
        if (metadata.Succeeded)
            return (PoseLibraryMetadataStatus.Valid, string.Empty);

        var status = metadata.Status switch
        {
            SceneEntryStatus.Future => PoseLibraryMetadataStatus.Future,
            SceneEntryStatus.Oversized => PoseLibraryMetadataStatus.Oversized,
            _ => PoseLibraryMetadataStatus.Corrupt,
        };
        return (status, metadata.Failure?.Detail ?? "The scene could not be read.");
    }

    /// <summary>Whether the path names a whole scene rather than a pose. The
    /// two are different documents with different codecs, so every read the
    /// library performs has to pick one.</summary>
    private static bool IsScene(string path) =>
        Path.GetExtension(path).Equals(
            SceneFile.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Renames the file in place, keeping its extension. The new
    /// name must be a bare file name; an empty, invalid, or taken name is
    /// refused and the source stays untouched.</summary>
    public PoseLibraryFileActionResult Rename(string path, string newName)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Rename;
        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return Refused(kind, "A name is required.");
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Refused(kind, "The name contains characters a file cannot carry.");

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                return Refused(kind, "The file has no parent folder.");
            if (!File.Exists(path))
                return Refused(kind, "The file no longer exists.");

            var destination = Path.Combine(
                directory, trimmed + Path.GetExtension(path));
            if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
                return Succeeded(kind, path);
            if (File.Exists(destination))
                return Refused(kind, "That name already exists here.");

            File.Move(path, destination);
            return Succeeded(kind, destination);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Renaming failed: {ex.Message}");
        }
    }

    /// <summary>Moves the file into another existing folder under its own
    /// name. A missing destination folder or a taken name is refused and the
    /// source stays untouched.</summary>
    public PoseLibraryFileActionResult Move(string path, string destinationDirectory)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Move;
        try
        {
            if (!File.Exists(path))
                return Refused(kind, "The file no longer exists.");
            if (!Directory.Exists(destinationDirectory))
                return Refused(kind, "The destination folder no longer exists.");

            var destination = Path.Combine(
                destinationDirectory, Path.GetFileName(path));
            if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase))
                return Succeeded(kind, path);
            if (File.Exists(destination))
                return Refused(kind, "A file of that name already exists there.");

            File.Move(path, destination);
            return Succeeded(kind, destination);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Moving failed: {ex.Message}");
        }
    }

    /// <summary>Deletes the file. A file already gone is success — the wanted
    /// postcondition holds either way.</summary>
    public PoseLibraryFileActionResult Delete(string path)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Delete;
        try
        {
            File.Delete(path);
            return Succeeded(kind, resultPath: null);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Deleting failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves the file into its directory's <see cref="QuarantineFolderName"/>
    /// folder, out of the scan's sight but preserved as evidence. A name
    /// collision takes a numeric suffix rather than overwriting what an
    /// earlier quarantine put there.
    /// </summary>
    public PoseLibraryFileActionResult Quarantine(string path)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Quarantine;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                return Refused(kind, "The file has no parent folder.");
            if (!File.Exists(path))
                return Refused(kind, "The file no longer exists.");

            var quarantine = Path.Combine(directory, QuarantineFolderName);
            Directory.CreateDirectory(quarantine);
            if (UniqueDestination(quarantine, Path.GetFileName(path))
                is not { } destination)
                return Refused(kind, "The quarantine folder is full of that name.");

            File.Move(path, destination);
            return Succeeded(kind, destination);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Quarantining failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Moves a quarantined file back beside its quarantine folder. Only a
    /// file inside a <see cref="QuarantineFolderName"/> folder qualifies; a
    /// name the library has since re-taken gets a numeric suffix so the
    /// restore never overwrites a live file.
    /// </summary>
    public PoseLibraryFileActionResult Restore(string quarantinedPath)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Restore;
        try
        {
            var quarantine = Path.GetDirectoryName(quarantinedPath);
            if (quarantine is null
                || !string.Equals(
                    Path.GetFileName(quarantine),
                    QuarantineFolderName,
                    StringComparison.OrdinalIgnoreCase))
                return Refused(kind, "The file is not in a quarantine folder.");
            var home = Path.GetDirectoryName(quarantine);
            if (string.IsNullOrEmpty(home))
                return Refused(kind, "The quarantine folder has no parent.");
            if (!File.Exists(quarantinedPath))
                return Refused(kind, "The quarantined file no longer exists.");

            if (UniqueDestination(home, Path.GetFileName(quarantinedPath))
                is not { } destination)
                return Refused(kind, "The library folder is full of that name.");

            File.Move(quarantinedPath, destination);
            return Succeeded(kind, destination);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Restoring failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-reads the file's metadata through the same bounded streaming seam
    /// the scan uses and answers its CURRENT typed status — the "retry" of a
    /// corrupt entry whose write may since have completed. The probe itself
    /// succeeds whenever the file could be classified; the status says what
    /// it is.
    ///
    /// <para>The retry answers exactly what the next SCAN would answer, which
    /// means it must read each kind through that kind's own codec: a scene
    /// re-read with the pose codec would answer Corrupt however healthy it is,
    /// which is the one answer a retry must never invent.</para>
    /// </summary>
    public PoseLibraryFileActionResult Probe(string path)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.Probe;
        try
        {
            var (status, detail) = IsScene(path)
                ? Classify(_sceneStore.ReadMetadata(path))
                : Classify(_store.ReadMetadata(path));
            return new PoseLibraryFileActionResult
            {
                Kind = kind,
                Succeeded = true,
                Detail = detail,
                ProbeStatus = status,
            };
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Probing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites the file's author and tags through the atomic store: bounded
    /// full read, mutate, validate, same-directory atomic replace. Tags are
    /// trimmed, blanks dropped, and duplicates folded case-insensitively; an
    /// empty author or tag set clears the member.
    ///
    /// <para>This is Poser's only read-modify-WRITE-IN-PLACE on a file it did
    /// not author, so it is bound by two rules the other write paths never
    /// need. Root members Poser does not model survive the rewrite verbatim
    /// (<c>PoseFile.UnmappedMembers</c>) — a Brio document keeps everything
    /// Brio consumes. And a document the codec cannot fully account for is
    /// never rewritten: an oversized or malformed file is refused by the read,
    /// and a <see cref="PoseLibraryMetadataStatus.Future"/> document — one
    /// declaring a version Poser has already said it does not support — is
    /// refused here even though it parses, because "parses" is not
    /// "understood".</para>
    /// </summary>
    /// <param name="description">The pose's own description. NULL leaves the
    /// stored value alone — a caller that does not show the field must not
    /// clear it — and an empty string clears it, exactly like the author.
    /// </param>
    /// <param name="image">What to do with the stored preview image; the
    /// default leaves it untouched.</param>
    public PoseLibraryFileActionResult EditMetadata(
        string path,
        string? author,
        IReadOnlyList<string> tags,
        string? description = null,
        PosePreviewImageEdit image = default)
    {
        const PoseLibraryFileActionKind kind = PoseLibraryFileActionKind.EditMetadata;
        try
        {
            var read = _store.Read(path);
            if (!read.Succeeded)
                return Refused(
                    kind,
                    read.Failure?.Detail ?? "The pose file could not be read.");

            var pose = read.Pose!;
            if (!string.IsNullOrWhiteSpace(pose.Version))
                return Refused(
                    kind,
                    $"Pose version '{pose.Version}' is not supported, so the " +
                    "file is not rewritten.");

            var trimmedAuthor = author?.Trim();
            pose.Author = string.IsNullOrEmpty(trimmedAuthor) ? null : trimmedAuthor;

            var cleaned = NormalizeTags(tags);
            pose.Tags = cleaned.Count == 0 ? null : cleaned;

            if (description is not null)
            {
                var trimmed = description.Trim();
                pose.Description =
                    string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }

            if (image.Remove)
                pose.Base64Image = null;
            else if (image.Base64 is { Length: > 0 } encoded)
                pose.Base64Image = encoded;

            var write = _store.Write(pose, path);
            return write.Succeeded
                ? Succeeded(kind, path)
                : Refused(
                    kind,
                    write.Failure?.Detail ?? "The pose file could not be written.");
        }
        catch (Exception ex)
        {
            return Refused(kind, $"Editing the metadata failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The largest source image an edit will encode into a pose document. A
    /// preview is a thumbnail; a multi-megabyte source would inflate by a
    /// third in base64 and ride inside every read of that pose from then on.
    /// </summary>
    public const long MaxPreviewImageBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Reads an image off disk as the base64 a pose document stores, or
    /// refuses with a reason fit to show. The bytes are never inspected beyond
    /// their size: the format the file claims is the viewer's problem, and
    /// pretending to validate one here would be theatre.
    /// </summary>
    public static PoseLibraryFileActionResult ReadPreviewImage(
        string path, out string? base64)
    {
        const PoseLibraryFileActionKind kind =
            PoseLibraryFileActionKind.EditMetadata;
        base64 = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Refused(kind, "The image no longer exists.");
            if (info.Length > MaxPreviewImageBytes)
                return Refused(
                    kind,
                    $"The image is {info.Length / 1024} KB; a pose preview is " +
                    $"limited to {MaxPreviewImageBytes / 1024} KB.");
            base64 = Convert.ToBase64String(File.ReadAllBytes(path));
            return Succeeded(kind, path);
        }
        catch (Exception ex)
        {
            return Refused(kind, $"The image could not be read: {ex.Message}");
        }
    }

    private static List<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        var cleaned = new List<string>(tags.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
                continue;
            cleaned.Add(trimmed);
        }
        return cleaned;
    }

    /// <summary>The destination path for a file landing in
    /// <paramref name="directory"/>: the bare name, else the first free
    /// numeric suffix, else null when the convention is exhausted.</summary>
    private static string? UniqueDestination(string directory, string fileName)
    {
        var bare = Path.Combine(directory, fileName);
        if (!File.Exists(bare))
            return bare;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i <= MaxCollisionSuffix; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static PoseLibraryFileActionResult Succeeded(
        PoseLibraryFileActionKind kind, string? resultPath) =>
        new()
        {
            Kind = kind,
            Succeeded = true,
            ResultPath = resultPath,
        };

    private static PoseLibraryFileActionResult Refused(
        PoseLibraryFileActionKind kind, string detail) =>
        new()
        {
            Kind = kind,
            Succeeded = false,
            Detail = detail,
        };
}
