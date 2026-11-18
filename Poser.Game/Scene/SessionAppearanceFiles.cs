using Poser.Domain.Operations;

namespace Poser.Game.Scene;

/// <summary>Temporary appearance files a scene uses. Each one is kept until its session ends, so undo can still read it.</summary>
internal sealed class SessionAppearanceFiles(Action<string> delete) : IDisposable
{
    // File name starts: an appearance packed while saving a scene, and an MCDF waiting to be applied while a scene loads.
    private const string Prefix = Poser.Application.Scene.SceneAppearanceRule.PackedPrefix;
    private const string StagedPrefix = "poser-scene-mcdf-";

    /// <summary>Poser's own folder for these files, so the cleanup at startup
    /// never touches another program's temporary files.</summary>
    internal static string TempDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "Poser", "scene-appearance");

    /// <summary>Files older than this are deleted when Poser starts. Age is
    /// only a guess: a second running copy of the game could still use one.</summary>
    internal static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, SessionGeneration> _files = new();
    private SessionGeneration? _swept;
    private bool _pending;
    private bool _disposed;

    /// <summary>A new file for an appearance packed while saving, or, with
    /// <paramref name="staged"/>, for an MCDF about to be applied while a scene loads.</summary>
    internal static string NewTempPath(bool staged = false)
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, $"{(staged ? StagedPrefix : Prefix)}{Guid.NewGuid():N}.mcdf");
    }

    /// <summary>Deletes these files in <paramref name="directory"/> that were
    /// last written before <paramref name="cutoffUtc"/>.</summary>
    internal static void DeleteStale(string directory, DateTime cutoffUtc, Action<string> delete)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var prefix in new[] { Prefix, StagedPrefix })
                foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.mcdf"))
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                        delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read only wastes disk space; it must not stop Poser starting.
        }
    }

    public bool Retain(string path, SessionGeneration session)
    {
        lock (_files)
        {
            if (_disposed) return false;
            _files[path] = session;
            _pending |= session != _swept;
            return true;
        }
    }

    /// <summary>Called every frame to delete files from other sessions. It
    /// only does work when the session changed or such a file was added.</summary>
    public void Sweep(SessionGeneration? current)
    {
        lock (_files)
        {
            if (current == _swept && !_pending) return;
            DeleteOutside(current);
        }
    }

    public void Dispose()
    {
        lock (_files)
        {
            _disposed = true;
            DeleteOutside(null);
        }
    }

    private void DeleteOutside(SessionGeneration? current)
    {
        _swept = current;
        _pending = false;
        foreach (var (path, session) in _files)
        {
            if (session == current) continue;
            delete(path);
            _files.Remove(path);
        }
    }
}
