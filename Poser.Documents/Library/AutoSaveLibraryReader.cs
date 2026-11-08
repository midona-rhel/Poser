using System.Globalization;
using Poser.Domain.Library;
using Poser.Documents.Files;

namespace Poser.Documents.Library;

/// <summary>Contents of one autosave folder, prepared for display by a
/// background scan.</summary>
public sealed class AutoSaveFolder
{
    /// <summary>Full folder path, also used to break equal-date ties.</summary>
    public required string Directory { get; init; }

    /// <summary>Display date used to group this snapshot.</summary>
    public required string Day { get; init; }

    /// <summary>Pose files in display order. Empty folders are omitted.</summary>
    public required List<AutoSaveEntry> Entries { get; init; }
}

/// <summary>Display metadata for one autosaved pose, read on a background thread.</summary>
public sealed class AutoSaveEntry
{
    public required string FilePath { get; init; }

    /// <summary>File name without its extension.</summary>
    public required string Name { get; init; }

    public required string NameLower { get; init; }

    /// <summary>The modified stamp, already formatted.</summary>
    public required string Stamp { get; init; }

    /// <summary>Place recorded in the pose file. Empty means the file has no
    /// readable place metadata; no location is inferred from its folder.</summary>
    public required string Place { get; init; }

    /// <summary>Whether the pose file contains a preview image.</summary>
    public required bool HasThumbnail { get; init; }
}


/// <summary>
/// Reads current and legacy autosave directories without application or game
/// access. Each scan lists every entry, but cached metadata is reused while a
/// file's length and last-write time are unchanged. The caller serializes scans.
/// </summary>
public sealed class AutoSaveLibraryReader
{
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";
    private const string SnapshotDayFolderFormat = "yyyy-MM-dd";
    private const string DayFormat = LibraryStamp.DateFormat;
    private const string StampFormat = LibraryStamp.DateTimeFormat;
    private const string PoseExtension = ".pose";

    private readonly record struct FileStamp(long Length, long WriteTicks);

    private readonly record struct FileFacts(FileStamp Stamp, string Place, bool HasThumbnail);

    /// <summary>What the last pass read, and what this one has confirmed.
    /// Swapped at the end of a pass, so a pruned file is forgotten with it.
    /// </summary>
    private Dictionary<string, FileFacts> _known = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FileFacts> _seen = new(StringComparer.OrdinalIgnoreCase);

    public List<AutoSaveFolder> Read(string root, string? sceneRoot = null)
    {
        var snapshots = new List<(DirectoryInfo Directory, DateTime At)>();
        try
        {
            // The enumeration carries each entry's stamps, so ordering and
            // caching need no second call per folder or file.
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
                snapshots.Add((directory, directory.LastWriteTimeUtc));
        }
        catch (Exception)
        {
            // A missing or unreadable root is an empty tab, not a failure.
        }

        if (sceneRoot is not null)
        {
            try
            {
                foreach (var directory in new DirectoryInfo(sceneRoot).EnumerateDirectories())
                    snapshots.Add((directory, directory.LastWriteTimeUtc));
            }
            catch (Exception) { }
        }

        // Newest first, ties on name descending: the order the service's own
        // retention uses, so what the browser lists last is what it prunes.
        snapshots.Sort(static (a, b) =>
        {
            int byDate = b.At.CompareTo(a.At);
            return byDate != 0
                ? byDate
                : string.CompareOrdinal(b.Directory.FullName, a.Directory.FullName);
        });

        var read = new List<AutoSaveFolder>(snapshots.Count);
        var files = new List<FileInfo>();
        foreach (var (directory, at) in snapshots)
        {
            files.Clear();
            try
            {
                foreach (var file in directory.EnumerateFiles())
                    if (System.IO.Path.GetExtension(file.Name).Equals(
                            PoseExtension, StringComparison.OrdinalIgnoreCase)
                        || file.Extension.Equals(SceneFile.Extension, StringComparison.OrdinalIgnoreCase))
                        files.Add(file);
            }
            catch (Exception)
            {
            }

            if (files.Count == 0)
                continue;
            // Newest first, by the save time; the name only breaks ties.
            files.Sort(static (a, b) =>
            {
                int byTime = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                return byTime != 0
                    ? byTime
                    : string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
            });

            var entries = new List<AutoSaveEntry>(files.Count);
            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                var facts = Describe(file);
                entries.Add(new AutoSaveEntry
                {
                    FilePath = file.FullName,
                    Name = name,
                    NameLower = name.ToLowerInvariant(),
                    Stamp = file.LastWriteTime.ToString(
                        StampFormat, CultureInfo.InvariantCulture),
                    Place = facts.Place,
                    HasThumbnail = facts.HasThumbnail,
                });
            }

            read.Add(new AutoSaveFolder
            {
                Directory = directory.FullName,
                Day = SnapshotDay(directory.Name, at),
                Entries = entries,
            });
        }

        (_known, _seen) = (_seen, _known);
        _seen.Clear();
        return read;
    }

    private static string SnapshotDay(string name, DateTime writtenUtc)
    {
        // The per-day layout: the folder name IS the (local) day. Read off the
        // NAME rather than through the mtime fallback, which a later prune
        // deleting siblings inside the folder would silently bump — then
        // restated in the caption's own format, which is not the disk's.
        if (DateTime.TryParseExact(
                name,
                SnapshotDayFolderFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var named))
            return named.ToString(DayFormat, CultureInfo.InvariantCulture);

        var time = DateTime.TryParseExact(
            name,
            SnapshotFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : writtenUtc.ToLocalTime();
        return time.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where an auto-saved pose says it was taken, and whether it carries an
    /// image. Read through the ordinary pose codec's own metadata probe — the
    /// same seam the scanned library indexes every <c>.pose</c> with, so the
    /// auto-save tab is not a second JSON contract — and only for a file whose
    /// length or write time moved since the last pass: the probe walks the
    /// whole bounded document.
    ///
    /// <para>Anything that does not answer a place is EMPTY, never a guess: a
    /// file written before auto-saves recorded one, and a file whose document
    /// no longer reads, are both "no place recorded" and gather under the day
    /// alone. A failed read is not remembered, so a file that was only
    /// briefly unreadable is asked again next pass.</para>
    /// </summary>
    private FileFacts Describe(FileInfo file)
    {
        var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        string path = file.FullName;
        if (_known.TryGetValue(path, out var held) && held.Stamp == stamp)
        {
            _seen[path] = held;
            return held;
        }

        if (file.Extension.Equals(SceneFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var scene = SceneFileStore.Default.ReadMetadata(path);
            var sceneFacts = new FileFacts(stamp, scene.PlaceName ?? string.Empty, false);
            if (scene.Succeeded) _seen[path] = sceneFacts;
            return sceneFacts;
        }
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
        if (!metadata.Succeeded)
            return new FileFacts(stamp, string.Empty, false);
        var facts = new FileFacts(
            stamp, metadata.PlaceName ?? string.Empty, metadata.HasThumbnail);
        _seen[path] = facts;
        return facts;
    }
}
