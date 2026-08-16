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
using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;
using Poser.Core;
using Poser.Entities;
using Poser.Files;
using Poser.Game;
using Poser.Game.Posing;
using Poser.Services;
using ProductionPoser::Poser.Composition;

namespace Poser.ContractTests;

public sealed class LifecycleContractTests
{
    [Fact]
    public void Gpose_exit_captures_before_legacy_teardown_and_publishes_false_once()
    {
        var authored = true;
        var history = new List<string>();
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        framework.IsInFrameworkUpdateThread.Returns(true);
        var capture = new RecordingCapturePort(() =>
        {
            Assert.True(authored);
            history.Add("capture");
            return FinalCaptureResult.Captured(1);
        });
        var coordinator = new SessionLifecycleCoordinator(capture);
        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                {
                    authored = false;
                    history.Add("legacy-teardown");
                }
            });

        using var gpose = new GPoseService(
            clientState, framework, eventBus, log, coordinator);
        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

        Assert.Equal(1, capture.CallCount);
        Assert.Equal(new[] { "capture", "legacy-teardown" }, history);
        Assert.Equal(FinalCaptureStatus.Captured, coordinator.LastExit!.Value.Capture.Status);
        eventBus.Received(2).Publish(Arg.Any<GPoseStateChangedEvent>());
        eventBus.Received(1).Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => !evt.IsGPosing));
    }

    [Fact]
    public void Capture_failure_and_partial_failure_remain_observable_while_teardown_runs()
    {
        foreach (var failure in new[]
        {
            new InvalidOperationException("capture failed"),
            new InvalidOperationException("one actor failed"),
        })
        {
            var clientState = Substitute.For<IClientState>();
            var framework = Substitute.For<IFramework>();
            var eventBus = Substitute.For<IEventBus>();
            var log = Substitute.For<IPluginLog>();
            var history = new List<string>();
            var published = 0;
            framework.IsInFrameworkUpdateThread.Returns(true);
            var capture = new RecordingCapturePort(() =>
            {
                history.Add("capture");
                if (failure.Message == "capture failed")
                    throw failure;
                return FinalCaptureResult.Failure(
                    failure.Message, capturedActors: 1, dispatchAccepted: true);
            });
            var coordinator = new SessionLifecycleCoordinator(capture);
            eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
                .Do(callInfo =>
                {
                    published++;
                    if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                        history.Add("legacy-teardown");
                });

            using var gpose = new GPoseService(
                clientState, framework, eventBus, log, coordinator);
            clientState.IsGPosing.Returns(true);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
            clientState.IsGPosing.Returns(false);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

            var exit = coordinator.LastExit!.Value;
            Assert.Equal(FinalCaptureStatus.Failure, exit.Capture.Status);
            Assert.Equal(
                failure.Message == "capture failed" ? 0 : 1,
                exit.Capture.CapturedActors);
            Assert.Equal(
                failure.Message != "capture failed",
                exit.Capture.DispatchAccepted);
            Assert.False(exit.Capture.CaptureCompleted);
            Assert.Equal(new[] { "capture", "legacy-teardown" }, history);
            Assert.Equal(2, published);
            if (failure.Message == "capture failed")
                log.Received(1).Error(Arg.Is<string>(message => message.Contains(failure.Message)));
        }
    }

    [Fact]
    public void Framework_admission_reentry_and_concurrent_duplicate_exit_edges_are_single()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        SessionLifecycleCoordinator? coordinator = null;
        SessionExitResult? captureReentry = null;
        var capture = new RecordingCapturePort(() =>
        {
            captureReentry = coordinator!.OnGposeExit();
            return FinalCaptureResult.DispatchStarted(1);
        });
        coordinator = new SessionLifecycleCoordinator(capture);
        var reentered = false;
        var trueEvents = 0;
        var falseEvents = 0;
        framework.IsInFrameworkUpdateThread.Returns(false);
        clientState.IsGPosing.Returns(true);

        using var gpose = new GPoseService(
            clientState, framework, eventBus, log, coordinator);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        Assert.Null(coordinator.ActiveSessionGeneration);
        log.Received(1).Error(
            Arg.Is<string>(message => message.Contains("framework update thread")));

        framework.IsInFrameworkUpdateThread.Returns(true);
        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                var evt = callInfo.Arg<GPoseStateChangedEvent>();
                if (evt.IsGPosing)
                    trueEvents++;
                else
                {
                    falseEvents++;
                    if (!reentered)
                    {
                        reentered = true;
                        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
                    }
                }
            });
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        clientState.IsGPosing.Returns(false);

        Parallel.Invoke(
            () => framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework),
            () => framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework));

        Assert.True(captureReentry.HasValue);
        Assert.True(captureReentry.Value.AlreadyHandled);
        Assert.True(coordinator.OnGposeExit().AlreadyHandled);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, trueEvents);
        Assert.Equal(1, falseEvents);
    }

    [Fact]
    public void Unload_reuses_the_exit_edge_and_cannot_reopen_after_disposal()
    {
        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        var capture = new RecordingCapturePort(
            () => throw new IOException("unload capture failed"));
        var coordinator = new SessionLifecycleCoordinator(capture);
        var published = 0;
        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(_ => published++);
        framework.IsInFrameworkUpdateThread.Returns(true);
        clientState.IsGPosing.Returns(true);

        var gpose = new GPoseService(
            clientState, framework, eventBus, log, coordinator);
        gpose.ExitForUnload();
        gpose.ExitForUnload();
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        gpose.Dispose();
        gpose.Dispose();

        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, published);
        eventBus.Received(1).Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => !evt.IsGPosing));
        eventBus.DidNotReceive().Publish(
            Arg.Is<GPoseStateChangedEvent>(evt => evt.IsGPosing));
        Assert.Equal(FinalCaptureStatus.Failure, coordinator.LastExit!.Value.Capture.Status);
        Assert.Contains("unload capture failed", coordinator.LastExit.Value.Capture.Detail);
        Assert.Null(coordinator.ActiveSessionGeneration);
    }

    [Fact]
    public void Framework_unload_dispatch_fault_or_cancel_still_disposes_provider_and_fails_import()
    {
        foreach (var dispatch in new[] { "faulted", "canceled" })
        {
            using var app = new PoseImportCaptureHarness();
            var receipts = new List<OperationReceipt>();
            Assert.True(app.BeginResetImport(receipts.Add).Success);
            var framework = Substitute.For<IFramework>();
            var gpose = Substitute.For<IGPoseService>();
            var lifecycle = Substitute.For<ISessionLifecycleCoordinator>();
            var log = Substitute.For<IPluginLog>();
            framework.IsInFrameworkUpdateThread.Returns(false);
            framework.RunOnFrameworkThread(Arg.Any<Action>())
                .Returns(dispatch == "faulted"
                    ? Task.FromException(new InvalidOperationException("dispatcher faulted"))
                    : Task.FromCanceled(new CancellationToken(canceled: true)));
            lifecycle.When(value => value.InvalidateForUnload()).Do(_ =>
                Assert.Equal(OperationReceiptState.Failed, Assert.Single(receipts).State));

            using var provider = new ServiceCollection()
                .AddSingleton<ISessionLifecycleCoordinator>(lifecycle)
                .AddSingleton(app.Imports)
                .AddSingleton<DisposalProbe>()
                .BuildServiceProvider();
            var probe = provider.GetRequiredService<DisposalProbe>();

            ProductionPoser::Poser.Poser.DisposeProviderAfterFrameworkExit(
                provider, framework, gpose, log, cleanup: static () => { });

            Assert.True(probe.Disposed);
            Assert.False(app.Imports.IsPending);
            Assert.Null(receipts[0].Recovery);
            lifecycle.Received(1).InvalidateForUnload();
            log.Received(1).Error(
                Arg.Is<string>(message => message.Contains(dispatch)));
        }
    }

    [Fact]
    public void Session_generation_is_stable_on_duplicates_rotates_on_reentry_and_clears_before_exit()
    {
        SessionLifecycleCoordinator? coordinator = null;
        var capture = new RecordingCapturePort(() =>
        {
            Assert.Null(coordinator!.ActiveSessionGeneration);
            return FinalCaptureResult.Captured(1);
        });
        coordinator = new SessionLifecycleCoordinator(capture);
        var first = coordinator.OnGposeEntered();
        var duplicate = coordinator.OnGposeEntered();
        Assert.True(first.HasValue);
        Assert.Equal(first, duplicate);

        coordinator.OnGposeExit();
        Assert.Null(coordinator.ActiveSessionGeneration);
        var reentry = coordinator.OnGposeEntered();
        Assert.True(reentry.HasValue);
        Assert.NotEqual(first, reentry);
        Assert.Equal(reentry, coordinator.ActiveSessionGeneration);

        var clientState = Substitute.For<IClientState>();
        var framework = Substitute.For<IFramework>();
        var eventBus = Substitute.For<IEventBus>();
        var log = Substitute.For<IPluginLog>();
        framework.IsInFrameworkUpdateThread.Returns(true);
        SessionGeneration? atFalseEvent = null;
        var gposeCoordinator = new SessionLifecycleCoordinator(
            new RecordingCapturePort(() => FinalCaptureResult.Captured(1)));
        eventBus.When(bus => bus.Publish(Arg.Any<GPoseStateChangedEvent>()))
            .Do(callInfo =>
            {
                if (!callInfo.Arg<GPoseStateChangedEvent>().IsGPosing)
                    atFalseEvent = gposeCoordinator.ActiveSessionGeneration;
            });
        using var gpose = new GPoseService(
            clientState, framework, eventBus, log, gposeCoordinator);
        clientState.IsGPosing.Returns(true);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        var active = gposeCoordinator.ActiveSessionGeneration;
        clientState.IsGPosing.Returns(false);
        framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
        Assert.NotNull(active);
        Assert.Null(atFalseEvent);
        Assert.Null(gposeCoordinator.ActiveSessionGeneration);

    }

    [Fact]
    public async Task Entry_during_active_exit_is_rejected_and_invalidation_closes_permanently()
    {
        var captureStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SessionLifecycleCoordinator(
            new RecordingCapturePort(() =>
            {
                captureStarted.SetResult(true);
                releaseCapture.Task.GetAwaiter().GetResult();
                return FinalCaptureResult.Captured(1);
            }));
        var first = coordinator.OnGposeEntered();
        var exit = Task.Run(coordinator.OnGposeExit);
        await captureStarted.Task;

        Assert.Null(coordinator.OnGposeEntered());
        coordinator.InvalidateForUnload();
        Assert.Null(coordinator.ActiveSessionGeneration);
        Assert.Null(coordinator.OnGposeEntered());

        releaseCapture.SetResult(true);
        await exit;
        Assert.True(first.HasValue);
        Assert.Null(coordinator.ActiveSessionGeneration);
        Assert.Null(coordinator.OnGposeEntered());
    }

    [Fact]
    public void Autosave_health_projection_preserves_terminal_evidence_bounds_overflow_and_status_contract()
    {
        var service = Substitute.For<IAutoSaveService>();
        service.CaptureForExit().Returns(AutoSaveCaptureResult.DispatchStarted(2));
        service.CompleteForExit().Returns(
            AutoSaveTerminalResult.RecoveryRequired("worker failed"));
        service.LastHealthRecord.Returns(JsonSerializer.Deserialize<AutoSaveHealthRecord>("""
            {
              "OperationId": "op-1", "Reason": "gpose-exit", "Status": 5,
              "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:01:00Z",
              "IntendedActors": 2, "WrittenActors": 1, "AffectedPaths": ["a.pose", "b.pose"],
              "FailurePhase": "ActorWrite", "Detail": "first actor failed",
              "RecoveryEvidencePaths": ["a.tmp"], "RecoveryEntries": [{
                "OperationId": "cancel-1", "Reason": "interval", "Status": 5,
                "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:00:01Z",
                "IntendedActors": 2, "WrittenActors": 0, "AffectedPaths": ["cancel.pose"],
                "FailurePhase": "HealthTransition", "Detail": "cancel failed",
                "RecoveryEvidencePaths": ["cancel.tmp"]
              }], "RecoveryOverflowCount": 3
            }
            """));
        var result = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(
            () => service).CaptureForExit();

        Assert.Equal(FinalPersistenceStatus.RecoveryRequired, result.Persistence);
        Assert.Equal("worker failed", result.PersistenceDetail);
        Assert.Equal("op-1", result.PersistenceEvidence!.OperationId);
        Assert.Equal(1, result.PersistenceEvidence.WrittenActors);
        Assert.Equal("ActorWrite", result.PersistenceEvidence.FailurePhase);
        Assert.Equal(new[] { "a.tmp" }, result.PersistenceEvidence.RecoveryEvidencePaths);
        Assert.Equal(3, result.PersistenceEvidence.RecoveryOverflowCount);
        var recovery = Assert.Single(result.PersistenceEvidence.RecoveryEntries);
        Assert.Equal("cancel-1", recovery.OperationId);
        Assert.Equal("HealthTransition", recovery.FailurePhase);
        Assert.Equal(new[] { "cancel.tmp" }, recovery.RecoveryEvidencePaths);
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

        var entries = Enumerable.Range(1, 6).Select(index =>
            new FinalPersistenceRecoveryEntry(
                $"op-{index}", "final", FinalPersistenceStatus.RecoveryRequired,
                DateTime.UtcNow, DateTime.UtcNow, 0, 0, null, null, null, null))
            .ToArray();
        var bounded = new FinalPersistenceEvidence(
            "op", "final", FinalPersistenceStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow, 2, 1, null, "HealthTransition", "detail", null,
            entries);
        Assert.Equal(4, bounded.RecoveryEntries.Count);
        Assert.Equal(2, bounded.RecoveryOverflowCount);
        var oversizedEntries = Enumerable.Range(1, 6).Select(index =>
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
        var boundedFields = new FinalPersistenceEvidence(
            "op", "final", FinalPersistenceStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow, 2, 1, null, "HealthTransition", "detail", null,
            oversizedEntries);
        var first = boundedFields.RecoveryEntries[0];
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
        var existingOverflow = new FinalPersistenceEvidence(
            "op", "final", FinalPersistenceStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow, 0, 0, null, null, null, null,
            entries, int.MaxValue);
        Assert.Equal(int.MaxValue, existingOverflow.RecoveryOverflowCount);

        foreach (var status in Enum.GetValues<AutoSaveHealthStatus>())
        {
            var mapped = Substitute.For<IAutoSaveService>();
            mapped.CaptureForExit().Returns(AutoSaveCaptureResult.DispatchStarted(1));
            mapped.CompleteForExit().Returns(AutoSaveTerminalResult.RecoveryRequired("terminal"));
            mapped.LastHealthRecord.Returns(JsonSerializer.Deserialize<AutoSaveHealthRecord>($$"""
                { "OperationId": "op-{{(int)status}}", "Reason": "test", "Status": {{(int)status}},
                  "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:01:00Z",
                  "IntendedActors": 1, "WrittenActors": 0, "RecoveryEntries": [{
                    "OperationId": "entry-{{(int)status}}", "Reason": "test", "Status": {{(int)status}},
                    "CreatedUtc": "2026-08-13T10:00:00Z", "UpdatedUtc": "2026-08-13T10:01:00Z",
                    "IntendedActors": 1, "WrittenActors": 0 }] }
                """));
            var mappedResult = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(
                () => mapped).CaptureForExit();
            var expected = status switch
            {
                AutoSaveHealthStatus.Pending or AutoSaveHealthStatus.Queued or AutoSaveHealthStatus.DispatchAccepted => FinalPersistenceStatus.Pending,
                AutoSaveHealthStatus.Written => FinalPersistenceStatus.Written,
                AutoSaveHealthStatus.Cleaned => FinalPersistenceStatus.Cleaned,
                AutoSaveHealthStatus.RecoveryRequired => FinalPersistenceStatus.RecoveryRequired,
                AutoSaveHealthStatus.Cancelled => FinalPersistenceStatus.Cancelled,
                _ => throw new Xunit.Sdk.XunitException($"Unhandled status {status}"),
            };
            Assert.Equal(expected, Assert.Single(mappedResult.PersistenceEvidence!.RecoveryEntries).Status);
        }

        var invalid = Substitute.For<IAutoSaveService>();
        invalid.CaptureForExit().Returns(AutoSaveCaptureResult.NotCaptured());
        invalid.CompleteForExit().Returns(
            new AutoSaveTerminalResult((AutoSaveTerminalStatus)999, "invalid"));
        var invalidPort = new ProductionPoser::Poser.Lifecycle.AutoSaveFinalCapturePort(
            () => invalid);
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidPort.CaptureForExit());
    }

    [Fact]
    public void Production_composition_defers_autosave_until_exit_and_orders_attempt_before_legacy_event()
    {
        using var importHarness = new PoseImportCaptureHarness();
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
            services.AddSingleton<IDataManager>(Substitute.For<IDataManager>());
            services.AddPoserCore();
            services.AddPoserFeatures();
            services.AddSingleton<PoseImportCapture>(importHarness.Imports);
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
            eventBus.Subscribe<GPoseStateChangedEvent>(evt =>
            {
                if (!evt.IsGPosing)
                {
                    legacyObserved = true;
                    Assert.Equal(FinalCaptureStatus.NotCaptured,
                        coordinator.LastExit!.Value.Capture.Status);
                    Assert.Equal("No actors had authored edits.",
                        coordinator.LastExit.Value.Capture.Detail);
                    Assert.True(coordinator.LastExit.Value.LegacyTeardownPending);
                }
            });

            _ = provider.GetRequiredService<IGPoseService>();
            Assert.Same(
                importHarness.Imports,
                provider.GetRequiredService<Func<IPoseImportLifecycleControl>>()());
            Assert.False(Directory.Exists(autoSaveRoot));
            clientState.IsGPosing.Returns(true);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);
            Assert.False(Directory.Exists(autoSaveRoot));
            clientState.IsGPosing.Returns(false);
            framework.Update += Raise.Event<IFramework.OnUpdateDelegate>(framework);

            Assert.True(Directory.Exists(autoSaveRoot));
            Assert.True(legacyObserved);
            Assert.Equal(FinalCaptureStatus.NotCaptured,
                coordinator.LastExit!.Value.Capture.Status);
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

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
