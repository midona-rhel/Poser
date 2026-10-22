using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>One admission, lifetime and outcome owner for UI, scene and bridge requests.</summary>
public sealed class ClothingOnlyControl : IInvisibleSkinService, IDisposable
{
    private readonly IClothingOnlyRuntimePort _runtime;
    private readonly ISessionGenerationSource _sessions;
    private readonly IEventBus _events;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<CancellationTokenSource> _pending = new();
    private bool _closed;
    private Poser.Domain.Operations.SessionGeneration? _exitingSession;

    public ClothingOnlyControl(IClothingOnlyRuntimePort runtime, ISessionGenerationSource sessions, IEventBus events)
    {
        _runtime = runtime;
        _sessions = sessions;
        _events = events;
        events.Subscribe<GPoseExitingEvent>(OnExit);
    }

    public bool IsHuman(ActorId actor) => _runtime.IsHuman(actor);
    public bool IsApplied(ActorId actor) => _runtime.IsApplied(actor);

    // No separate history here: scene load owns its transaction; interactive
    // clothing-only retains the existing redraw-to-restore behavior.
    public async Task<string?> Apply(ActorId actor, CancellationToken cancellation)
    {
        var session = _sessions.ActiveSessionGeneration;
        if (session == null) return "Clothing-only requires an active GPose session.";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (_gate)
        {
            if (_closed) return "Clothing-only is shutting down.";
            if (_exitingSession == session) return "Clothing-only is unavailable during GPose exit.";
            _pending.Add(operation);
        }
        bool entered = false;
        try
        {
            await _applyGate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            operation.Token.ThrowIfCancellationRequested();
            if (_sessions.ActiveSessionGeneration != session)
                return "The GPose session changed before clothing-only could run.";
            var failure = await _runtime.Apply(actor, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return failure;
        }
        catch (OperationCanceledException) { return "Clothing-only was cancelled."; }
        catch (Exception ex) { return $"Clothing-only failed: {ex.Message}"; }
        finally
        {
            if (entered) _applyGate.Release();
            lock (_gate) _pending.Remove(operation);
        }
    }

    private void OnExit(GPoseExitingEvent _)
    {
        // Exit notification precedes clearing the published session. Refuse new
        // requests in that interval as well as cancelling those already admitted.
        lock (_gate)
        {
            _exitingSession = _sessions.ActiveSessionGeneration;
            CancelPending();
        }
    }

    private void CancelPending()
    {
        // Keep sources alive while cancelling; each operation removes and disposes
        // its own source only after native cleanup finishes.
        lock (_gate)
            foreach (var operation in _pending.ToArray()) operation.Cancel();
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        _events.Unsubscribe<GPoseExitingEvent>(OnExit);
        CancelPending();
    }
}
