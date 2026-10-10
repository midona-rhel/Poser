using System.Globalization;
using Poser.Files;
using Poser.Domain.Library;

namespace Poser.Library;

/// <summary>One snapshot folder as the worker read it. Every string a mint
/// reads is already formatted here, so the pass on the draw thread writes
/// rows and looks favourites up and touches no file.</summary>
public sealed class AutoSaveFolder
{
    /// <summary>The snapshot folder itself — the row's provenance, and
    /// what the newest-first ordering broke its ties on.</summary>
    public required string Directory { get; init; }

    /// <summary>The day this snapshot's files were taken on — half of the
    /// rail row they fall under.</summary>
    public required string Day { get; init; }

    /// <summary>Its <c>.pose</c> files, already ordered. Never empty: a
    /// snapshot holding none is dropped by the worker, exactly as the
    /// synchronous build skipped it.</summary>
    public required List<AutoSaveEntry> Entries { get; init; }
}

/// <summary>One auto-saved pose, read and formatted off the draw thread.
/// </summary>
public sealed class AutoSaveEntry
{
    public required string FilePath { get; init; }

    /// <summary>The bare file name. The label takes the extension back on
    /// only when the setting asks for it, and the search keeps matching
    /// this either way.</summary>
    public required string Name { get; init; }

    public required string NameLower { get; init; }

    /// <summary>The modified stamp, already formatted.</summary>
    public required string Stamp { get; init; }

    /// <summary>Where the file says it was captured, or empty when it
    /// records no place. Read per FILE rather than per folder because a
    /// day folder spans a whole session: a snapshot taken in Limsa and one
    /// taken in Gridania land in the same folder and must not share a row.
    /// Empty is legacy — every auto-save written before 2026-08-14, and
    /// any file whose document no longer reads — and gathers under its day
    /// alone. No place is ever inferred.</summary>
    public required string Place { get; init; }

    /// <summary>Whether the document embeds a preview image. Auto-saves are
    /// written without one, so a tile that claimed one by default sent the
    /// thumbnail cache to open and parse every visible file to learn that.
    /// </summary>
    public required bool HasThumbnail { get; init; }
}


/// <summary>
/// Reads current and legacy autosave directories without application or game
/// access. The listing is redone every pass, but what a FILE says about itself
/// is remembered against the length and write time the listing reported, so a
/// pass only opens the files written since the last one. One pass at a time:
/// the owner serializes calls.
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

    public List<AutoSaveFolder> Read(string root)
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
                            PoseExtension, StringComparison.OrdinalIgnoreCase))
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

        var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
        if (!metadata.Succeeded)
            return new FileFacts(stamp, string.Empty, false);
        var facts = new FileFacts(
            stamp, metadata.PlaceName ?? string.Empty, metadata.HasThumbnail);
        _seen[path] = facts;
        return facts;
    }
}
