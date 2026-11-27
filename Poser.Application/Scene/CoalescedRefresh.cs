namespace Poser.Application.Scene;

/// <summary>
/// Collects requests for a refresh and runs at most one per frame. A request
/// only marks that a refresh is wanted; the owner runs it in its frame update.
/// However many requests come in one frame (a load that clears first sends one
/// for each actor it adds and removes), there is one refresh. It never runs
/// inside the code that asked for it, which may be a game hook.
/// </summary>
public sealed class CoalescedRefresh
{
    private bool _pending;
    private volatile bool _stopped;

    public void Request()
    {
        if (!_stopped)
            _pending = true;
    }

    /// <summary>Runs one pass when work is pending. A request made during the
    /// pass waits for the next drain.</summary>
    public void Drain(Action refresh)
    {
        if (!_pending || _stopped) return;
        _pending = false;
        refresh();
    }

    public void Cancel() => _pending = false;
    public void Stop() { _stopped = true; Cancel(); }
}
