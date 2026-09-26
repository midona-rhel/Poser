namespace Poser.Files;

/// <summary>Whole-scene snapshot filenames and disk retention, separate from pose snapshots.</summary>
public sealed class SceneAutoSaveStore
{
    private readonly SceneFileStore _store;
    private readonly Action<string> _error;
    public string RootDirectory { get; }

    public SceneAutoSaveStore(string rootDirectory, Action<string> error, SceneFileStore? store = null)
    {
        RootDirectory = rootDirectory;
        _error = error;
        _store = store ?? SceneFileStore.Default;
    }

    /// <summary>
    /// Content identity of one captured scene, with the capture stamp set
    /// aside: two ticks over an untouched scene differ only in when they ran,
    /// and that is not a change worth a file.
    ///
    /// <para>It hashes the WHOLE document, embedded poses included, rather
    /// than a summary of it: a summary that missed a moved bone would drop
    /// the user's work, which is far worse than a duplicate file. A document
    /// this cannot describe answers null, and a null signature never matches
    /// anything — an unreadable scene is written, not skipped.</para>
    /// </summary>
    public static string? Signature(SceneFile scene)
    {
        var savedAt = scene.SavedAt;
        scene.SavedAt = null;
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256
                .HashData(System.Text.Json.JsonSerializer
                    .SerializeToUtf8Bytes(scene)));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            scene.SavedAt = savedAt;
        }
    }

    public (string Path, SceneWriteOutcome Result) Write(SceneFile scene, DateTime localNow)
    {
        var folder = Path.Combine(RootDirectory, localNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, localNow);
        return (path, _store.Write(scene, path));
    }

    /// <summary>A second snapshot inside the same second suffixes rather than
    /// overwriting — same convention the pose auto-save uses.</summary>
    private static string UniquePath(string folder, DateTime localNow)
    {
        string stem = $"{localNow:HH-mm-ss} Scene";
        string candidate = System.IO.Path.Combine(
            folder, stem + SceneFile.Extension);
        for (int suffix = 2; File.Exists(candidate) && suffix < 100; suffix++)
        {
            candidate = System.IO.Path.Combine(
                folder, $"{stem} ({suffix}){SceneFile.Extension}");
        }
        return candidate;
    }

    /// <summary>
    /// Retention from DISK, never from an in-memory list: one snapshot file is
    /// one save event, newest first by write date so the order survives a
    /// restart, and a day folder whose last snapshot is pruned goes with it.
    /// Every IO failure is logged with its path and never aborts the sweep.
    /// </summary>
    public void Prune(int keep)
    {
        List<FileInfo> snapshots;
        try
        {
            var root = new DirectoryInfo(RootDirectory);
            if (!root.Exists)
                return;
            snapshots = root
                .EnumerateFiles("*" + SceneFile.Extension, SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.FullName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _error($"Scene auto-save: could not enumerate '{RootDirectory}': {ex.Message}");
            return;
        }

        foreach (var stale in snapshots.Skip(keep))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception ex)
            {
                _error($"Scene auto-save: could not delete '{stale.FullName}': {ex.Message}");
            }
        }

        try
        {
            foreach (var day in new DirectoryInfo(RootDirectory).EnumerateDirectories())
            {
                try
                {
                    if (!day.EnumerateFileSystemInfos().Any())
                        day.Delete();
                }
                catch (Exception ex)
                {
                    _error(
                        $"Scene auto-save: could not remove '{day.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _error($"Scene auto-save: could not sweep '{RootDirectory}': {ex.Message}");
        }
    }

}
