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

    /// <summary>The folder's scene autosaves and old pose backups, newest
    /// first. A folder with neither is left out.</summary>
    public required List<AutoSaveEntry> Entries { get; init; }
}

/// <summary>One autosaved scene or pose as the Auto-saves tab lists it: its
/// name and date only. What the file holds is read when it is selected.</summary>
public sealed class AutoSaveEntry
{
    public required string FilePath { get; init; }

    /// <summary>File name without its extension.</summary>
    public required string Name { get; init; }

    public required string NameLower { get; init; }

    /// <summary>The modified stamp, already formatted.</summary>
    public required string Stamp { get; init; }
}


/// <summary>
/// Lists the scene autosave folders and the old pose backup folders: folder
/// and file names and their dates, without opening any file, as the library
/// lists its sources.
/// </summary>
public static class AutoSaveLibraryReader
{
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";
    private const string SnapshotDayFolderFormat = "yyyy-MM-dd";
    private const string DayFormat = LibraryStamp.DateFormat;
    private const string StampFormat = LibraryStamp.DateTimeFormat;
    private const string PoseExtension = ".pose";

    public static List<AutoSaveFolder> Read(string root, string? sceneRoot = null)
    {
        var snapshots = new List<(DirectoryInfo Directory, DateTime At)>();
        try
        {
            // The enumeration carries each entry's dates, so sorting needs
            // no second call per folder or file.
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
                entries.Add(new AutoSaveEntry
                {
                    FilePath = file.FullName,
                    Name = name,
                    NameLower = name.ToLowerInvariant(),
                    Stamp = file.LastWriteTime.ToString(
                        StampFormat, CultureInfo.InvariantCulture),
                });
            }

            read.Add(new AutoSaveFolder
            {
                Directory = directory.FullName,
                Day = SnapshotDay(directory.Name, at),
                Entries = entries,
            });
        }

        return read;
    }

    private static string SnapshotDay(string name, DateTime writtenUtc)
    {
        // A scene autosave folder is named after its local day. The day is
        // taken from the name, not the folder's write time, because deleting
        // old saves inside the folder changes that time. Old pose backup
        // folders are named after their save time in UTC.
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
}
