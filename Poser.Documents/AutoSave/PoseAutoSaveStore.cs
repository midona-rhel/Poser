using System.Globalization;

namespace Poser.Files;

public sealed record NamedAutoSavePose(string ActorName, string FileName, PoseFile Pose);
public sealed record PoseSnapshotWriteResult(bool Success, int Written,
    IReadOnlyList<string> Paths, IReadOnlyList<string> RecoveryEvidence,
    string? FailurePhase, string? Detail);

/// <summary>Pose snapshot layout, safe writes and existing disk-based retention.</summary>
public sealed class PoseAutoSaveStore
{
    private const string DayFolderFormat = "yyyy-MM-dd";
    private const string TimePrefixFormat = "HH-mm-ss";
    private readonly Action<string> _error;
    private readonly Action<string> _info;
    private readonly Action<string> _debug;
    public string RootDirectory { get; }

    public PoseAutoSaveStore(string rootDirectory, Action<string> error,
        Action<string> info, Action<string> debug)
    {
        RootDirectory = rootDirectory;
        _error = error;
        _info = info;
        _debug = debug;
        try { Directory.CreateDirectory(rootDirectory); }
        catch (Exception ex) { _error($"Auto-save: could not create '{RootDirectory}': {ex.Message}"); }
    }

    public PoseSnapshotWriteResult Write(
        string reason,
        DateTime nowUtc,
        int keep,
        IReadOnlyList<NamedAutoSavePose> captured)
    {
        var success = true;
        string? failure = null;
        string? failurePhase = null;
        var affectedPaths = new List<string>();
        var recoveryEvidence = new List<string>();
        var saved = 0;
        try
        {
            var local = nowUtc.ToLocalTime();
            var dayFolder = Path.Combine(
                RootDirectory,
                local.ToString(DayFolderFormat, CultureInfo.InvariantCulture));
            var prefix = local.ToString(TimePrefixFormat, CultureInfo.InvariantCulture);
            var planned = new List<(NamedAutoSavePose Entry, string Path)>(captured.Count);
            foreach (var entry in captured)
            {
                var path = SnapshotFilePath(dayFolder, prefix, entry.FileName);
                affectedPaths.Add(path);
                planned.Add((entry, path));
            }

            Directory.CreateDirectory(dayFolder);
            foreach (var (entry, path) in planned)
            {
                try
                {
                    var write = AtomicPoseFileStore.Default.Write(entry.Pose, path);
                    if (write.Succeeded)
                    {
                        saved++;
                    }
                    else
                    {
                        success = false;
                        failurePhase ??= "ActorWrite";
                        failure ??= write.Failure?.Detail ?? $"export failed for actor '{entry.ActorName}'";
                        recoveryEvidence.AddRange(write.RecoveryEvidencePaths);
                        // The typed atomic store carries the filesystem
                        // evidence; this adds the auto-save actor context.
                        _error(
                            $"Auto-save ({reason}): export failed for actor '{entry.ActorName}' -> {path}: {write.Failure?.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    failurePhase ??= "ActorWrite";
                    failure ??= ex.Message;
                    _error(
                        $"Auto-save ({reason}): actor '{entry.ActorName}' -> {path} threw: {ex.Message}");
                }
            }

            _info($"Auto-saved {saved}/{captured.Count} actor(s) to {dayFolder} ({reason})");
            if (!Prune(keep))
            {
                success = false;
                failurePhase ??= "Retention";
                failure ??= "retention pruning failed";
            }
        }
        catch (Exception ex)
        {
            success = false;
            failurePhase ??= "Worker";
            failure ??= ex.Message;
            _error($"Auto-save ({reason}) failed: {ex}");
        }
        return new(success, saved, affectedPaths, recoveryEvidence, failurePhase, failure);
    }

    private static string SnapshotFilePath(string dayFolder, string prefix, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(dayFolder, $"{prefix} {stem}{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(dayFolder, $"{prefix} {stem} ({suffix}){extension}");
        return candidate;
    }

    /// <summary>
    /// Ktisis <c>FormatService.StripInvalidChars</c> parity, plus in-snapshot
    /// de-duplication so two actors with the same name both survive.
    /// </summary>
    public static string UniqueFileName(string actorName, HashSet<string> used)
    {
        var name = Sanitize(actorName);
        if (used.Add(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static string Sanitize(string actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
            return "Actor";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = actorName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "Actor" : sanitized;
    }

    /// <summary>
    /// Disk-based retention: the newest <c>MaxAutoSaves</c> FILES by date are
    /// kept, everything older is deleted — a file is what the auto-saves tab
    /// lists and what the cap means to the user (a save of three actors is
    /// three auto-saves). A whole folder of the old one-folder-per-save
    /// layout counts as one, which is how pre-existing snapshots join the
    /// same ordering and age out without a migration. Reading the disk
    /// rather than a session queue is what makes retention hold across
    /// restarts.
    ///
    /// <para>Date, not name (Brio's semantic): a save is written once and never
    /// touched again, so its last-write time IS the save date, and a folder or
    /// file the user renamed keeps its true age instead of being sorted by
    /// whatever it is now called. Ties break on key, descending, so the order
    /// is total even at one-second stamp granularity. A day folder whose last
    /// event was pruned goes with it.</para>
    /// </summary>
    private bool Prune(int keep)
    {
        var events = new List<(DateTime AtUtc, string Key, string? LegacyDir, List<string>? Files)>();
        var dayFolders = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                if (!IsDayFolder(name))
                {
                    // Old layout: the folder is the save.
                    events.Add((Directory.GetLastWriteTimeUtc(dir), name, dir, null));
                    continue;
                }

                dayFolders.Add(dir);
                foreach (var group in Directory.EnumerateFiles(dir)
                             .GroupBy(file => Path.GetFileName(file)))
                {
                    var files = group.ToList();
                    var newest = DateTime.MinValue;
                    foreach (var file in files)
                    {
                        var at = File.GetLastWriteTimeUtc(file);
                        if (at > newest)
                            newest = at;
                    }
                    events.Add((newest, $"{name}/{group.Key}", null, files));
                }
            }
        }
        catch (Exception ex)
        {
            _error($"Auto-save: could not enumerate '{RootDirectory}' to prune: {ex.Message}");
            return false;
        }

        var stale = events
            .OrderByDescending(entry => entry.AtUtc)
            .ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        var pruned = 0;
        var success = true;
        foreach (var (_, _, legacyDir, files) in stale)
        {
            try
            {
                if (legacyDir != null)
                    Directory.Delete(legacyDir, recursive: true);
                else
                    foreach (var file in files!)
                        File.Delete(file);
                pruned++;
            }
            catch (Exception ex)
            {
                success = false;
                _error(
                    $"Auto-save: could not prune '{legacyDir ?? files![0]}': {ex.Message}");
            }
        }

        foreach (var dir in dayFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                success = false;
                _error(
                    $"Auto-save: could not remove empty day folder '{dir}': {ex.Message}");
            }
        }

        if (pruned > 0)
            _debug($"Auto-save pruned {pruned} old save(s).");
        return success;
    }

    private static bool IsDayFolder(string name) =>
        DateTime.TryParseExact(
            name,
            DayFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    public bool CleanAll()
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(RootDirectory).ToList();
        }
        catch (Exception ex)
        {
            _error($"Auto-save: could not enumerate '{RootDirectory}' to clean: {ex.Message}");
            return false;
        }

        var deleted = 0;
        var success = true;
        foreach (var dir in folders)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                success = false;
                _error($"Auto-save: could not delete '{dir}': {ex.Message}");
            }
        }

        _info($"Auto-save cleaned {deleted} snapshot folder(s) on leaving GPose.");
        try
        {
            return success && !Directory.EnumerateDirectories(RootDirectory).Any();
        }
        catch (Exception ex)
        {
            _error(
                $"Auto-save: could not verify clean-on-exit root '{RootDirectory}': {ex.Message}");
            return false;
        }
    }

}
