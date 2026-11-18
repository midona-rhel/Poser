using Poser.Domain.Operations;

namespace Poser.Game.Scene;

/// <summary>Embedded packages remain readable by actor history until its session ends.</summary>
internal sealed class SessionAppearanceFiles(Action<string> delete) : IDisposable
{
    // Looks packed while saving a scene; an MCDF staged to be put on during a load.
    private const string Prefix = Poser.Application.Scene.SceneAppearanceRule.PackedPrefix;
    private const string StagedPrefix = "poser-scene-mcdf-";

    /// <summary>Poser's own folder for appearance packages, so a startup
    /// sweep never touches another program's temp files.</summary>
    internal static string TempDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "Poser", "scene-appearance");

    /// <summary>Age threshold for startup cleanup. This is a heuristic; file age
    /// alone does not prove that another running client has stopped using it.</summary>
    internal static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, SessionGeneration> _files = new();
    private SessionGeneration? _swept;
    private bool _pending;
    private bool _disposed;

    /// <summary>A new file for a look packed while saving, or, with
    /// <paramref name="staged"/>, for an MCDF about to be put on during a load.</summary>
    internal static string NewTempPath(bool staged = false)
    {
        Directory.CreateDirectory(TempDirectory);
        return Path.Combine(TempDirectory, $"{(staged ? StagedPrefix : Prefix)}{Guid.NewGuid():N}.mcdf");
    }

    /// <summary>Deletes appearance packages in <paramref name="directory"/>
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
