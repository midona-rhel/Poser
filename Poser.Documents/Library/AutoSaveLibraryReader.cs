using System.Globalization;
using Poser.Files;

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
}


/// <summary>Reads current and legacy autosave directories without application or game access.</summary>
public static class AutoSaveLibraryReader
{
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";
    private const string SnapshotDayFolderFormat = "yyyy-MM-dd";
    private const string DayFormat = LibraryStamp.DateFormat;
    private const string StampFormat = LibraryStamp.DateTimeFormat;
    private const string PoseExtension = ".pose";

    public static List<AutoSaveFolder> Read(string root)
    {
        var snapshots = new List<(string Directory, DateTime At)>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
                snapshots.Add((directory, SafeFolderTime(directory)));
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
                : string.CompareOrdinal(b.Directory, a.Directory);
        });

        var read = new List<AutoSaveFolder>(snapshots.Count);
        var files = new List<string>();
        foreach (var (directory, _) in snapshots)
        {
            files.Clear();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (System.IO.Path.GetExtension(file).Equals(
                            PoseExtension, StringComparison.OrdinalIgnoreCase))
                        files.Add(file);
            }
            catch (Exception)
            {
            }

            if (files.Count == 0)
                continue;
            // Newest first, by the save time; the name only breaks ties.
            files.Sort((a, b) =>
            {
                int byTime = SafeFileTime(b).CompareTo(SafeFileTime(a));
                return byTime != 0
                    ? byTime
                    : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            var entries = new List<AutoSaveEntry>(files.Count);
            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                entries.Add(new AutoSaveEntry
                {
                    FilePath = file,
                    Name = name,
                    NameLower = name.ToLowerInvariant(),
                    Stamp = SafeFileTime(file).ToString(
                        StampFormat, CultureInfo.InvariantCulture),
                    Place = SafePlace(file),
                });
            }

            read.Add(new AutoSaveFolder
            {
                Directory = directory,
                Day = SnapshotDay(directory),
                Entries = entries,
            });
        }

        return read;
    }

    private static string SnapshotDay(string directory)
    {
        var name = System.IO.Path.GetFileName(directory);

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
            : SafeFolderTime(directory).ToLocalTime();
        return time.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    private static DateTime SafeFolderTime(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static DateTime SafeFileTime(string file)
    {
        try
        {
            return File.GetLastWriteTime(file);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// Where an auto-saved pose says it was taken. Read through the ordinary
    /// pose codec's own metadata probe — the same seam the scanned library
    /// indexes every <c>.pose</c> with, so the auto-save tab is not a second
    /// JSON contract. Worker thread only: the probe validates a whole bounded
    /// document per file.
    ///
    /// <para>Anything that does not answer a place is EMPTY, never a guess: a
    /// file written before auto-saves recorded one, and a file whose document
    /// no longer reads, are both "no place recorded" and gather under the day
    /// alone.</para>
    /// </summary>
    private static string SafePlace(string file)
    {
        try
        {
            var metadata = AtomicPoseFileStore.Default.ReadMetadata(file);
            return metadata.Succeeded ? metadata.PlaceName ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

}

