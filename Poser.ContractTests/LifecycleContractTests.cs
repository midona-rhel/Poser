extern alias ProductionPoser;

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Poser.Application.Lifecycle;
using Poser.Core;
using Poser.Entities;
using Poser.Files;
using Poser.Game;
using Poser.Services;
using ProductionPoser::Poser.Composition;

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
        var log = Substitute.For<IPluginLog>();
        framework.IsInFrameworkUpdateThread.Returns(true);
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
            log,
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
        var log = Substitute.For<IPluginLog>();
        var legacyExitCount = 0;
        framework.IsInFrameworkUpdateThread.Returns(true);
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
            log,
            coordinator);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, legacyExitCount);
        Assert.Equal(FinalCaptureStatus.Failure, coordinator.LastExit!.Value.Capture.Status);
        Assert.Contains("capture failed", coordinator.LastExit.Value.Capture.Detail);
        log.Received(1).Error(
            Arg.Is<string>(message => message.Contains("capture failed")));
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
    }

    [Fact]
    public void GPose_observation_requires_the_framework_update_thread()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var coordinator = new SessionLifecycleCoordinator(
            new RecordingCapturePort(() => FinalCaptureResult.Captured(1)));

        clientState.IsGPosing.Returns(true);
        framework.IsInFrameworkUpdateThread.Returns(false);

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        eventBus.DidNotReceive().Publish(Arg.Any<GPoseStateChangedEvent>());
        Assert.Null(coordinator.LastExit);
        log.Received(1).Error(
            Arg.Is<string>(message => message.Contains("framework update thread")));
    }

    [Fact]
    public void GPose_exit_reentry_on_the_same_framework_thread_publishes_once()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var capture = new RecordingCapturePort(
            () => FinalCaptureResult.DispatchStarted(1));
        var coordinator = new SessionLifecycleCoordinator(capture);
        var reentered = false;
        framework.IsInFrameworkUpdateThread.Returns(true);

        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!reentered && !callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                {
                    reentered = true;
                    framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
                }
            });

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, capture.CallCount);
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
    }

    [Fact]
    public void Concurrent_framework_observations_do_not_duplicate_the_exit_edge()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var capture = new RecordingCapturePort(
            () => FinalCaptureResult.DispatchStarted(1));
        var coordinator = new SessionLifecycleCoordinator(capture);
        framework.IsInFrameworkUpdateThread.Returns(true);

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);

        Parallel.Invoke(
            () => framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework),
            () => framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework));

        Assert.Equal(1, capture.CallCount);
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
    }

    [Fact]
    public void Plugin_unload_uses_the_same_exit_edge_and_publishes_false_once()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var capture = new RecordingCapturePort(
            () => FinalCaptureResult.Captured(1));
        var coordinator = new SessionLifecycleCoordinator(capture);
        var exitEvents = 0;
        framework.IsInFrameworkUpdateThread.Returns(true);
        clientState.IsGPosing.Returns(true);

        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                    exitEvents++;
            });

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        gpose.ExitForUnload();
        gpose.ExitForUnload();

        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, exitEvents);
        eventBus.Received(1).Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => !evt.IsGPosing));
        Assert.Equal(FinalCaptureStatus.Captured, coordinator.LastExit!.Value.Capture.Status);
    }

    [Fact]
    public void Plugin_unload_failure_still_publishes_false_once()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var coordinator = new SessionLifecycleCoordinator(
            new RecordingCapturePort(
                () => throw new IOException("unload capture failed")));
        var exitEvents = 0;
        framework.IsInFrameworkUpdateThread.Returns(true);
        clientState.IsGPosing.Returns(true);

        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                    exitEvents++;
            });

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        gpose.ExitForUnload();

        Assert.Equal(1, exitEvents);
        Assert.Equal(FinalCaptureStatus.Failure, coordinator.LastExit!.Value.Capture.Status);
        Assert.Contains("unload capture failed", coordinator.LastExit.Value.Capture.Detail);
    }

    [Fact]
    public void Framework_update_after_unload_cannot_reopen_or_publish_a_true_edge()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var capture = new RecordingCapturePort(
            () => FinalCaptureResult.Captured(1));
        var coordinator = new SessionLifecycleCoordinator(capture);
        framework.IsInFrameworkUpdateThread.Returns(true);
        clientState.IsGPosing.Returns(true);

        using var gpose = new GPoseService(
            clientState,
            framework,
            eventBus,
            log,
            coordinator);

        gpose.ExitForUnload();
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, capture.CallCount);
        eventBus.Received(1).Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => !evt.IsGPosing));
        eventBus.DidNotReceive().Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => evt.IsGPosing));
        Assert.Equal(FinalCaptureStatus.Captured, coordinator.LastExit!.Value.Capture.Status);
    }

    [Fact]
    public void Faulted_framework_unload_dispatch_still_disposes_provider()
    {
        var framework = Substitute.For<IFramework>();
        var gpose = Substitute.For<IGPoseService>();
        var log = Substitute.For<IPluginLog>();
        framework.IsInFrameworkUpdateThread.Returns(false);
        framework.RunOnFrameworkThread(Arg.Any<Action>())
            .Returns(Task.FromException(new InvalidOperationException("dispatcher faulted")));

        using var provider = new ServiceCollection()
            .AddSingleton<DisposalProbe>()
            .BuildServiceProvider();
        var probe = provider.GetRequiredService<DisposalProbe>();

        ProductionPoser::Poser.Poser.DisposeProviderAfterFrameworkExit(
            provider,
            framework,
            gpose,
            log,
            cleanup: static () => { });

        Assert.True(probe.Disposed);
        log.Received(1).Error(
            Arg.Is<string>(message => message.Contains("dispatcher faulted")));
    }

    [Fact]
    public void Canceled_framework_unload_dispatch_still_disposes_provider()
    {
        var framework = Substitute.For<IFramework>();
        var gpose = Substitute.For<IGPoseService>();
        var log = Substitute.For<IPluginLog>();
        framework.IsInFrameworkUpdateThread.Returns(false);
        framework.RunOnFrameworkThread(Arg.Any<Action>())
            .Returns(Task.FromCanceled(new CancellationToken(canceled: true)));

        using var provider = new ServiceCollection()
            .AddSingleton<DisposalProbe>()
            .BuildServiceProvider();
        var probe = provider.GetRequiredService<DisposalProbe>();

        ProductionPoser::Poser.Poser.DisposeProviderAfterFrameworkExit(
            provider,
            framework,
            gpose,
            log,
            cleanup: static () => { });

        Assert.True(probe.Disposed);
        log.Received(1).Error(
            Arg.Is<string>(message => message.Contains("canceled")));
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
    public void Partial_capture_failure_is_not_reported_as_completed()
    {
        var result = FinalCaptureResult.Failure(
            "one actor failed",
            capturedActors: 1,
            dispatchAccepted: true);

        Assert.Equal(FinalCaptureStatus.Failure, result.Status);
        Assert.Equal(1, result.CapturedActors);
        Assert.True(result.DispatchAccepted);
        Assert.False(result.CaptureCompleted);
    }

    [Fact]
    public void Autosave_terminal_health_is_carried_into_the_application_receipt()
    {
        var service = Substitute.For<IAutoSaveService>();
        service.CaptureForExit().Returns(AutoSaveCaptureResult.DispatchStarted(2));
        service.CompleteForExit().Returns(
            AutoSaveTerminalResult.RecoveryRequired("worker failed"));
        service.LastHealthRecord.Returns(JsonSerializer.Deserialize<AutoSaveHealthRecord>("""
            {
              "OperationId": "op-1",
              "Reason": "gpose-exit",
              "Status": 5,
              "CreatedUtc": "2026-08-13T10:00:00Z",
              "UpdatedUtc": "2026-08-13T10:01:00Z",
              "IntendedActors": 2,
              "WrittenActors": 1,
              "AffectedPaths": ["a.pose", "b.pose"],
              "FailurePhase": "ActorWrite",
              "Detail": "first actor failed",
              "RecoveryEvidencePaths": ["a.tmp"],
              "RecoveryEntries": [
                {
                  "OperationId": "cancel-1",
                  "Reason": "interval",
                  "Status": 5,
                  "CreatedUtc": "2026-08-13T10:00:00Z",
                  "UpdatedUtc": "2026-08-13T10:00:01Z",
                  "IntendedActors": 2,
                  "WrittenActors": 0,
                  "AffectedPaths": ["cancel.pose"],
                  "FailurePhase": "HealthTransition",
                  "Detail": "cancel failed",
                  "RecoveryEvidencePaths": ["cancel.tmp"]
                }
              ],
              "RecoveryOverflowCount": 3
            }
            """));

        var port = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(
            () => service);
        var result = port.CaptureForExit();

        Assert.Equal(FinalPersistenceStatus.RecoveryRequired, result.Persistence);
        Assert.Equal("worker failed", result.PersistenceDetail);
        Assert.NotNull(result.PersistenceEvidence);
        Assert.Equal("op-1", result.PersistenceEvidence!.OperationId);
        Assert.Equal(1, result.PersistenceEvidence.WrittenActors);
        Assert.Equal("ActorWrite", result.PersistenceEvidence.FailurePhase);
        Assert.Equal(new[] { "a.tmp" }, result.PersistenceEvidence.RecoveryEvidencePaths);
        Assert.Equal(3, result.PersistenceEvidence.RecoveryOverflowCount);
        var recoveryEntry = Assert.Single(result.PersistenceEvidence.RecoveryEntries);
        Assert.Equal("cancel-1", recoveryEntry.OperationId);
        Assert.Equal("HealthTransition", recoveryEntry.FailurePhase);
        Assert.Equal(new[] { "cancel.tmp" }, recoveryEntry.RecoveryEvidencePaths);
        Assert.Equal(
            new FinalCaptureResult(
                FinalCaptureStatus.DispatchStarted,
                2,
                null,
                true,
                FinalPersistenceStatus.RecoveryRequired,
                "worker failed"),
            result);
        service.Received(1).CaptureForExit();
        service.Received(1).CompleteForExit();
    }

    [Fact]
    public void Application_recovery_evidence_is_bounded_and_counts_discarded_entries()
    {
        var entries = Enumerable.Range(1, 6).Select(index =>
            new FinalPersistenceRecoveryEntry(
                new string((char)('a' + index), 300),
                new string('r', 300),
                FinalPersistenceStatus.RecoveryRequired,
                DateTime.UtcNow,
                DateTime.UtcNow,
                9000,
                9000,
                Enumerable.Range(0, 300).Select(_ => new string('p', 2000)),
                new string('f', 300),
                new string('d', 5000),
                Enumerable.Range(0, 300).Select(_ => new string('e', 2000))))
            .ToArray();

        var evidence = new FinalPersistenceEvidence(
            "op", "final", FinalPersistenceStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow, 2, 1, null, "HealthTransition", "detail", null,
            entries);

        Assert.Equal(4, evidence.RecoveryEntries.Count);
        Assert.Equal(2, evidence.RecoveryOverflowCount);
        var first = evidence.RecoveryEntries[0];
        Assert.Equal(128, first.OperationId.Length);
        Assert.Equal(128, first.Reason.Length);
        Assert.Equal(8192, first.IntendedActors);
        Assert.Equal(8192, first.WrittenActors);
        Assert.Equal(256, first.AffectedPaths.Count);
        Assert.Equal(1024, first.AffectedPaths[0].Length);
        Assert.Equal(128, first.FailurePhase!.Length);
        Assert.Equal(4096, first.Detail!.Length);
        Assert.Equal(256, first.RecoveryEvidencePaths.Count);
        Assert.Equal(1024, first.RecoveryEvidencePaths[0].Length);
    }

    [Fact]
    public void Adapter_maps_every_health_status_to_an_explicit_application_status()
    {
        foreach (var status in Enum.GetValues<AutoSaveHealthStatus>())
        {
            var service = Substitute.For<IAutoSaveService>();
            service.CaptureForExit().Returns(AutoSaveCaptureResult.DispatchStarted(1));
            service.CompleteForExit().Returns(AutoSaveTerminalResult.RecoveryRequired("terminal"));
            service.LastHealthRecord.Returns(JsonSerializer.Deserialize<AutoSaveHealthRecord>($$"""
                {
                  "OperationId": "op-{{(int)status}}", "Reason": "test", "Status": {{(int)status}},
                  "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:01:00Z",
                  "IntendedActors": 1, "WrittenActors": 0, "RecoveryEntries": [{
                    "OperationId": "entry-{{(int)status}}", "Reason": "test", "Status": {{(int)status}},
                    "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:01:00Z",
                    "IntendedActors": 1, "WrittenActors": 0
                  }]
                }
                """));

            var result = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(() => service).CaptureForExit();
            var entry = Assert.Single(result.PersistenceEvidence!.RecoveryEntries);
            var expected = status switch
            {
                AutoSaveHealthStatus.Pending or AutoSaveHealthStatus.Queued or AutoSaveHealthStatus.DispatchAccepted => FinalPersistenceStatus.Pending,
                AutoSaveHealthStatus.Written => FinalPersistenceStatus.Written,
                AutoSaveHealthStatus.Cleaned => FinalPersistenceStatus.Cleaned,
                AutoSaveHealthStatus.RecoveryRequired => FinalPersistenceStatus.RecoveryRequired,
                AutoSaveHealthStatus.Cancelled => FinalPersistenceStatus.Cancelled,
                _ => throw new Xunit.Sdk.XunitException($"Unhandled status {status}"),
            };
            Assert.Equal(expected, entry.Status);
        }
    }

    [Fact]
    public void Adapter_rejects_unknown_terminal_status_instead_of_claiming_not_attempted()
    {
        var service = Substitute.For<IAutoSaveService>();
        service.CaptureForExit().Returns(AutoSaveCaptureResult.NotCaptured());
        service.CompleteForExit().Returns(
            new AutoSaveTerminalResult((AutoSaveTerminalStatus)999, "invalid"));

        var port = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(() => service);
        Assert.Throws<ArgumentOutOfRangeException>(() => port.CaptureForExit());
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
    public void Lazy_capture_port_defers_service_stub_construction()
    {
        var services = new ServiceCollection();
        var constructed = 0;
        services.AddSingleton<DeferredCaptureServiceStub>(_ =>
        {
            constructed++;
            return new DeferredCaptureServiceStub();
        });
        services.AddSingleton<IFinalCapturePort>(sp =>
            new DelegateCapturePort(() =>
                sp.GetRequiredService<DeferredCaptureServiceStub>().Capture()));
        services.AddSingleton<ISessionLifecycleCoordinator, SessionLifecycleCoordinator>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var coordinator = provider.GetRequiredService<ISessionLifecycleCoordinator>();

        Assert.Equal(0, constructed);
        coordinator.OnGposeExit();
        Assert.Equal(1, constructed);
    }

    [Fact]
    public void Production_registration_defers_autosave_until_exit_and_orders_attempt_before_legacy_event()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var log = Substitute.For<IPluginLog>();
        var pluginInterface = Substitute.For<IDalamudPluginInterface>();
        var configRoot = Path.Combine(
            Path.GetTempPath(), "poser-lifecycle-composition-tests", Guid.NewGuid().ToString("N"));
        var autoSaveRoot = Path.Combine(configRoot, "AutoSaves");
        pluginInterface.GetPluginConfigDirectory().Returns(configRoot);
        framework.IsInFrameworkUpdateThread.Returns(true);

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClientState>(clientState);
            services.AddSingleton<IFramework>(framework);
            services.AddSingleton<IPluginLog>(log);
            services.AddSingleton<IDalamudPluginInterface>(pluginInterface);
            services.AddPoserCore();
            services.AddPoserFeatures();
            var actorManager = Substitute.For<IActorManager>();
            actorManager.Actors.Returns(Array.Empty<IActor>());
            services.AddSingleton<IActorManager>(actorManager);

            using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
            var eventBus = provider.GetRequiredService<IEventBus>();
            var coordinator = provider.GetRequiredService<ISessionLifecycleCoordinator>();
            var configuration = provider.GetRequiredService<global::Poser.Config.ConfigurationService>();
            configuration.Config.AutoSave.Enabled = true;
            var legacyObserved = false;
            // The production registration is exercised with an empty actor
            // manager so this test does not construct unrelated native scene
            // owners; the final capture attempt still runs and reports no
            // authored actors before the pre-subscribed legacy handler.
            eventBus.Subscribe<GPoseStateChangedEvent>(evt =>
            {
                if (!evt.IsGPosing)
                {
                    legacyObserved = true;
                    Assert.True(coordinator.LastExit.HasValue);
                    Assert.Equal(
                        FinalCaptureStatus.NotCaptured,
                        coordinator.LastExit.Value.Capture.Status);
                    Assert.Equal(
                        "No actors had authored edits.",
                        coordinator.LastExit.Value.Capture.Detail);
                    Assert.True(coordinator.LastExit.Value.LegacyTeardownPending);
                }
            });

            _ = provider.GetRequiredService<IGPoseService>();
            Assert.False(Directory.Exists(autoSaveRoot));

            clientState.IsGPosing.Returns(true);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
            Assert.False(Directory.Exists(autoSaveRoot));

            clientState.IsGPosing.Returns(false);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

            Assert.True(Directory.Exists(autoSaveRoot));
            Assert.True(legacyObserved);
            Assert.Equal(
                FinalCaptureStatus.NotCaptured,
                coordinator.LastExit!.Value.Capture.Status);
            Assert.Equal(
                "No actors had authored edits.",
                coordinator.LastExit.Value.Capture.Detail);
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
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

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class DeferredCaptureServiceStub
    {
        public FinalCaptureResult Capture() => FinalCaptureResult.Captured(1);
    }

}
