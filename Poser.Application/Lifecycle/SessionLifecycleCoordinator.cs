using Poser.Domain.Operations;

namespace Poser.Application.Lifecycle;

/// <summary>
/// Finishes the final snapshot work before the caller begins GPose teardown.
/// The result distinguishes capture from completed persistence.
/// </summary>
public interface IFinalCapturePort
{
    /// <summary>
    /// Attempts exactly one synchronous final capture and returns only after the
    /// owned autosave worker has reached a terminal result.
    /// </summary>
    FinalCaptureResult CaptureForExit();
}

public interface ISessionGenerationSource
{
    /// <summary>Current accepted GPose session identity, or null when inactive.</summary>
    SessionGeneration? ActiveSessionGeneration { get; }
}

/// <summary>
/// Result of final capture before the exit event is sent.
/// LegacyTeardownPending does not mean teardown has finished.
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
/// Starts session IDs and requests final capture at most once per exit.
/// The host calls it on the framework thread before publishing the teardown event.
/// Cancellation and native cleanup remain the responsibility of that event's subscribers.
/// </summary>
public sealed class SessionLifecycleCoordinator : ISessionGenerationSource
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
    private SessionGeneration? _activeSessionGeneration;
    private bool _unloadInvalidated;

    public SessionLifecycleCoordinator(IFinalCapturePort finalCapture)
    {
        _finalCapture = finalCapture;
    }

    public SessionGeneration? ActiveSessionGeneration
    {
        get
        {
            lock (_gate)
                return _activeSessionGeneration;
        }
    }

    /// <summary>Marks the start of a new GPose session for this coordinator.</summary>
    public SessionGeneration? OnGposeEntered()
    {
        lock (_gate)
        {
            if (_unloadInvalidated || _state == ExitState.Running)
                return null;

            if (_activeSessionGeneration is { } active)
                return active;

            _activeSessionGeneration = SessionGeneration.New();
            _state = ExitState.Ready;
            _hasLastExit = false;
            return _activeSessionGeneration;
        }
    }

    /// <summary>
    /// Permanently closes session admission for unload or failed framework
    /// dispatch. This operation is thread-safe and performs no capture or
    /// native/event work.
    /// </summary>
    public void InvalidateForUnload()
    {
        lock (_gate)
        {
            _activeSessionGeneration = null;
            _unloadInvalidated = true;
        }
    }

    /// <summary>
    /// Attempts the final capture for the current exit edge at most once and
    /// returns before the caller publishes the legacy exit event.
    /// </summary>
    public SessionExitResult OnGposeExit()
    {
        lock (_gate)
        {
            if (_unloadInvalidated)
                return _hasLastExit
                    ? _lastExit with { AlreadyHandled = true }
                    : SessionExitResult.Reentrant;
            if (_state == ExitState.Completed)
                return _lastExit with { AlreadyHandled = true };
            if (_state == ExitState.Running)
                return SessionExitResult.Reentrant;

            _state = ExitState.Running;
            // New operations must see the session as inactive before final capture
            // and teardown begin.
            _activeSessionGeneration = null;
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
