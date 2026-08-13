using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Poser.Application.Lifecycle;
using Poser.Core;
using Poser.Game;
using Poser.Services;

namespace Poser.ContractTests;

public sealed class LifecycleContractTests
{
    [Fact]
    public void GPose_exit_captures_before_legacy_event_and_publishes_once()
    {
        var authoredState = true;
        var events = new List<string>();
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var capture = new RecordingCapturePort(() =>
        {
            Assert.True(authoredState);
            events.Add("capture");
            return FinalCaptureResult.Captured(1);
        });
        var coordinator = new SessionLifecycleCoordinator(capture);

        // This compatibility handler is already installed before GPoseService
        // observes the edge; the coordinator is still called first.
        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                {
                    authoredState = false;
                    events.Add("legacy-teardown");
                }
            });

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            coordinator);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, capture.CallCount);
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
        Assert.Equal(new[] { "capture", "legacy-teardown" }, events);
    }

    [Fact]
    public void GPose_exit_capture_failure_is_recorded_and_legacy_event_still_publishes_once()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var legacyExitCount = 0;
        var capture = new RecordingCapturePort(() =>
            throw new InvalidOperationException("capture failed"));
        var coordinator = new SessionLifecycleCoordinator(capture);

        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                    legacyExitCount++;
            });

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            coordinator);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, legacyExitCount);
        Assert.Equal(FinalCaptureStatus.Failure, coordinator.LastExit!.Value.Capture.Status);
        Assert.Contains("capture failed", coordinator.LastExit.Value.Capture.Detail);
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
    }

    [Fact]
    public void Exit_captures_before_legacy_teardown_mutates_authored_state()
    {
        var authoredState = "readable";
        var events = new List<string>();
        var capture = new RecordingCapturePort(() =>
        {
            Assert.Equal("readable", authoredState);
            events.Add("capture");
            return FinalCaptureResult.Captured(1);
        });
        var coordinator = new SessionLifecycleCoordinator(capture);

        var result = coordinator.OnGposeExit();
        authoredState = "detached";
        events.Add("legacy-teardown");

        Assert.Equal(FinalCaptureStatus.Captured, result.Capture.Status);
        Assert.True(result.LegacyTeardownPending);
        Assert.Equal(new[] { "capture", "legacy-teardown" }, events);
    }

    [Fact]
    public void Capture_failure_is_observable_but_does_not_block_legacy_teardown()
    {
        var events = new List<string>();
        var capture = new RecordingCapturePort(() =>
        {
            events.Add("capture");
            throw new InvalidOperationException("capture failed");
        });
        var coordinator = new SessionLifecycleCoordinator(capture);

        var result = coordinator.OnGposeExit();
        events.Add("legacy-teardown");

        Assert.Equal(FinalCaptureStatus.Failure, result.Capture.Status);
        Assert.Contains("capture failed", result.Capture.Detail);
        Assert.True(result.LegacyTeardownPending);
        Assert.Equal(new[] { "capture", "legacy-teardown" }, events);
    }

    [Fact]
    public void Reentrant_and_duplicate_exit_edges_capture_once()
    {
        SessionLifecycleCoordinator? coordinator = null;
        SessionExitResult? reentrant = null;
        var capture = new RecordingCapturePort(() =>
        {
            reentrant = coordinator!.OnGposeExit();
            return FinalCaptureResult.DispatchStarted(1);
        });
        coordinator = new SessionLifecycleCoordinator(capture);

        var first = coordinator.OnGposeExit();
        var duplicate = coordinator.OnGposeExit();

        Assert.Equal(1, capture.CallCount);
        Assert.NotNull(reentrant);
        Assert.True(reentrant!.Value.AlreadyHandled);
        Assert.True(duplicate.AlreadyHandled);
        Assert.Equal(FinalCaptureStatus.DispatchStarted, first.Capture.Status);

        coordinator.OnGposeEntered();
        coordinator.OnGposeExit();
        Assert.Equal(2, capture.CallCount);
    }

    [Fact]
    public void Lazy_capture_factory_does_not_construct_autosave_with_the_coordinator()
    {
        var services = new ServiceCollection();
        var constructed = 0;
        services.AddSingleton<FakeAutoSave>(_ =>
        {
            constructed++;
            return new FakeAutoSave();
        });
        services.AddSingleton<IFinalCapturePort>(sp =>
            new DelegateCapturePort(() => sp.GetRequiredService<FakeAutoSave>().Capture()));
        services.AddSingleton<ISessionLifecycleCoordinator, SessionLifecycleCoordinator>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var coordinator = provider.GetRequiredService<ISessionLifecycleCoordinator>();

        Assert.Equal(0, constructed);
        coordinator.OnGposeExit();
        Assert.Equal(1, constructed);
    }

    [Fact]
    public void Real_lazy_gpose_autosave_graph_has_no_cycle_and_captures_before_teardown()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = new EventBus();
        var authoredState = true;
        var captureCalls = 0;
        var constructed = 0;

        // Subscribe the teardown behavior before the lazy AutoSave factory is
        // ever resolved. The host path must still capture before this handler.
        eventBus.Subscribe<GPoseStateChangedEvent>(evt =>
        {
            if (!evt.IsGPosing)
                authoredState = false;
        });

        var services = new ServiceCollection();
        services.AddSingleton<IClientState>(clientState);
        services.AddSingleton<IFramework>(framework);
        services.AddSingleton<IEventBus>(eventBus);
        services.AddSingleton<IFinalCapturePort>(sp =>
            new DelegateCapturePort(() =>
            {
                var result = sp.GetRequiredService<IAutoSaveService>()
                    .CaptureForExit();
                return FinalCaptureResult.Captured(result.CapturedActors);
            }));
        services.AddSingleton<ISessionLifecycleCoordinator, SessionLifecycleCoordinator>();
        services.AddSingleton<IGPoseService, GPoseService>();
        services.AddSingleton<IAutoSaveService>(sp =>
        {
            constructed++;
            return new GraphAutoSave(
                sp.GetRequiredService<IGPoseService>(),
                () =>
                {
                    Assert.True(authoredState);
                    captureCalls++;
                    return AutoSaveCaptureResult.Captured(1);
                });
        });

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _ = provider.GetRequiredService<IGPoseService>();

        Assert.Equal(0, constructed);
        _ = provider.GetRequiredService<IAutoSaveService>();
        Assert.Equal(1, constructed);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, captureCalls);
        Assert.False(authoredState);
        Assert.NotNull(provider.GetRequiredService<ISessionLifecycleCoordinator>().LastExit);
    }

    private sealed class RecordingCapturePort(Func<FinalCaptureResult> capture)
        : IFinalCapturePort
    {
        public int CallCount { get; private set; }

        public FinalCaptureResult CaptureForExit()
        {
            CallCount++;
            return capture();
        }
    }

    private sealed class DelegateCapturePort(Func<FinalCaptureResult> capture)
        : IFinalCapturePort
    {
        public FinalCaptureResult CaptureForExit() => capture();
    }

    private sealed class FakeAutoSave
    {
        public FinalCaptureResult Capture() => FinalCaptureResult.Captured(1);
    }

    private sealed class GraphAutoSave : IAutoSaveService
    {
        private readonly Func<AutoSaveCaptureResult> _capture;

        public GraphAutoSave(
            IGPoseService gpose,
            Func<AutoSaveCaptureResult> capture)
        {
            _ = gpose;
            _capture = capture;
        }

        public string RootDirectory => string.Empty;

        public DateTime? LastSaveUtc => null;

        public int SaveNow(string reason) => 0;

        public AutoSaveCaptureResult CaptureForExit() => _capture();

        public void Dispose()
        {
        }
    }
}
