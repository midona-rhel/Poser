using System.Globalization;
using Poser.Documents.Files;

namespace Poser.Documents.AutoSave;

public sealed record NamedAutoSavePose(string ActorName, string FileName, PoseFile Pose);
public sealed record PoseSnapshotWriteResult(bool Success, int Written,
    IReadOnlyList<string> Paths, IReadOnlyList<string> RecoveryEvidence,
    string? FailurePhase, string? Detail);

/// <summary>Pose snapshot layout, safe writes and existing disk-based retention.</summary>
public sealed class PoseAutoSaveStore
{
    private const string DayFolderFormat = "yyyy-MM-dd";
    private const string TimePrefixFormat = "HH-mm-ss";
    private const string PoseExtension = ".pose";
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
                        // Add the actor name to the file-store failure.
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
    /// Creates a valid, unique file name for each actor in the snapshot.
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

        return Files.FileNamePolicy.GeneratedStem(actorName, "Actor");
    }

    /// <summary>
    /// Keeps the newest configured number of pose files, using filesystem
    /// last-write time. Each pose file counts separately. A directory from the
    /// older one-directory-per-snapshot format counts as one item. Last-write
    /// time is only a fallback save date and can change if a file is copied,
    /// synchronized, or edited. Names break timestamp ties.
    ///
    /// Only <c>*.pose</c> files count. Temporary and backup files left by a
    /// failed atomic write are preserved for recovery.
    /// </summary>
    private bool Prune(int keep)
    {
        var events = new List<(DateTime AtUtc, string Key, string Target, bool LegacyDir)>();
        var dayFolders = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                if (!IsDayFolder(name))
                {
                    // Old layout: the folder is the save.
                    events.Add((Directory.GetLastWriteTimeUtc(dir), name, dir, true));
                    continue;
                }

                dayFolders.Add(dir);
                foreach (var file in Directory.EnumerateFiles(dir, "*" + PoseExtension))
                {
                    events.Add((File.GetLastWriteTimeUtc(file),
                        $"{name}/{Path.GetFileName(file)}", file, false));
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
        foreach (var (_, _, path, legacyDir) in stale)
        {
            try
            {
                if (legacyDir)
                    Directory.Delete(path, recursive: true);
                else
                    File.Delete(path);
                pruned++;
            }
            catch (Exception ex)
            {
                success = false;
                _error($"Auto-save: could not prune '{path}': {ex.Message}");
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
