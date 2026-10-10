using Poser.Domain.Operations;

namespace Poser.Game.Scene;

/// <summary>Embedded packages remain readable by actor history until its session ends.</summary>
internal sealed class SessionAppearanceFiles(Action<string> delete) : IDisposable
{
    private const string Prefix = "poser-scene-appearance-";

    /// <summary>Poser's own folder for appearance packages, so a startup
    /// sweep never touches another program's temp files.</summary>
    internal static string TempDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "Poser", "scene-appearance");

    /// <summary>Packages left by a crash are older than any live session;
    /// another game client running Poser keeps its own young files.</summary>
    internal static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, SessionGeneration> _files = new();
    private SessionGeneration? _swept;
    private bool _pending;
    private bool _disposed;

    internal static string NewTempPath()
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, $"{Prefix}{Guid.NewGuid():N}.mcdf");
    }

    /// <summary>Deletes appearance packages in <paramref name="directory"/>
    /// last written before <paramref name="cutoffUtc"/>.</summary>
    internal static void DeleteStale(string directory, DateTime cutoffUtc, Action<string> delete)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, Prefix + "*.mcdf"))
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable temp folder costs disk, never startup.
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

    /// <summary>Called every frame; does work only when the session changed
    /// or a package from another session was retained.</summary>
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
