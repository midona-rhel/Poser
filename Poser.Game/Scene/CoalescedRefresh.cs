namespace Poser.Game.Scene;

/// <summary>Framework-thread refresh queue; nested notifications request work, never recurse.</summary>
internal sealed class CoalescedRefresh
{
    private bool _running;
    private bool _pending;
    private volatile bool _stopped;

    public void Request(Action refresh)
    {
        if (_stopped) return;
        _pending = true;
        Drain(refresh);
    }

    public void Drain(Action refresh)
    {
        if (_running || _stopped) return;
        _running = true;
        try
        {
            // Discovery can publish synchronously. Bound this frame to the
            // initial pass and one follow-up; another notification waits a tick.
            for (int pass = 0; pass < 2 && _pending && !_stopped; pass++)
            {
                _pending = false;
                refresh();
            }
        }
        finally { _running = false; }
    }

    public void Cancel() => _pending = false;
    public void Stop() { _stopped = true; Cancel(); }
}
