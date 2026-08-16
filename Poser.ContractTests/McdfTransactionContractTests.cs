using System.Linq;
using Poser.Application.Integration;
using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.ContractTests;

public sealed class McdfTransactionContractTests
{
    private sealed class Harness : IDisposable
    {
        public FakeIntegrationRuntimePort Port { get; } = new();
        public FakeMcdfFileBoundary Files { get; } = new();
        public FakeSessionGenerationSource Sessions { get; } = new();
        public ActorIntegrationSession Session { get; }
        public ActorId Actor { get; } = ActorId.New();

        public Harness()
        {
            Sessions.ActiveSessionGeneration = SessionGeneration.New();
            Port.Resolvable.Add(Actor);
            Session = new ActorIntegrationSession(Port, Files, Sessions);
        }

        public void Dispose() => Session.Dispose();

        public Task Idle() => WaitUntil(() => !Session.McdfBusy, "the MCDF task to finish");

        public Task PortSaw(string call, int count) =>
            WaitUntil(() => Port.CallCount(call) >= count, $"{count}x {call}");

        private static async Task WaitUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Timed out waiting for {what}.");
                await Task.Delay(10);
            }
        }
    }

    [Fact]
    public async Task Import_commits_exact_identity_ownership_and_ordered_side_effects()
    {
        using var app = new Harness();
        app.Files.PackageHasBody = true;
        var begun = app.Session.BeginImport(app.Actor, @"X:\in\look.mcdf");

        Assert.True(begun.Success, begun.Detail);
        var pending = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Pending, pending.State);
        Assert.Equal(app.Actor, pending.TargetActorId);
        Assert.Equal(app.Sessions.ActiveSessionGeneration, pending.SessionGeneration);
        Assert.Equal(OperationEpoch.First, pending.OperationEpoch);
        Assert.NotEqual(Guid.Empty, pending.OperationId);

        await app.Idle();

        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Applied, receipt.State);
        Assert.Equal(pending.OperationId, receipt.OperationId);
        Assert.Equal(pending.OperationEpoch, receipt.OperationEpoch);
        Assert.Equal(pending.SessionGeneration, receipt.SessionGeneration);
        Assert.Equal(app.Actor, receipt.TargetActorId);
        Assert.Equal(McdfPhase.Completed, app.Session.Mcdf!.Phase);
        Assert.True(app.Session.Mcdf.Outcome!.Success);

        var overrides = app.Session.OverridesFor(app.Actor).Mcdf!;
        Assert.Equal(app.Port.CreatedCollections[0], overrides.TemporaryCollection);
        Assert.Equal(app.Port.AppliedProfiles[0], overrides.TemporaryProfile);
        Assert.Equal(app.Files.CreatedDirectories[0], overrides.OperationDirectory);
        Assert.True(overrides.GlamourerLocked);
        Assert.Empty(app.Files.DeletedDirectories);

        var calls = app.Port.Calls.ToList();
        Assert.True(calls.IndexOf("CreateTemporaryCollection")
            < calls.IndexOf("HoldGlamourerState"));
        Assert.True(calls.IndexOf("HoldGlamourerState")
            < calls.IndexOf("RedrawAndWait"));
        Assert.True(calls.IndexOf("RedrawAndWait")
            < calls.IndexOf("ApplyTemporaryBodyProfile"));
    }

    [Fact]
    public async Task Admission_rejects_missing_generation_and_competing_work_while_generation_replacement_invalidates()
    {
        using (var missing = new Harness())
        {
            missing.Sessions.ActiveSessionGeneration = null;
            var refused = missing.Session.BeginImport(
                missing.Actor, @"X:\in\missing-generation.mcdf");
            Assert.False(refused.Success);
            Assert.Null(missing.Session.McdfReceipt);
            Assert.Empty(missing.Files.CreatedDirectories);
            Assert.False(missing.Session.McdfBusy);
        }

        using (var busy = new Harness())
        {
            busy.Files.ReadGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(busy.Session.BeginImport(busy.Actor, @"X:\in\a.mcdf").Success);
            Assert.True(busy.Session.McdfBusy);
            Assert.False(busy.Session.BeginImport(busy.Actor, @"X:\in\b.mcdf").Success);
            Assert.False(busy.Session.BeginExport(busy.Actor, @"X:\out\c.mcdf", "d").Success);
            Assert.False(busy.Session.ResetMcdf(busy.Actor).Success);
            busy.Files.ReadGate.TrySetResult(true);
            await busy.Idle();
        }

        using (var reset = new Harness())
        {
            var resetRedraw = new TaskCompletionSource<IntegrationPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            reset.Port.RedrawWaits.Enqueue(resetRedraw);
            Assert.True(reset.Session.BeginImport(
                reset.Actor, @"X:\in\reset-actor.mcdf").Success);
            await reset.PortSaw("RedrawAndWait", 1);

            Assert.True(reset.Session.ResetActor(reset.Actor).Success);
            Assert.Single(reset.Port.DeletedCollections);
            Assert.False(reset.Session.BeginImport(
                reset.Actor, @"X:\in\blocked-until-drained.mcdf").Success);
            resetRedraw.TrySetResult(IntegrationPortResult.Fail(
                "The operation was cancelled."));
            await reset.Idle();

            Assert.Equal(OperationReceiptState.Cancelled,
                reset.Session.McdfReceipt!.State);
            Assert.Equal(IntegrationOverrides.None,
                reset.Session.OverridesFor(reset.Actor));
            Assert.Empty(reset.Port.AppliedProfiles);
            Assert.True(reset.Session.BeginImport(
                reset.Actor, @"X:\in\after-drain.mcdf").Success);
            await reset.Idle();
            Assert.Equal(OperationReceiptState.Applied,
                reset.Session.McdfReceipt!.State);
        }

        using (var actorReplaced = new Harness())
        {
            actorReplaced.Files.PackageHasResources = false;
            actorReplaced.Files.ReadGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(actorReplaced.Session.BeginImport(
                actorReplaced.Actor, @"X:\in\actor-replaced.mcdf").Success);
            actorReplaced.Port.Resolvable.Remove(actorReplaced.Actor);
            actorReplaced.Files.ReadGate.TrySetResult(true);
            await actorReplaced.Idle();

            Assert.Equal(OperationReceiptState.Failed,
                actorReplaced.Session.McdfReceipt!.State);
            Assert.Equal(IntegrationOverrides.None,
                actorReplaced.Session.OverridesFor(actorReplaced.Actor));
            Assert.Equal(actorReplaced.Files.CreatedDirectories,
                actorReplaced.Files.DeletedDirectories);
        }

        using var replaced = new Harness();
        var generationRedraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        replaced.Port.RedrawWaits.Enqueue(generationRedraw);
        Assert.True(replaced.Session.BeginImport(replaced.Actor, @"X:\in\stale.mcdf").Success);
        await replaced.PortSaw("RedrawAndWait", 1);
        var oldSession = replaced.Session.McdfReceipt!.SessionGeneration;
        replaced.Sessions.ActiveSessionGeneration = SessionGeneration.New();
        generationRedraw.TrySetResult(IntegrationPortResult.Ok());
        await replaced.Idle();

        var cancelled = replaced.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Cancelled, cancelled.State);
        Assert.Equal(oldSession, cancelled.SessionGeneration);
        Assert.NotEqual(replaced.Sessions.ActiveSessionGeneration, cancelled.SessionGeneration);
        Assert.Equal(IntegrationOverrides.None, replaced.Session.OverridesFor(replaced.Actor));
        Assert.Single(replaced.Port.DeletedCollections);
    }

    [Fact]
    public async Task Cancel_failure_and_missing_integration_roll_back_without_leaking_ownership()
    {
        using (var canceled = new Harness())
        {
            var redraw = new TaskCompletionSource<IntegrationPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            canceled.Port.RedrawWaits.Enqueue(redraw);
            Assert.True(canceled.Session.BeginImport(canceled.Actor, @"X:\in\cancel.mcdf").Success);
            await canceled.PortSaw("RedrawAndWait", 1);
            canceled.Session.CancelMcdf();
            redraw.TrySetResult(IntegrationPortResult.Fail("The operation was cancelled."));
            await canceled.Idle();

            Assert.Equal(OperationReceiptState.Cancelled, canceled.Session.McdfReceipt!.State);
            Assert.True(canceled.Session.Mcdf!.Outcome!.Cancelled);
            Assert.Equal(canceled.Files.CreatedDirectories, canceled.Files.DeletedDirectories);
            Assert.Equal(IntegrationOverrides.None, canceled.Session.OverridesFor(canceled.Actor));
            Assert.Empty(canceled.Port.AppliedProfiles);
        }

        using (var failed = new Harness())
        {
            failed.Files.ReadFailure = "package read failed";
            Assert.True(failed.Session.BeginImport(failed.Actor, @"X:\in\failed.mcdf").Success);
            await failed.Idle();
            Assert.Equal(OperationReceiptState.Failed, failed.Session.McdfReceipt!.State);
            Assert.Contains("package read failed", failed.Session.McdfReceipt.Detail);
            Assert.Equal(failed.Files.CreatedDirectories, failed.Files.DeletedDirectories);
            Assert.Equal(IntegrationOverrides.None, failed.Session.OverridesFor(failed.Actor));
        }

        using var unavailable = new Harness();
        unavailable.Port.Penumbra = new IntegrationAvailability(
            false, "Penumbra is not installed.");
        Assert.True(unavailable.Session.BeginImport(unavailable.Actor, @"X:\in\missing.mcdf").Success);
        await unavailable.Idle();
        Assert.Equal(OperationReceiptState.Failed, unavailable.Session.McdfReceipt!.State);
        Assert.Contains("Penumbra", unavailable.Session.McdfReceipt.Detail);
        Assert.Empty(unavailable.Port.CreatedCollections);
        Assert.Equal(0, unavailable.Port.CallCount("HoldGlamourerState"));
        Assert.Equal(unavailable.Files.CreatedDirectories, unavailable.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, unavailable.Session.OverridesFor(unavailable.Actor));
    }

    [Fact]
    public async Task Redraw_barrier_owns_extracted_directory_and_retry_releases_it()
    {
        using (var failedBeforeRedraw = new Harness())
        {
            failedBeforeRedraw.Files.PackageHasGlamourer = false;
            failedBeforeRedraw.Port.FailAddTemporaryMods = "Penumbra rejected the temporary mod.";
            var barrier = new TaskCompletionSource<IntegrationPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            failedBeforeRedraw.Port.RedrawWaits.Enqueue(barrier);
            Assert.True(failedBeforeRedraw.Session.BeginImport(
                failedBeforeRedraw.Actor, @"X:\in\barrier.mcdf").Success);
            await failedBeforeRedraw.PortSaw("RedrawAndWait", 1);
            Assert.Single(failedBeforeRedraw.Port.DeletedCollections);
            Assert.Empty(failedBeforeRedraw.Files.DeletedDirectories);
            barrier.TrySetResult(IntegrationPortResult.Ok());
            await failedBeforeRedraw.Idle();
            Assert.Equal(
                failedBeforeRedraw.Files.CreatedDirectories,
                failedBeforeRedraw.Files.DeletedDirectories);
        }

        using var retry = new Harness();
        retry.Files.PackageHasGlamourer = false;
        retry.Port.FailAddTemporaryMods = "Penumbra rejected the temporary mod.";
        var redraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        retry.Port.RedrawWaits.Enqueue(redraw);
        Assert.True(retry.Session.BeginImport(retry.Actor, @"X:\in\retry.mcdf").Success);
        await retry.PortSaw("RedrawAndWait", 1);
        redraw.TrySetResult(IntegrationPortResult.Fail(
            "The actor did not finish redrawing within 10 seconds."));
        await retry.Idle();

        var retained = retry.Session.OverridesFor(retry.Actor).Mcdf!;
        Assert.Empty(retry.Files.DeletedDirectories);
        Assert.True(retained.RedrawPending);
        Assert.Contains("Reset MCDF", retry.Session.McdfReceipt!.Detail);
        Assert.True(retry.Session.ResetMcdf(retry.Actor).Success);
        await retry.Idle();
        Assert.Equal(retry.Files.CreatedDirectories, retry.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, retry.Session.OverridesFor(retry.Actor));
    }

    [Fact]
    public async Task Committed_teardown_uses_exact_actor_or_name_and_waits_for_redraw_before_release()
    {
        using (var exact = new Harness())
        {
            exact.Files.PackageHasBody = true;
            Assert.True(exact.Session.BeginImport(exact.Actor, @"X:\in\exact.mcdf").Success);
            await exact.Idle();
            var redraw = new TaskCompletionSource<IntegrationPortResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            exact.Port.RedrawWaits.Enqueue(redraw);
            Assert.True(exact.Session.ResetMcdf(exact.Actor).Success);
            await exact.PortSaw("RedrawAndWait", 2);
            Assert.Single(exact.Port.DeletedProfiles);
            Assert.Single(exact.Port.DeletedCollections);
            Assert.Empty(exact.Files.DeletedDirectories);
            var teardownCalls = exact.Port.Calls.ToList();
            Assert.True(teardownCalls.IndexOf("UnlockGlamourerState")
                < teardownCalls.IndexOf("RestoreGlamourerState"));
            Assert.True(teardownCalls.IndexOf("RestoreGlamourerState")
                < teardownCalls.IndexOf("DeleteTemporaryBodyProfileById"));
            Assert.True(teardownCalls.IndexOf("DeleteTemporaryBodyProfileById")
                < teardownCalls.IndexOf("DeleteTemporaryCollection"));
            Assert.True(teardownCalls.IndexOf("DeleteTemporaryCollection")
                < teardownCalls.LastIndexOf("RedrawAndWait"));
            redraw.TrySetResult(IntegrationPortResult.Ok());
            await exact.Idle();
            Assert.Equal(exact.Files.CreatedDirectories, exact.Files.DeletedDirectories);
            Assert.Equal(IntegrationOverrides.None, exact.Session.OverridesFor(exact.Actor));
        }

        using var byName = new Harness();
        byName.Port.ActorName = "Aymeric Borel";
        Assert.True(byName.Session.BeginImport(byName.Actor, @"X:\in\gone.mcdf").Success);
        await byName.Idle();
        byName.Port.Resolvable.Remove(byName.Actor);
        Assert.True(byName.Session.ResetAll().Success);

        Assert.Equal(new[] { "Aymeric Borel" }, byName.Port.UnlockedGlamourerNames);
        Assert.Equal(
            new[] { ("Aymeric Borel", "incoming-state") },
            byName.Port.RestoredGlamourerStatesByName);
        var byNameCalls = byName.Port.Calls.ToList();
        Assert.True(byNameCalls.IndexOf("UnlockGlamourerStateByName")
            < byNameCalls.IndexOf("RestoreGlamourerStateByName"));
        Assert.Equal(0, byName.Port.CallCount("UnlockGlamourerState"));
        Assert.Equal(0, byName.Port.CallCount("RestoreGlamourerState"));
        Assert.Empty(byName.Port.RestoredGlamourerStates);
        Assert.Equal(IntegrationOverrides.None, byName.Session.OverridesFor(byName.Actor));
        Assert.False(byName.Session.McdfBusy);
        Assert.Equal(byName.Files.CreatedDirectories, byName.Files.DeletedDirectories);
    }

    [Fact]
    public async Task Failed_name_teardown_retries_and_unnamed_missing_actor_retains_the_lock()
    {
        using (var unlock = new Harness())
        {
            Assert.True(unlock.Session.BeginImport(unlock.Actor, @"X:\in\unlock.mcdf").Success);
            await unlock.Idle();
            unlock.Port.Resolvable.Remove(unlock.Actor);
            unlock.Port.FailUnlockGlamourerByName = "Glamourer is not responding.";
            Assert.False(unlock.Session.ResetAll().Success);
            var retained = unlock.Session.OverridesFor(unlock.Actor).Mcdf!;
            Assert.True(retained.GlamourerLocked);
            Assert.Equal("Imported Character", retained.ActorName);
            Assert.Empty(unlock.Port.RestoredGlamourerStatesByName);
            unlock.Port.FailUnlockGlamourerByName = null;
            Assert.True(unlock.Session.ResetMcdf(unlock.Actor).Success);
            Assert.Equal(
                new[] { ("Imported Character", "incoming-state") },
                unlock.Port.RestoredGlamourerStatesByName);
        }

        using (var restore = new Harness())
        {
            Assert.True(restore.Session.BeginImport(restore.Actor, @"X:\in\restore.mcdf").Success);
            await restore.Idle();
            restore.Port.Resolvable.Remove(restore.Actor);
            restore.Port.FailRestoreGlamourerByName = "Glamourer refused the state.";
            Assert.False(restore.Session.ResetAll().Success);
            var owned = restore.Session.OverridesFor(restore.Actor).Mcdf!;
            Assert.False(owned.GlamourerLocked);
            Assert.NotNull(restore.Session.OverridesFor(restore.Actor).Baseline.GlamourerState);
            restore.Port.FailRestoreGlamourerByName = null;
            Assert.True(restore.Session.ResetMcdf(restore.Actor).Success);
            Assert.Equal(IntegrationOverrides.None, restore.Session.OverridesFor(restore.Actor));
            Assert.Equal(2, restore.Port.CallCount("RestoreGlamourerStateByName"));
            Assert.Equal(
                new[]
                {
                    ("Imported Character", "incoming-state"),
                    ("Imported Character", "incoming-state"),
                },
                restore.Port.RestoredGlamourerStatesByName);
        }

        using var unnamed = new Harness();
        unnamed.Port.ActorName = string.Empty;
        Assert.True(unnamed.Session.BeginImport(unnamed.Actor, @"X:\in\unnamed.mcdf").Success);
        await unnamed.Idle();
        unnamed.Port.Resolvable.Remove(unnamed.Actor);
        Assert.False(unnamed.Session.ResetAll().Success);
        Assert.True(unnamed.Session.OverridesFor(unnamed.Actor).Mcdf!.GlamourerLocked);
        Assert.Empty(unnamed.Port.UnlockedGlamourerNames);
        Assert.Empty(unnamed.Port.RestoredGlamourerStatesByName);
    }

    [Fact]
    public async Task Reimport_tears_down_old_ownership_before_committing_the_next_generation()
    {
        using var app = new Harness();
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\first.mcdf").Success);
        await app.Idle();
        var firstReceipt = app.Session.McdfReceipt!;
        var firstCollection = app.Port.CreatedCollections[0];
        var firstDirectory = app.Files.CreatedDirectories[0];

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\second.mcdf").Success);
        await app.Idle();
        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Applied, receipt.State);
        Assert.Equal(firstReceipt.OperationEpoch.Next(), receipt.OperationEpoch);
        Assert.Contains(firstCollection, app.Port.DeletedCollections);
        Assert.Contains(firstDirectory, app.Files.DeletedDirectories);
        Assert.Equal(
            app.Files.CreatedDirectories[1],
            app.Session.OverridesFor(app.Actor).Mcdf!.OperationDirectory);
        Assert.DoesNotContain(
            app.Files.CreatedDirectories[1], app.Files.DeletedDirectories);

        var calls = app.Port.Calls.ToList();
        Assert.True(calls.LastIndexOf("DeleteTemporaryCollection")
            < calls.LastIndexOf("CreateTemporaryCollection"));
        Assert.True(app.Port.CallCount("RedrawAndWait") >= 2);
    }

    [Fact]
    public async Task Export_is_read_only_and_refuses_an_actor_still_wearing_mcdf()
    {
        using (var export = new Harness())
        {
            var begun = export.Session.BeginExport(
                export.Actor, @"X:\out\me.mcdf", "desc");
            Assert.True(begun.Success, begun.Detail);
            var pending = export.Session.McdfReceipt!;
            Assert.Equal(export.Actor, pending.TargetActorId);
            await export.Idle();
            Assert.Equal(OperationReceiptState.Applied, export.Session.McdfReceipt!.State);
            Assert.Equal(pending.OperationId, export.Session.McdfReceipt.OperationId);
            Assert.Equal(McdfPhase.Completed, export.Session.Mcdf!.Phase);
            foreach (var forbidden in new[]
            {
                "CreateTemporaryCollection", "AssignTemporaryCollection",
                "AddTemporaryMods", "DeleteTemporaryCollection",
                "HoldGlamourerState", "RestoreGlamourerState", "UnlockGlamourerState",
                "ApplyTemporaryBodyProfile", "DeleteTemporaryBodyProfileById",
                "SetIndividualCollection", "RestoreCollection", "RequestRedraw",
            })
                Assert.Equal(0, export.Port.CallCount(forbidden));
        }

        using var wearing = new Harness();
        Assert.True(wearing.Session.BeginImport(wearing.Actor, @"X:\in\wearing.mcdf").Success);
        await wearing.Idle();
        var refused = wearing.Session.BeginExport(
            wearing.Actor, @"X:\out\refused.mcdf", "desc");
        Assert.False(refused.Success);
        Assert.Contains("Reset MCDF", refused.Detail);
    }

    [Fact]
    public async Task Dispose_drains_active_work_tears_down_committed_ownership_and_refuses_new_operations()
    {
        var active = new Harness();
        active.Files.ReadGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(active.Session.BeginImport(active.Actor, @"X:\in\active.mcdf").Success);
        Assert.True(active.Session.McdfBusy);
        active.Session.Dispose();
        await active.Idle();
        Assert.Equal(OperationReceiptState.Cancelled, active.Session.McdfReceipt!.State);
        Assert.False(active.Session.BeginImport(active.Actor, @"X:\in\new.mcdf").Success);
        Assert.False(active.Session.BeginExport(active.Actor, @"X:\out\new.mcdf", "d").Success);

        var committed = new Harness();
        committed.Files.PackageHasBody = true;
        Assert.True(committed.Session.BeginImport(committed.Actor, @"X:\in\committed.mcdf").Success);
        await committed.Idle();
        committed.Port.Resolvable.Remove(committed.Actor);
        committed.Session.Dispose();

        Assert.Equal(IntegrationOverrides.None, committed.Session.OverridesFor(committed.Actor));
        Assert.Equal(committed.Port.CreatedCollections, committed.Port.DeletedCollections);
        Assert.Equal(committed.Port.AppliedProfiles, committed.Port.DeletedProfiles);
        Assert.Equal(new[] { "Imported Character" }, committed.Port.UnlockedGlamourerNames);
        Assert.Equal(
            new[] { ("Imported Character", "incoming-state") },
            committed.Port.RestoredGlamourerStatesByName);
        Assert.Equal(committed.Files.CreatedDirectories, committed.Files.DeletedDirectories);
    }
}
