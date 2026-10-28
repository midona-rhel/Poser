using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;

namespace Poser.Application.Appearance;

/// <summary>Runs clothing-only redraw requests from the UI, scene loader, and
/// debug bridge. Cancels requests when their actor leaves or GPose ends.</summary>
public sealed class ClothingOnlyControl : IInvisibleSkinService, IDisposable
{
    private readonly IClothingOnlyRuntimePort _runtime;
    private readonly ISessionGenerationSource _sessions;
    private readonly IEventBus _events;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<CancellationTokenSource, ActorId> _pending = new();
    private bool _closed;

    public ClothingOnlyControl(IClothingOnlyRuntimePort runtime, ISessionGenerationSource sessions, IEventBus events)
    {
        _runtime = runtime;
        _sessions = sessions;
        _events = events;
        events.Subscribe<GPoseExitingEvent>(OnExit);
    }

    public bool IsHuman(ActorId actor) => _runtime.IsHuman(actor);
    public bool IsApplied(ActorId actor) => _runtime.IsApplied(actor);

    // Scene loads record their own history. Interactive requests rely on the
    // redraw service to restore the previous state.
    public async Task<string?> Apply(ActorId actor, CancellationToken cancellation)
    {
        var session = _sessions.ActiveSessionGeneration;
        if (session == null) return "Clothing-only requires an active GPose session.";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (_gate)
        {
            if (_closed) return "Clothing-only is shutting down.";
            _pending.Add(operation, actor);
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

    public void Reconcile(Func<ActorId, bool> isCurrent)
    {
        // Cancelling an actor's work leaves requests for other actors alone.
        // Each operation removes and disposes its token after native cleanup.
        lock (_gate)
            foreach (var (operation, actor) in _pending.ToArray())
                if (!isCurrent(actor)) operation.Cancel();
        _runtime.Reconcile(isCurrent);
    }

    private void OnExit(GPoseExitingEvent _) => Reconcile(_ => false);

    public void Dispose()
    {
        lock (_gate) _closed = true;
        _events.Unsubscribe<GPoseExitingEvent>(OnExit);
        Reconcile(_ => false);
    }
}
