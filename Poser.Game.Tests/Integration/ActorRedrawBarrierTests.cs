using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;
using Poser.Game.Integration;

namespace Poser.Game.Tests.Integration;

public sealed class ActorRedrawBarrierTests
{
    [Fact]
    public async Task Drawable_old_body_does_not_complete_without_matching_redraw_and_published_skeleton()
    {
        var runtime = new Runtime { SkeletonReady = true };
        using var barrier = new ActorRedrawBarrier(runtime);
        var pending = barrier.RedrawAndWait(runtime.Actor, TimeSpan.FromSeconds(2), default);
        Assert.False(pending.IsCompleted);
        runtime.Notify(999, 3);
        Assert.False(pending.IsCompleted);
        runtime.SkeletonReady = false;
        runtime.Notify(100, 3);
        Assert.False(pending.IsCompleted);
        runtime.SkeletonReady = true;
        Assert.True((await pending).Success);
        Assert.Equal(0, runtime.Subscriptions);
    }

    [Fact]
    public async Task Immediate_notification_is_observed_before_request_returns()
    {
        var runtime = new Runtime { NotifyDuringRequest = true, SkeletonReady = true };
        using var barrier = new ActorRedrawBarrier(runtime);
        Assert.True((await barrier.RedrawAndWait(runtime.Actor, TimeSpan.FromSeconds(1), default)).Success);
        Assert.Equal(0, runtime.Subscriptions);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("actor")]
    [InlineData("session")]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task Failure_releases_observation_and_next_operation_can_start(string failure)
    {
        var runtime = new Runtime();
        using var barrier = new ActorRedrawBarrier(runtime);
        using var cancel = new CancellationTokenSource();
        var pending = barrier.RedrawAndWait(runtime.Actor,
            TimeSpan.FromMilliseconds(failure == "timeout" ? 75 : 2000), cancel.Token);
        Assert.False((await barrier.RedrawAndWait(runtime.Actor, TimeSpan.FromSeconds(1), default)).Success);
        switch (failure)
        {
            case "provider": runtime.ProviderAvailable = false; break;
            case "actor": runtime.Gone = true; break;
            case "session": runtime.Session = SessionGeneration.New(); break;
            case "cancel": cancel.Cancel(); break;
        }
        Assert.False((await pending).Success);
        Assert.Equal(0, runtime.Subscriptions);
        runtime.ProviderAvailable = true;
        runtime.Gone = false;
        runtime.NotifyDuringRequest = true;
        runtime.SkeletonReady = true;
        Assert.True((await barrier.RedrawAndWait(runtime.Actor, TimeSpan.FromSeconds(1), default)).Success);
    }

    private sealed class Runtime : IActorRedrawRuntime
    {
        public readonly ActorId Actor = new(Guid.NewGuid(), 0);
        public SessionGeneration Session = SessionGeneration.New();
        public bool ProviderAvailable { get; set; } = true;
        public bool SkeletonReady;
        public bool Gone;
        public bool NotifyDuringRequest;
        public int Subscriptions;
        private Action<nint, int>? _redrawn;
        public Task<T> OnFramework<T>(Func<T> action) => Task.FromResult(action());
        public RedrawActor? Resolve(ActorId actor) => Gone ? null : new(actor, Session, 100, 3);
        public bool Ready(RedrawActor actor) => SkeletonReady;
        public IDisposable Observe(Action<nint, int> redrawn)
        {
            Subscriptions++;
            _redrawn += redrawn;
            return new Subscription(() => { Subscriptions--; _redrawn -= redrawn; });
        }
        public IntegrationPortResult Request(ActorId actor)
        {
            Assert.Equal(1, Subscriptions);
            if (NotifyDuringRequest) Notify(100, 3);
            return IntegrationPortResult.Ok();
        }
        public void Notify(nint address, int index) => _redrawn?.Invoke(address, index);
        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
