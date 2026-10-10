namespace Poser.Application.Scene;

/// <summary>
/// Framework-thread refresh queue. A notification only marks work pending; the
/// owner's frame tick drains it with at most one pass. However many
/// notifications a frame carries (a clear-first load publishes per spawn and
/// per delete), it costs one refresh, and none ever runs inside the
/// publisher's call stack — which may be a native hook.
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
