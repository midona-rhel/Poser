using Poser.Documents.Library;
using Poser.Application.AutoSave;

namespace Poser.Application.Library;

/// <summary>
/// Scans autosave folders in the background. Concurrent requests schedule at most
/// one additional pass, and TakeLatest transfers the latest result to the caller.
/// </summary>
public sealed class AutoSaveLibrary(string rootDirectory, SceneAutoSaveService scenes)
{
    public string RootDirectory { get; } = rootDirectory;
    private readonly AutoSaveLibraryReader _reader = new();
    private readonly object _sync = new();
    private bool _scanning, _queued;
    private List<AutoSaveFolder>? _latest;

    public void RequestScan()
    {
        var root = RootDirectory;
        lock (_sync)
        {
            if (_scanning) { _queued = true; return; }
            _scanning = true;
        }
        _ = Task.Run(() =>
        {
            while (true)
            {
                List<AutoSaveFolder> result;
                try { result = _reader.Read(root, scenes.RootDirectory); }
                catch { result = []; }
                Volatile.Write(ref _latest, result);
                lock (_sync)
                {
                    if (!_queued) { _scanning = false; return; }
                    _queued = false;
                }
            }
        });
    }

    public List<AutoSaveFolder>? TakeLatest() => Interlocked.Exchange(ref _latest, null);
}

