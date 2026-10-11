using Poser.Domain.Operations;

namespace Poser.Application.Lifecycle;

/// <summary>
/// Host-provided bridge for the one lifecycle operation currently migrated:
/// synchronously reserve/capture the final immutable snapshot and join its
/// persistence worker before legacy GPose teardown runs. The result preserves
/// the capture compatibility fields and separately reports terminal persistence.
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
/// publication yet. Legacy teardown remains behind the existing GPose event
/// and is reported as pending here. The host invokes this synchronously from
/// its framework update callback; no cross-thread scheduling is performed.
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
            // Publish no token to capture, worker, or legacy observers after
            // this point: the exit edge owns the active token's lifetime.
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
