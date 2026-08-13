namespace Poser.Application.Lifecycle;

/// <summary>
/// Host-provided bridge for the one lifecycle operation currently migrated:
/// synchronously attempt one final immutable capture before legacy GPose teardown
/// runs. An implementation may return <see cref="FinalCaptureStatus.NotCaptured"/>
/// when an earlier dispatch is in flight; the result never acknowledges worker
/// completion or a durable write.
/// </summary>
public interface IFinalCapturePort
{
    /// <summary>Attempts exactly one synchronous final capture.</summary>
    FinalCaptureResult CaptureForExit();
}

/// <summary>
/// Coordinates the pre-publish capture edge. Legacy teardown remains behind
/// the existing GPose event and is reported as pending here. The host invokes
/// this synchronously from its framework update callback; no cross-thread
/// scheduling is performed by this coordinator.
/// </summary>
public interface ISessionLifecycleCoordinator
{
    /// <summary>
    /// Latest point-in-time exit result, or null before an exit edge. This is a
    /// diagnostic phase snapshot, not a completion claim for legacy teardown.
    /// </summary>
    SessionExitResult? LastExit { get; }

    /// <summary>Marks the start of a new GPose session for this coordinator.</summary>
    void OnGposeEntered();

    /// <summary>
    /// Attempts the final capture for the current exit edge at most once and
    /// returns before the caller publishes the legacy exit event.
    /// </summary>
    SessionExitResult OnGposeExit();
}

/// <summary>
/// Point-in-time result for the pre-publish phase. Legacy teardown remains
/// owned by the existing GPose event subscribers; <see
/// cref="LegacyTeardownPending"/> is a phase snapshot, not teardown completion.
/// </summary>
public readonly record struct SessionExitResult(
    FinalCaptureResult Capture,
    bool LegacyTeardownPending,
    bool AlreadyHandled)
{
    public static SessionExitResult Reentrant => new(
        FinalCaptureResult.NotCaptured("GPose exit capture is already in progress."),
        LegacyTeardownPending: true,
        AlreadyHandled: true);
}

/// <summary>
/// Exactly-once, reentrancy-safe owner of one final-capture attempt per
/// accepted GPose exit edge. It deliberately does not own cancellation,
/// restoration, native teardown, persistence joining, or detached-fact
/// publication yet.
/// </summary>
public sealed class SessionLifecycleCoordinator : ISessionLifecycleCoordinator
{
    private enum ExitState
    {
        Ready,
        Running,
        Completed,
    }

    private readonly IFinalCapturePort _finalCapture;
    private readonly object _gate = new();
    private ExitState _state = ExitState.Ready;
    private SessionExitResult _lastExit;
    private bool _hasLastExit;

    public SessionLifecycleCoordinator(IFinalCapturePort finalCapture)
    {
        _finalCapture = finalCapture;
    }

    public SessionExitResult? LastExit
    {
        get
        {
            lock (_gate)
                return _hasLastExit ? _lastExit : null;
        }
    }

    public void OnGposeEntered()
    {
        lock (_gate)
        {
            if (_state == ExitState.Running)
                return;

            _state = ExitState.Ready;
            _hasLastExit = false;
        }
    }

    public SessionExitResult OnGposeExit()
    {
        lock (_gate)
        {
            if (_state == ExitState.Completed)
                return _lastExit with { AlreadyHandled = true };
            if (_state == ExitState.Running)
                return SessionExitResult.Reentrant;

            _state = ExitState.Running;
        }

        FinalCaptureResult capture;
        try
        {
            capture = _finalCapture.CaptureForExit();
        }
        catch (Exception ex)
        {
            capture = FinalCaptureResult.Failure(
                $"Final GPose capture threw {ex.GetType().Name}: {ex.Message}");
        }

        var result = new SessionExitResult(
            capture,
            LegacyTeardownPending: true,
            AlreadyHandled: false);

        lock (_gate)
        {
            _lastExit = result;
            _hasLastExit = true;
            _state = ExitState.Completed;
        }

        return result;
    }
}
