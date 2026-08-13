namespace Poser.Application.Lifecycle;

/// <summary>
/// Host-provided bridge for the one lifecycle operation currently migrated:
/// detach the final immutable capture before legacy GPose teardown runs.
/// </summary>
public interface IFinalCapturePort
{
    FinalCaptureResult CaptureForExit();
}

/// <summary>
/// Coordinates the pre-publish capture edge. Legacy teardown remains behind
/// the existing GPose event and is reported as pending here.
/// </summary>
public interface ISessionLifecycleCoordinator
{
    SessionExitResult? LastExit { get; }

    void OnGposeEntered();

    SessionExitResult OnGposeExit();
}

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
/// Exactly-once, reentrancy-safe owner of the pre-publish capture phase. It
/// deliberately does not own cancellation, restoration, native teardown,
/// persistence joining, or detached-fact publication yet.
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
