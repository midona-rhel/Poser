using Poser.Library;
using Poser.Services;

namespace Poser.Application.Library;

public interface IAutoSaveLibrary
{
    void RequestScan();
    List<AutoSaveFolder>? TakeLatest();
}

/// <summary>Coalesces directory reads independently of library-panel lifetime.</summary>
public sealed class AutoSaveLibrary(IAutoSaveService saves) : IAutoSaveLibrary
{
    private readonly object _sync = new();
    private bool _scanning, _queued;
    private List<AutoSaveFolder>? _latest;

    public void RequestScan()
    {
        var root = saves.RootDirectory;
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
                try { result = AutoSaveLibraryReader.Read(root); }
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

