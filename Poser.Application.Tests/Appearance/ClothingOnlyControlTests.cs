using NSubstitute;
using Poser.Application.Appearance;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Appearance;

public sealed class ClothingOnlyControlTests
{
    private sealed class Fixture
    {
        public readonly IClothingOnlyRuntimePort Runtime = Substitute.For<IClothingOnlyRuntimePort>();
        public readonly ISessionGenerationSource Sessions = Substitute.For<ISessionGenerationSource>();
        public readonly IEventBus Events = Substitute.For<IEventBus>();
        public Action<GPoseExitingEvent> Exit = null!;
        public ClothingOnlyControl Control;
        public Fixture()
        {
            Sessions.ActiveSessionGeneration.Returns(SessionGeneration.New());
            Events.When(x => x.Subscribe(Arg.Any<Action<GPoseExitingEvent>>()))
                .Do(x => Exit = x.Arg<Action<GPoseExitingEvent>>());
            Control = new(Runtime, Sessions, Events);
        }
    }

    [Fact]
    public async Task Refusal_and_exception_have_one_awaitable_outcome()
    {
        var f = new Fixture();
        using var control = f.Control;
        f.Runtime.Apply(default, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("refused"));
        Assert.Equal("refused", await control.Apply(default, TestContext.Current.CancellationToken));
        f.Runtime.Apply(default, Arg.Any<CancellationToken>()).Returns(Task.FromException<string?>(new InvalidOperationException("broken")));
        Assert.Equal("Clothing-only failed: broken", await control.Apply(default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removal_cancels_only_the_removed_actor_and_exit_cancels_the_remaining_work()
    {
        var f = new Fixture();
        using var control = f.Control;
        var removed = new ActorId(Guid.NewGuid(), 1);
        var remaining = new ActorId(Guid.NewGuid(), 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Apply(Arg.Any<ActorId>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.Arg<ActorId>() == removed) started.TrySetResult();
            else remainingStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return (string?)null;
        });
        var first = control.Apply(removed, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queuedRemoved = control.Apply(removed, TestContext.Current.CancellationToken);
        var queuedRemaining = control.Apply(remaining, TestContext.Current.CancellationToken);
        control.Reconcile(actor => actor == remaining);
        Assert.Equal("Clothing-only was cancelled.", await first);
        Assert.Equal("Clothing-only was cancelled.", await queuedRemoved);
        await remainingStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(queuedRemaining.IsCompleted);
        f.Runtime.Received().Reconcile(Arg.Is<Func<ActorId, bool>>(predicate =>
            !predicate(removed) && predicate(remaining)));
        var queuedAtExit = control.Apply(remaining, TestContext.Current.CancellationToken);
        f.Exit(new());
        Assert.Equal("Clothing-only was cancelled.", await queuedRemaining);
        Assert.Equal("Clothing-only was cancelled.", await queuedAtExit);
        await f.Runtime.Received(1).Apply(removed, Arg.Any<CancellationToken>());
        await f.Runtime.Received(1).Apply(remaining, Arg.Any<CancellationToken>());
        f.Runtime.Received().Reconcile(Arg.Is<Func<ActorId, bool>>(predicate =>
            !predicate(removed) && !predicate(remaining)));
    }

    [Fact]
    public async Task Exit_refuses_new_requests_until_a_different_session_is_published()
    {
        var f = new Fixture();
        using var control = f.Control;
        // Production withdraws the token before publishing the exit event.
        f.Sessions.ActiveSessionGeneration.Returns((SessionGeneration?)null);
        f.Exit(new());
        Assert.Equal("Clothing-only requires an active GPose session.",
            await control.Apply(default, TestContext.Current.CancellationToken));
        await f.Runtime.DidNotReceive().Apply(default, Arg.Any<CancellationToken>());
        f.Sessions.ActiveSessionGeneration.Returns(SessionGeneration.New());
        f.Runtime.Apply(default, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        Assert.Null(await control.Apply(default, TestContext.Current.CancellationToken));
        await f.Runtime.Received(1).Apply(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_session_and_disposal_refuse_without_native_work()
    {
        var f = new Fixture();
        f.Sessions.ActiveSessionGeneration.Returns((SessionGeneration?)null);
        Assert.NotNull(await f.Control.Apply(default, TestContext.Current.CancellationToken));
        f.Sessions.ActiveSessionGeneration.Returns(SessionGeneration.New());
        f.Control.Dispose();
        Assert.NotNull(await f.Control.Apply(default, TestContext.Current.CancellationToken));
        await f.Runtime.DidNotReceive().Apply(Arg.Any<ActorId>(), Arg.Any<CancellationToken>());
    }
}
