using System.Collections.Concurrent;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Game.Integration;

internal readonly record struct RedrawActor(ActorId Actor, SessionGeneration Session, nint Address, int Index);

internal interface IActorRedrawRuntime
{
    Task<T> OnFramework<T>(Func<T> action);
    RedrawActor? Resolve(ActorId actor);
    IDisposable Observe(Action<nint, int> redrawn);
    bool ProviderAvailable { get; }
    bool Ready(RedrawActor actor);
    IntegrationResult Request(ActorId actor);
}

/// <summary>One observed redraw at a time per exact actor. No native handles leave Game.</summary>
internal sealed class ActorRedrawBarrier(IActorRedrawRuntime runtime) : IDisposable
{
    private readonly ConcurrentDictionary<ActorId, Guid> _pending = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly CancellationTokenSource _lifetime = new();

    public async Task<IntegrationResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation)
    {
        var operation = Guid.NewGuid();
        if (!_pending.TryAdd(actor, operation))
            return IntegrationResult.Fail("A redraw is already pending for this actor.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            var target = await runtime.OnFramework(() => runtime.Resolve(actor)).WaitAsync(token);
            if (target is not { } exact)
                return IntegrationResult.Fail("The actor or GPose session is no longer available.");

            var observed = 0;
            var requesting = 0;
            // Subscribe BEFORE requesting: providers may complete synchronously.
            // Penumbra identifies completion by object address AND object-table index.
            using var observation = runtime.Observe((address, index) =>
            {
                if (Volatile.Read(ref requesting) != 0
                    && address == exact.Address && index == exact.Index
                    && _pending.TryGetValue(actor, out var current) && current == operation)
                    Interlocked.Exchange(ref observed, 1);
            });
            var requested = await runtime.OnFramework(() =>
            {
                if (token.IsCancellationRequested)
                    return IntegrationResult.Fail("The redraw was cancelled.");
                Interlocked.Exchange(ref requesting, 1);
                return runtime.Request(actor);
            }).WaitAsync(token);
            if (!requested.Success) return requested;

            string? failed = null;
            async Task<bool> Settled()
            {
                token.ThrowIfCancellationRequested();
                var state = await runtime.OnFramework(() =>
                {
                    if (!runtime.ProviderAvailable) return (Failed: "Penumbra became unavailable during redraw.", Ready: false);
                    if (runtime.Resolve(actor) != exact) return (Failed: "The actor or session changed during redraw.", Ready: false);
                    // The old body's drawable flag is irrelevant until Penumbra
                    // has acknowledged this actor's requested redraw.
                    return (Failed: (string?)null, Ready: Volatile.Read(ref observed) != 0 && runtime.Ready(exact));
                }).WaitAsync(token);
                failed = state.Failed;
                return state.Failed != null || state.Ready;
            }
            // The linked token is the real deadline; the poll's own bound only
            // answers if a probe outlives it without the token firing.
            if (!await FrameworkPoll.Until(Settled, timeout, PollInterval, token))
                throw new OperationCanceledException(token);
            return failed is { } failure ? IntegrationResult.Fail(failure) : IntegrationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return IntegrationResult.Fail(cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested
                ? "The redraw operation was cancelled."
                : $"The actor did not finish redrawing within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            return IntegrationResult.Fail($"The actor redraw failed: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(actor, out _);
        }
    }

    public void Dispose() => _lifetime.Cancel();
}
