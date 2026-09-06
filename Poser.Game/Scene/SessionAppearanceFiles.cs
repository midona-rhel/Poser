using Poser.Domain.Operations;

namespace Poser.Game.Scene;

/// <summary>Embedded packages remain readable by actor history until its session ends.</summary>
internal sealed class SessionAppearanceFiles(Action<string> delete) : IDisposable
{
    private readonly Dictionary<string, SessionGeneration> _files = new();
    private bool _disposed;

    public bool Retain(string path, SessionGeneration session)
    {
        lock (_files)
        {
            if (_disposed) return false;
            _files[path] = session;
            return true;
        }
    }

    public void Sweep(SessionGeneration? current)
    {
        lock (_files)
        {
            foreach (var path in _files.Where(x => x.Value != current).Select(x => x.Key).ToArray())
            {
                delete(path);
                _files.Remove(path);
            }
        }
    }

    public void Dispose()
    {
        lock (_files)
        {
            _disposed = true;
            Sweep(null);
        }
    }
}
