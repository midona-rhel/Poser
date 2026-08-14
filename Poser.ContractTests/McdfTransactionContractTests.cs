using Poser.Application.Integration;
using Poser.Application.Operations;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.ContractTests;

/// <summary>
/// Contract tests for the MCDF transaction owner, driven through the public
/// ActorIntegrationSession compatibility surface with deterministic fakes at
/// the real owner boundary (runtime port, file boundary, session source).
/// The invariants under test: exact actor/session/epoch identity on every
/// phase, one active transaction, invalidation before rollback, a bounded
/// exact-actor redraw-complete barrier before the collection-backed
/// extracted directory is released, retained retryable evidence on redraw
/// failure, and a bounded cancel/drain before disposal.
/// </summary>
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

        public static async Task WaitUntil(Func<bool> condition, string what)
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

    // ── Admission and identity ───────────────────────────────────────────

    [Fact]
    public async Task Import_commits_ownership_and_publishes_exact_applied_receipt()
    {
        using var app = new Harness();
        app.Files.PackageHasBody = true;

        var begun = app.Session.BeginImport(app.Actor, @"X:\in\look.mcdf");

        Assert.True(begun.Success, begun.Detail);
        var pending = app.Session.McdfReceipt;
        Assert.NotNull(pending);
        Assert.Equal(OperationReceiptState.Pending, pending!.State);
        Assert.Equal(app.Actor, pending.TargetActorId);
        Assert.Equal(app.Sessions.ActiveSessionGeneration, pending.SessionGeneration);
        Assert.Equal(OperationEpoch.First, pending.OperationEpoch);
        Assert.NotEqual(Guid.Empty, pending.OperationId);

        await app.Idle();

        var receipt = app.Session.McdfReceipt;
        Assert.NotNull(receipt);
        Assert.Equal(OperationReceiptState.Applied, receipt!.State);
        Assert.Equal(pending.OperationId, receipt.OperationId);
        Assert.Equal(pending.OperationEpoch, receipt.OperationEpoch);
        Assert.Equal(pending.SessionGeneration, receipt.SessionGeneration);
        Assert.Equal(app.Actor, receipt.TargetActorId);

        var progress = app.Session.Mcdf;
        Assert.NotNull(progress);
        Assert.Equal(McdfPhase.Completed, progress!.Phase);
        Assert.True(progress.Outcome!.Success);

        var overrides = app.Session.OverridesFor(app.Actor);
        Assert.NotNull(overrides.Mcdf);
        Assert.Single(app.Port.CreatedCollections);
        Assert.Equal(app.Port.CreatedCollections[0], overrides.Mcdf!.TemporaryCollection);
        Assert.True(overrides.Mcdf.GlamourerLocked);
        Assert.Single(app.Port.AppliedProfiles);
        Assert.Equal(app.Port.AppliedProfiles[0], overrides.Mcdf.TemporaryProfile);
        // The extracted payloads stay owned while the collection references them.
        Assert.Single(app.Files.CreatedDirectories);
        Assert.Equal(app.Files.CreatedDirectories[0], overrides.Mcdf.OperationDirectory);
        Assert.Empty(app.Files.DeletedDirectories);
    }

    [Fact]
    public void Import_refuses_without_an_active_session_generation()
    {
        using var app = new Harness();
        app.Sessions.ActiveSessionGeneration = null;

        var begun = app.Session.BeginImport(app.Actor, @"X:\in\look.mcdf");

        Assert.False(begun.Success);
        Assert.Null(app.Session.McdfReceipt);
        Assert.Empty(app.Files.CreatedDirectories);
        Assert.False(app.Session.McdfBusy);
    }

    [Fact]
    public async Task Only_one_transaction_runs_at_a_time()
    {
        using var app = new Harness();
        app.Files.ReadGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        Assert.True(app.Session.McdfBusy);

        Assert.False(app.Session.BeginImport(app.Actor, @"X:\in\b.mcdf").Success);
        Assert.False(app.Session.BeginExport(app.Actor, @"X:\out\c.mcdf", "d").Success);
        Assert.False(app.Session.ResetMcdf(app.Actor).Success);

        app.Files.ReadGate.TrySetResult(true);
        await app.Idle();
    }

    [Fact]
    public async Task Second_import_advances_the_owner_local_epoch()
    {
        using var app = new Harness();
        app.Files.PackageHasResources = false;
        app.Files.PackageHasGlamourer = true;

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.Idle();
        var first = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Applied, first.State);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\b.mcdf").Success);
        await app.Idle();
        var second = app.Session.McdfReceipt!;

        Assert.Equal(OperationReceiptState.Applied, second.State);
        Assert.Equal(first.OperationEpoch.Next(), second.OperationEpoch);
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    // ── Cancellation, invalidation, rollback ─────────────────────────────

    [Fact]
    public async Task Cancellation_rolls_back_registered_ownership_and_publishes_cancelled()
    {
        using var app = new Harness();
        var redraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(redraw);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.PortSaw("RedrawAndWait", 1);

        app.Session.CancelMcdf();
        redraw.TrySetResult(IntegrationPortResult.Fail("The operation was cancelled."));
        await app.Idle();

        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Cancelled, receipt.State);
        Assert.True(app.Session.Mcdf!.Outcome!.Cancelled);
        // Reverse-order rollback released everything that was registered.
        Assert.Single(app.Port.DeletedCollections);
        Assert.Equal(app.Port.CreatedCollections[0], app.Port.DeletedCollections[0]);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        // The guard refused before the body-profile phase could mutate.
        Assert.Empty(app.Port.AppliedProfiles);
    }

    [Fact]
    public async Task Reset_actor_invalidates_the_in_flight_import_before_rollback()
    {
        using var app = new Harness();
        var redraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(redraw);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.PortSaw("RedrawAndWait", 1);

        // Invalidation runs the rollback NOW, on the caller's thread, while
        // the background task is still parked on the redraw barrier.
        var reset = app.Session.ResetActor(app.Actor);
        Assert.True(reset.Success, reset.Detail);
        Assert.Single(app.Port.DeletedCollections);

        // A replacement operation still cannot start until the invalidated
        // task drains.
        Assert.False(app.Session.BeginImport(app.Actor, @"X:\in\b.mcdf").Success);

        redraw.TrySetResult(IntegrationPortResult.Fail("The operation was cancelled."));
        await app.Idle();

        Assert.Equal(OperationReceiptState.Cancelled, app.Session.McdfReceipt!.State);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
        Assert.Empty(app.Port.AppliedProfiles);

        // The drained slot admits the next transaction with a fresh epoch.
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\b.mcdf").Success);
        await app.Idle();
        Assert.Equal(OperationReceiptState.Applied, app.Session.McdfReceipt!.State);
    }

    [Fact]
    public async Task Session_generation_replacement_refuses_the_next_phase_and_rolls_back()
    {
        using var app = new Harness();
        var redraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(redraw);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.PortSaw("RedrawAndWait", 1);

        // The GPose session ends and a new one begins while the import is
        // parked: the stale session token must refuse the commit.
        app.Sessions.ActiveSessionGeneration = SessionGeneration.New();
        redraw.TrySetResult(IntegrationPortResult.Ok());
        await app.Idle();

        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Cancelled, receipt.State);
        Assert.NotEqual(app.Sessions.ActiveSessionGeneration, receipt.SessionGeneration);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
        Assert.Single(app.Port.DeletedCollections);
    }

    [Fact]
    public async Task Actor_replacement_at_commit_rolls_back_instead_of_committing()
    {
        using var app = new Harness();
        app.Files.PackageHasResources = false;
        var redraw = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // Resource-free package: no apply barrier; park the read instead.
        app.Files.ReadGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        app.Port.Resolvable.Remove(app.Actor);
        app.Files.ReadGate.TrySetResult(true);
        await app.Idle();

        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Failed, receipt.State);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
        _ = redraw;
    }

    [Fact]
    public async Task Missing_required_integration_fails_before_any_actor_mutation()
    {
        using var app = new Harness();
        app.Port.Penumbra = new IntegrationAvailability(false, "Penumbra is not installed.");

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.Idle();

        var receipt = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Failed, receipt.State);
        Assert.Contains("Penumbra", receipt.Detail);
        Assert.Empty(app.Port.CreatedCollections);
        Assert.Equal(0, app.Port.CallCount("HoldGlamourerState"));
        // Nothing referenced the extraction directory, so rollback released it.
        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
    }

    // ── Redraw-complete barrier before directory release ─────────────────

    [Fact]
    public async Task Rollback_releases_the_extracted_directory_only_after_the_redraw_barrier()
    {
        using var app = new Harness();
        app.Files.PackageHasGlamourer = false;
        app.Port.FailAddTemporaryMods = "Penumbra rejected the temporary mod.";
        var barrier = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(barrier);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        // The failure path deleted the temporary collection, then parked on
        // the redraw-complete barrier; the directory must still be owned.
        await app.PortSaw("RedrawAndWait", 1);
        Assert.Single(app.Port.DeletedCollections);
        Assert.Empty(app.Files.DeletedDirectories);

        barrier.TrySetResult(IntegrationPortResult.Ok());
        await app.Idle();

        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
        Assert.Equal(OperationReceiptState.Failed, app.Session.McdfReceipt!.State);
    }

    [Fact]
    public async Task Redraw_failure_retains_directory_ownership_and_reset_retries_it()
    {
        using var app = new Harness();
        app.Files.PackageHasGlamourer = false;
        app.Port.FailAddTemporaryMods = "Penumbra rejected the temporary mod.";
        var barrier = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(barrier);

        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.PortSaw("RedrawAndWait", 1);
        barrier.TrySetResult(IntegrationPortResult.Fail(
            "The actor did not finish redrawing within 10 seconds."));
        await app.Idle();

        // The redraw never completed while the actor still resolves: the
        // extracted directory stays owned as retryable evidence.
        Assert.Empty(app.Files.DeletedDirectories);
        var retained = app.Session.OverridesFor(app.Actor).Mcdf;
        Assert.NotNull(retained);
        Assert.Equal(app.Files.CreatedDirectories[0], retained!.OperationDirectory);
        Assert.True(retained.RedrawPending);
        Assert.Contains("Reset MCDF", app.Session.McdfReceipt!.Detail);

        // Reset MCDF retries: the barrier completes this time and the
        // directory is released.
        var reset = app.Session.ResetMcdf(app.Actor);
        Assert.True(reset.Success, reset.Detail);
        await app.Idle();
        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
    }

    [Fact]
    public async Task Teardown_of_committed_ownership_waits_for_the_redraw_barrier()
    {
        using var app = new Harness();
        app.Files.PackageHasBody = true;
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.Idle();
        Assert.NotNull(app.Session.OverridesFor(app.Actor).Mcdf);

        var barrier = new TaskCompletionSource<IntegrationPortResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        app.Port.RedrawWaits.Enqueue(barrier);

        var reset = app.Session.ResetMcdf(app.Actor);
        Assert.True(reset.Success, reset.Detail);

        // The synchronous stage released the native pieces...
        Assert.Equal(1, app.Port.CallCount("UnlockGlamourerState"));
        Assert.Single(app.Port.DeletedProfiles);
        Assert.Single(app.Port.DeletedCollections);
        // ...restored the captured incoming appearance...
        Assert.Contains("incoming-state", app.Port.RestoredGlamourerStates);
        // ...but the extracted directory stays owned until the exact actor's
        // redraw completes.
        Assert.Empty(app.Files.DeletedDirectories);
        Assert.True(app.Session.McdfBusy);

        barrier.TrySetResult(IntegrationPortResult.Ok());
        await app.Idle();

        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
    }

    [Fact]
    public async Task Teardown_of_an_unresolvable_actor_releases_the_directory_immediately()
    {
        using var app = new Harness();
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.Idle();

        app.Port.Resolvable.Remove(app.Actor);
        var reset = app.Session.ResetMcdf(app.Actor);

        Assert.True(reset.Success, reset.Detail);
        // Nothing native can reference the payloads: no barrier, no task.
        Assert.False(app.Session.McdfBusy);
        Assert.Equal(app.Files.CreatedDirectories, app.Files.DeletedDirectories);
        Assert.Equal(IntegrationOverrides.None, app.Session.OverridesFor(app.Actor));
    }

    [Fact]
    public async Task Reimport_tears_down_the_prior_transaction_and_releases_its_directory_after_the_barrier()
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

        // The prior transaction was torn down: its collection deleted, its
        // directory released after a redraw-complete barrier, and the new
        // ownership stands alone.
        Assert.Contains(firstCollection, app.Port.DeletedCollections);
        Assert.Contains(firstDirectory, app.Files.DeletedDirectories);
        var overrides = app.Session.OverridesFor(app.Actor);
        Assert.Equal(app.Files.CreatedDirectories[1], overrides.Mcdf!.OperationDirectory);
        Assert.DoesNotContain(app.Files.CreatedDirectories[1], app.Files.DeletedDirectories);
        // Old teardown barrier + new apply barrier.
        Assert.True(app.Port.CallCount("RedrawAndWait") >= 2);
    }

    // ── Export ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_is_read_only_capture_plus_file_work_and_publishes_a_receipt()
    {
        using var app = new Harness();

        var begun = app.Session.BeginExport(app.Actor, @"X:\out\me.mcdf", "desc");

        Assert.True(begun.Success, begun.Detail);
        var pending = app.Session.McdfReceipt!;
        Assert.Equal(OperationReceiptState.Pending, pending.State);
        Assert.Equal(app.Actor, pending.TargetActorId);

        await app.Idle();

        Assert.Equal(OperationReceiptState.Applied, app.Session.McdfReceipt!.State);
        Assert.Equal(pending.OperationId, app.Session.McdfReceipt!.OperationId);
        Assert.Equal(McdfPhase.Completed, app.Session.Mcdf!.Phase);
        // Read-only: no mutating call reached the actor.
        foreach (var forbidden in new[]
        {
            "CreateTemporaryCollection", "AssignTemporaryCollection",
            "AddTemporaryMods", "DeleteTemporaryCollection",
            "HoldGlamourerState", "RestoreGlamourerState", "UnlockGlamourerState",
            "ApplyTemporaryBodyProfile", "DeleteTemporaryBodyProfileById",
            "SetIndividualCollection", "RestoreCollection", "RequestRedraw",
        })
            Assert.Equal(0, app.Port.CallCount(forbidden));
    }

    [Fact]
    public async Task Export_refuses_an_mcdf_wearing_actor()
    {
        using var app = new Harness();
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        await app.Idle();

        var begun = app.Session.BeginExport(app.Actor, @"X:\out\me.mcdf", "desc");

        Assert.False(begun.Success);
        Assert.Contains("Reset MCDF", begun.Detail);
    }

    // ── Drain before disposal ────────────────────────────────────────────

    [Fact]
    public async Task Dispose_cancels_and_drains_the_active_task_then_refuses_new_operations()
    {
        var app = new Harness();
        app.Files.ReadGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(app.Session.BeginImport(app.Actor, @"X:\in\a.mcdf").Success);
        Assert.True(app.Session.McdfBusy);

        // Dispose cancels the operation; the boundary observes the token and
        // the drain joins the task inside the bound.
        app.Session.Dispose();
        await app.Idle();

        Assert.Equal(OperationReceiptState.Cancelled, app.Session.McdfReceipt!.State);
        Assert.False(app.Session.BeginImport(app.Actor, @"X:\in\b.mcdf").Success);
        Assert.False(app.Session.BeginExport(app.Actor, @"X:\out\c.mcdf", "d").Success);
    }
}
