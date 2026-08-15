using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using NSubstitute;
using Poser.Files;
using Poser.Services;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

/// <summary>
/// What a snapshot contains and where it lands: candidate selection, folder
/// naming, file naming, and per-actor failure isolation.
///
/// <para>The service captures on the caller's thread and writes on a worker, so
/// these tests split accordingly: <c>SaveNow</c>'s return and the
/// <c>CreatePoseFile</c> calls are asserted immediately, and everything about
/// folders and files only after <c>WaitForWrite</c>.</para>
/// </summary>
public class AutoSaveServiceSnapshotTests
{
    [Fact]
    public void CaptureForExit_reports_capture_when_worker_dispatch_is_not_accepted()
    {
        using var h = new AutoSaveHarness
        {
            Dispatch = _ => false
        };
        h.AddActor("Alpha");

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.Captured, result.Status);
        Assert.Equal(1, result.CapturedActors);
        Assert.False(result.DispatchAccepted);
        Assert.Null(h.Service.LastSaveUtc);
        Assert.Equal(
            AutoSaveTerminalStatus.RecoveryRequired,
            h.Service.CompleteForExit().Status);
    }

    [Fact]
    public void CaptureForExit_reports_failure_without_throwing_when_capture_fails()
    {
        using var h = new AutoSaveHarness();
        var broken = h.AddActor("Broken");
        h.FailCaptureFor(broken, new IOException("skeleton copy failed"));

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.Failure, result.Status);
        Assert.Contains("skeleton copy failed", result.Detail);
        Assert.Equal(0, result.CapturedActors);
        Assert.Equal(1, h.CaptureCallCount);
        h.WaitForWrite();
    }

    [Fact]
    public void Partial_capture_failure_is_not_reported_as_completed()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Good");
        var broken = h.AddActor("Broken");
        h.FailCaptureFor(broken, new IOException("skeleton copy failed"));

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.Failure, result.Status);
        Assert.Equal(1, result.CapturedActors);
        Assert.True(result.DispatchAccepted);
        Assert.False(result.CaptureCompleted);
        Assert.Equal(
            AutoSaveTerminalStatus.RecoveryRequired,
            h.Service.CompleteForExit().Status);
        h.WaitForWrite();
    }

    [Fact]
    public void SaveNow_captures_only_actors_with_authored_edits()
    {
        using var h = new AutoSaveHarness();
        var alpha = h.AddActor("Alpha");
        var beta = h.AddActor("Beta");
        var gamma = h.AddActor("Gamma", authored: false);

        var captured = h.Service.SaveNow("test");

        Assert.Equal(2, captured);
        Assert.Equal(2, h.CaptureCallCount);
        h.PoseFiles.Received(1).CreatePoseFile(alpha.Skeletons);
        h.PoseFiles.Received(1).CreatePoseFile(beta.Skeletons);
        h.PoseFiles.DidNotReceive().CreatePoseFile(gamma.Skeletons);

        // LastSaveUtc is stamped only after detached data is accepted for
        // dispatch; it does not acknowledge the worker or disk write.
        Assert.Equal(h.NowUtc, h.Service.LastSaveUtc);

        h.WaitForWrite();
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Alpha.pose", $"{h.PrefixNow()} Beta.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_writes_a_readable_pose_file()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha");

        h.Service.SaveNow("test");
        h.WaitForWrite();

        // The worker serializes whatever CreatePoseFile handed it, so the file
        // has to round-trip — an empty or half-written file would still exist.
        var path = Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Alpha.pose");
        var loaded = PoseFile.Load(path);
        Assert.NotNull(loaded);
        Assert.Contains("j_kosi", loaded!.Bones.Keys);
    }

    [Fact]
    public void SaveNow_names_the_day_folder_and_time_prefix_from_the_injected_clock()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = new DateTime(2026, 12, 31, 23, 45, 6, DateTimeKind.Utc);
        h.AddActor("Alpha");

        h.Service.SaveNow("test");
        h.WaitForWrite();

        // One folder per LOCAL day, files prefixed with the local 24-hour
        // time (the deliberate deviation from both references'
        // folder-per-save layout). The expectation converts through the same
        // ToLocalTime as the service, so it holds in any machine time zone —
        // including one where this UTC instant is already January 1st.
        Assert.Equal(new[] { h.DayNow() }, h.SnapshotFolders());
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Alpha.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_with_no_authored_edits_captures_and_writes_nothing()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha", authored: false);
        h.AddActor("Beta", authored: false);

        var captured = h.Service.SaveNow("test");

        // No candidates means no worker is dispatched at all, so the disk state
        // here is already final.
        Assert.Equal(0, captured);
        Assert.Equal(0, h.CaptureCallCount);
        h.WaitForWrite();
        Assert.Empty(Directory.GetDirectories(h.Root));
        Assert.Null(h.Service.LastSaveUtc);
    }

    [Fact]
    public void SaveNow_with_no_actors_at_all_captures_and_writes_nothing()
    {
        using var h = new AutoSaveHarness();

        var captured = h.Service.SaveNow("test");

        Assert.Equal(0, captured);
        Assert.Equal(0, h.CaptureCallCount);
        h.WaitForWrite();
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public void SaveNow_deduplicates_identical_actor_names_within_a_snapshot()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Zidane");
        h.AddActor("Zidane");

        var captured = h.Service.SaveNow("test");

        Assert.Equal(2, captured);

        h.WaitForWrite();
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Zidane (2).pose", $"{h.PrefixNow()} Zidane.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_sanitizes_invalid_filename_characters()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("A<b>:c");

        h.Service.SaveNow("test");
        h.WaitForWrite();

        Assert.Equal(
            new[] { $"{h.PrefixNow()} A_b__c.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveNow_falls_back_to_Actor_for_a_blank_name(string blank)
    {
        using var h = new AutoSaveHarness();
        h.AddActor(blank);

        h.Service.SaveNow("test");
        h.WaitForWrite();

        Assert.Equal(
            new[] { $"{h.PrefixNow()} Actor.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_counts_captures_not_writes_and_continues_past_a_failed_write()
    {
        using var h = new AutoSaveHarness();
        var bad = h.AddActor("Bad");
        h.AddActor("Good");
        h.FailWriteFor(bad);

        var captured = h.Service.SaveNow("test");

        // Both actors were captured, so both count — the return says nothing
        // about what survives the worker.
        Assert.Equal(2, captured);
        Assert.Equal(2, h.CaptureCallCount);

        h.WaitForWrite();

        // Brio aborts the whole snapshot on one bad actor; this must not.
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Good.pose" },
            h.SnapshotFiles(h.DayNow()));
        Assert.True(h.ErrorCount >= 1, "the failed write must be logged as an error");
    }

    [Fact]
    public void SaveNow_continues_after_a_capture_throws()
    {
        using var h = new AutoSaveHarness();
        var boom = h.AddActor("Boom");
        var good = h.AddActor("Good");
        h.FailCaptureFor(boom, new IOException("skeleton copy failed"));

        var captured = h.Service.SaveNow("test");

        // The capture is attempted for both, but only one becomes a candidate.
        Assert.Equal(1, captured);
        Assert.Equal(2, h.CaptureCallCount);
        h.PoseFiles.Received(1).CreatePoseFile(good.Skeletons);
        Assert.True(h.ErrorCount >= 1, "the throwing capture must be logged as an error");

        h.WaitForWrite();
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Good.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_continues_after_the_skeleton_scan_throws_for_one_actor()
    {
        using var h = new AutoSaveHarness();
        h.AddActorThatThrows("Broken", new InvalidOperationException("skeleton gone"));
        var good = h.AddActor("Good");

        var captured = h.Service.SaveNow("test");

        // The broken actor never becomes a candidate, so it is never captured.
        Assert.Equal(1, captured);
        Assert.Equal(1, h.CaptureCallCount);
        h.PoseFiles.Received(1).CreatePoseFile(good.Skeletons);
        Assert.True(h.ErrorCount >= 1, "the failed actor scan must be logged as an error");

        h.WaitForWrite();
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Good.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_suffixes_the_file_when_the_same_second_already_has_one()
    {
        using var h = new AutoSaveHarness();
        var day = Path.Combine(h.Root, h.DayNow());
        Directory.CreateDirectory(day);
        var collided = Path.Combine(day, $"{h.PrefixNow()} Alpha.pose");
        File.WriteAllText(collided, "{}");
        h.AddActor("Alpha");

        var captured = h.Service.SaveNow("test");

        Assert.Equal(1, captured);

        h.WaitForWrite();
        // An exit save in the second an interval save used (or a DST-fold
        // replay) must never overwrite the file already there.
        Assert.Equal("{}", File.ReadAllText(collided));
        Assert.Equal(
            new[]
            {
                $"{h.PrefixNow()} Alpha (2).pose",
                $"{h.PrefixNow()} Alpha.pose",
            },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void SaveNow_passes_the_skeleton_list_from_the_skeleton_service()
    {
        using var h = new AutoSaveHarness();
        var actor = h.AddActor("Alpha");

        h.Service.SaveNow("test");

        var forwarded = Assert.Single(h.CapturedSkeletons);

        // Reference identity: the capture gets exactly what ISkeletonService
        // returned, not a copy or a re-query.
        Assert.Same(actor.Skeletons, forwarded);
        Assert.Equal(h.Skeletons.GetSkeletons(actor.Actor), forwarded);
    }

    [Fact]
    public void SaveNow_coalesces_a_snapshot_that_arrives_while_the_previous_write_is_in_flight()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha");
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("first"));
        hold.WaitUntilHeld();

        // The worker still holds the active item. The bounded writer captures
        // one pending periodic item and replaces it if another tick arrives.
        Assert.Equal(1, h.Service.SaveNow("second"));
        Assert.Equal(2, h.CaptureCallCount);

        hold.Release();
        h.WaitForWrite();
        Assert.Single(h.SnapshotFolders());

        // Once the worker is idle the very same call goes through.
        Assert.Equal(1, h.Service.SaveNow("third"));
        h.WaitForWrite();
    }

    [Fact]
    public void Final_terminal_result_is_written_only_after_the_worker_finishes()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        using var hold = h.HoldWorker();

        h.Service.SaveNow("interval");
        hold.WaitUntilHeld();
        h.Service.CaptureForExit();

        Assert.Equal(
            AutoSaveTerminalStatus.Pending,
            h.Service.LastTerminalResult.Status);

        hold.Release();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveTerminalStatus.Written, terminal.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, h.Service.LastTerminalResult.Status);
    }

    [Fact]
    public void Final_write_failure_returns_recovery_required_after_join()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        var broken = h.AddActor("Broken");
        h.FailWriteFor(broken);

        var capture = h.Service.CaptureForExit();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, capture.Status);
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, terminal.Status);
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, h.Service.LastTerminalResult.Status);
        Assert.True(h.ErrorCount >= 1);
    }

    [Theory]
    [InlineData(AutoSaveHealthStatus.Written)]
    [InlineData(AutoSaveHealthStatus.Cleaned)]
    [InlineData(AutoSaveHealthStatus.RecoveryRequired)]
    [InlineData(AutoSaveHealthStatus.Cancelled)]
    public void Restart_preserves_terminal_health_observation_and_allows_fresh_admission(
        AutoSaveHealthStatus status)
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        var prior = AutoSaveHealthRecord.Create(
            "prior", "previous", status,
            DateTime.UtcNow, DateTime.UtcNow,
            intendedActors: 1,
            writtenActors: status == AutoSaveHealthStatus.Written ? 1 : 0);
        Assert.True(new AutoSaveHealthStore(h.Root).Write(prior).Succeeded);
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root);

        var observed = h.Service.LastHealthRecord;
        Assert.NotNull(observed);
        Assert.Equal(status, observed!.Status);

        var capture = h.Service.CaptureForExit();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, capture.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, terminal.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, h.Service.LastTerminalResult.Status);
        Assert.Equal(AutoSaveHealthStatus.Written, h.Service.LastHealthRecord!.Status);
        Assert.Equal(1, h.CaptureCallCount);
    }

    [Theory]
    [InlineData(AutoSaveHealthStatus.Pending)]
    [InlineData(AutoSaveHealthStatus.Queued)]
    [InlineData(AutoSaveHealthStatus.DispatchAccepted)]
    public void Restart_successfully_promotes_each_nonterminal_and_allows_fresh_admission(
        AutoSaveHealthStatus status)
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        Assert.True(new AutoSaveHealthStore(h.Root).Write(AutoSaveHealthRecord.Create(
            "stale", "previous", status,
            DateTime.UtcNow, DateTime.UtcNow,
            intendedActors: 1)).Succeeded);
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root);

        var observed = h.Service.LastHealthRecord;
        Assert.NotNull(observed);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, observed!.Status);
        Assert.Equal("Interrupted", observed.FailurePhase);

        var capture = h.Service.CaptureForExit();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, capture.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, terminal.Status);
        Assert.Equal(AutoSaveHealthStatus.Written, h.Service.LastHealthRecord!.Status);
        Assert.Equal(1, h.CaptureCallCount);
    }

    [Theory]
    [InlineData(AutoSaveHealthStatus.Pending)]
    [InlineData(AutoSaveHealthStatus.Queued)]
    [InlineData(AutoSaveHealthStatus.DispatchAccepted)]
    public void Restart_failed_nonterminal_promotion_blocks_fresh_admission(
        AutoSaveHealthStatus status)
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        Assert.True(new AutoSaveHealthStore(h.Root).Write(AutoSaveHealthRecord.Create(
            "stale", "previous", status,
            DateTime.UtcNow, DateTime.UtcNow,
            intendedActors: 1)).Succeeded);
        h.HealthStoreOverride = new AutoSaveHealthStore(
            h.Root, new FailingHealthFileSystem());

        var capture = h.Service.CaptureForExit();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, capture.Status);
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, terminal.Status);
        Assert.Equal(0, h.CaptureCallCount);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired,
            h.Service.LastHealthRecord!.Status);
        Assert.Equal("HealthTransition", h.Service.LastHealthRecord.FailurePhase);
        Assert.Equal(status, new AutoSaveHealthStore(h.Root).Read()!.Status);
    }

    [Fact]
    public void Recovery_merge_saturates_existing_and_pending_overflow_counts()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = false;
        h.Settings.CleanOnExit = false;
        var entries = Enumerable.Range(1, 4)
            .Select(index => AutoSaveHealthRecoveryEntry.Create(
                $"seed-{index}", "previous", AutoSaveHealthStatus.RecoveryRequired,
                DateTime.UtcNow, DateTime.UtcNow))
            .ToArray();
        Assert.True(new AutoSaveHealthStore(h.Root).Write(AutoSaveHealthRecord.Create(
            "seed", "previous", AutoSaveHealthStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow,
            recoveryEntries: entries,
            recoveryOverflowCount: int.MaxValue)).Succeeded);
        h.HealthStoreOverride = new AutoSaveHealthStore(
            h.Root, new FailingHealthFileSystem());

        var t0 = new DateTime(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);
        for (var session = 0; session < 6; session++)
        {
            if (session > 0)
            {
                h.GPose.IsGPosing.Returns(false);
                h.TickAt(t0.AddSeconds(session * 2));
                h.GPose.IsGPosing.Returns(true);
                h.TickAt(t0.AddSeconds(session * 2 + 1));
            }

            Assert.Equal(AutoSaveCaptureStatus.NotCaptured,
                h.Service.CaptureForExit().Status);
            Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired,
                h.Service.CompleteForExit().Status);
        }

        Assert.Equal(int.MaxValue, h.Service.LastHealthRecord!.RecoveryOverflowCount);
        Assert.True(h.Service.LastHealthRecord.RecoveryOverflowCount >= 0);
    }

    [Fact]
    public void Older_periodic_terminal_health_cannot_overwrite_final_queued_admission()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        var healthFileSystem = new BlockingFinalWrittenReplaceFileSystem();
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root, healthFileSystem);
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("periodic"));
        hold.WaitUntilHeld();

        var finalCapture = h.Service.CaptureForExit();
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, finalCapture.Status);
        var finalQueued = h.Service.LastHealthRecord;
        Assert.NotNull(finalQueued);
        Assert.Equal(AutoSaveHealthStatus.Queued, finalQueued!.Status);
        healthFileSystem.FinalOperationId = finalQueued.OperationId;

        hold.Release();
        healthFileSystem.WaitUntilFinalReplace();

        // The final replacement is held after the older worker has had the
        // opportunity to finish.  A stale periodic terminal record must not
        // have replaced the final Queued record beneath it.
        var whileBlocked = new AutoSaveHealthStore(h.Root).Read();
        Assert.NotNull(whileBlocked);
        Assert.Equal(finalQueued.OperationId, whileBlocked!.OperationId);
        Assert.Equal(AutoSaveHealthStatus.Queued, whileBlocked.Status);

        healthFileSystem.Release();
        h.WaitForWrite();
        Assert.Equal(AutoSaveTerminalStatus.Written, h.Service.CompleteForExit().Status);

        var terminal = new AutoSaveHealthStore(h.Root).Read();
        Assert.NotNull(terminal);
        Assert.Equal(finalQueued.OperationId, terminal!.OperationId);
        Assert.Equal(AutoSaveHealthStatus.Written, terminal.Status);
    }

    [Fact]
    public void Failed_pending_periodic_cancellation_evidence_survives_final_admission()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        var healthFileSystem = new FailingFlushHealthFileSystem(failFlush: 3);
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root, healthFileSystem);
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("active-periodic"));
        hold.WaitUntilHeld();
        Assert.Equal(1, h.Service.SaveNow("pending-periodic"));
        var pendingOperationId = h.Service.LastHealthRecord!.OperationId;

        var finalCapture = h.Service.CaptureForExit();
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, finalCapture.Status);
        var finalQueued = h.Service.LastHealthRecord;
        Assert.NotNull(finalQueued);
        Assert.NotEqual(pendingOperationId, finalQueued!.OperationId);
        Assert.Equal(AutoSaveHealthStatus.Queued, finalQueued.Status);

        hold.Release();
        h.WaitForWrite();

        var terminal = h.Service.CompleteForExit();
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, terminal.Status);
        Assert.Contains(pendingOperationId, terminal.Detail);
        Assert.Contains("HealthTransition", terminal.Detail);
        Assert.Contains("intended=1", terminal.Detail);

        var health = new AutoSaveHealthStore(h.Root).Read();
        Assert.NotNull(health);
        Assert.Equal(finalQueued.OperationId, health!.OperationId);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, health.Status);
        Assert.Contains(pendingOperationId, health.Detail);
        Assert.DoesNotContain(health.RecoveryEvidencePaths, path => path.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Acknowledged_pending_recovery_does_not_leak_into_the_next_session()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root,
            new FailingFlushHealthFileSystem(failFlush: 3));
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("active-periodic"));
        hold.WaitUntilHeld();
        Assert.Equal(1, h.Service.SaveNow("pending-periodic"));
        var firstPending = h.Service.LastHealthRecord!.OperationId;
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, h.Service.CaptureForExit().Status);
        hold.Release();
        h.WaitForWrite();
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, h.Service.CompleteForExit().Status);

        // The next session starts only after the first terminal merge has been
        // durably acknowledged. Its clean exit must not replay the old entry.
        h.Service.Tick(DateTime.UtcNow);
        Assert.Equal(1, h.Service.SaveNow("next-session"));
        h.WaitForWrite();
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, h.Service.CaptureForExit().Status);
        var second = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveTerminalStatus.Written, second.Status);
        var health = new AutoSaveHealthStore(h.Root).Read()!;
        Assert.Equal(AutoSaveHealthStatus.Written, health.Status);
        Assert.DoesNotContain(health.RecoveryEntries, entry => entry.OperationId == firstPending);
        Assert.Equal(0, health.RecoveryOverflowCount);
    }

    [Fact]
    public void Failed_terminal_publication_does_not_duplicate_pending_recovery_on_no_admission_exit()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        h.HealthStoreOverride = new AutoSaveHealthStore(h.Root,
            new FailingFlushHealthFileSystem(failFlush: 6));
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("active-periodic"));
        hold.WaitUntilHeld();
        Assert.Equal(1, h.Service.SaveNow("pending-periodic"));
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, h.Service.CaptureForExit().Status);
        hold.Release();
        h.WaitForWrite();
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired,
            h.Service.CompleteForExit().Status);

        // Reset the exit state without admitting another operation. The next
        // merge must deduplicate the recovery already present in the current
        // health record, then acknowledge and clear the pending set.
        h.Settings.Enabled = true;
        h.Service.Tick(DateTime.UtcNow);
        h.Settings.Enabled = false;
        Assert.Equal(AutoSaveCaptureStatus.NotCaptured,
            h.Service.CaptureForExit().Status);
        var second = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, second.Status);
        var health = new AutoSaveHealthStore(h.Root).Read()!;
        var ids = health.RecoveryEntries.Select(entry => entry.OperationId).ToArray();
        Assert.Equal(ids.Distinct(StringComparer.Ordinal), ids);
        Assert.Equal(0, health.RecoveryOverflowCount);
    }

    [Fact]
    public void Failed_periodic_coalescing_cancellation_is_reported_without_admitting_replacement()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        h.HealthStoreOverride = new AutoSaveHealthStore(
            h.Root,
            new FailingFlushHealthFileSystem(failFlush: 3));
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("active-periodic"));
        hold.WaitUntilHeld();
        Assert.Equal(1, h.Service.SaveNow("pending-periodic"));
        var pendingOperationId = h.Service.LastHealthRecord!.OperationId;

        var replacement = h.Service.SaveNow("replacement-periodic");
        Assert.Equal(1, replacement);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired,
            h.Service.LastHealthRecord!.Status);
        Assert.Equal(pendingOperationId, h.Service.LastHealthRecord.OperationId);
        Assert.Contains("health write failed", h.Service.LastHealthRecord.Detail);
        Assert.Equal(3, h.CaptureCallCount);

        hold.Release();
        var terminal = h.Service.CompleteForExit();
        Assert.Equal(AutoSaveTerminalStatus.RecoveryRequired, terminal.Status);
        Assert.Contains(pendingOperationId, terminal.Detail);
        Assert.Contains("HealthTransition", terminal.Detail);
    }

    [Fact]
    public void Terminal_health_reports_exact_written_count_and_paths()
    {
        using var h = new AutoSaveHarness();
        var bad = h.AddActor("Bad");
        h.AddActor("Good");
        h.FailWriteFor(bad);

        h.Service.SaveNow("terminal-evidence");
        h.WaitForWrite();

        var health = new AutoSaveHealthStore(h.Root).Read();
        Assert.NotNull(health);
        Assert.Equal(2, health!.IntendedActors);
        Assert.Equal(1, health.WrittenActors);
        Assert.Equal(
            new[]
            {
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Bad.pose"),
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Good.pose"),
            },
            health.AffectedPaths);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, health.Status);
    }

    [Fact]
    public void Health_admission_failure_does_not_publish_a_worker_job()
    {
        using var h = new AutoSaveHarness();
        var dispatchCalls = 0;
        h.Dispatch = _ =>
        {
            dispatchCalls++;
            return true;
        };
        h.HealthStoreOverride = new AutoSaveHealthStore(
            h.Root,
            new FailingHealthFileSystem());
        h.AddActor("Alpha");

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.Failure, result.Status);
        Assert.False(result.DispatchAccepted);
        Assert.Equal(1, h.CaptureCallCount);
        Assert.Equal(0, dispatchCalls);
        Assert.Empty(h.SnapshotFolders());
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired,
            h.Service.LastHealthRecord!.Status);
    }

    [Fact]
    public void Startup_stale_health_failure_blocks_capture_with_recovery_evidence()
    {
        using var h = new AutoSaveHarness();
        var normal = new AutoSaveHealthStore(h.Root);
        Assert.True(normal.Write(AutoSaveHealthRecord.Create(
            "stale", "interval", AutoSaveHealthStatus.Queued,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 1)).Succeeded);
        h.HealthStoreOverride = new AutoSaveHealthStore(
            h.Root,
            new FailingHealthFileSystem());
        h.AddActor("Alpha");

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, result.Status);
        Assert.Equal(0, h.CaptureCallCount);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired,
            h.Service.LastHealthRecord!.Status);
        Assert.Equal("HealthTransition", h.Service.LastHealthRecord.FailurePhase);
        Assert.False(string.IsNullOrEmpty(h.Service.LastHealthRecord.Detail));
        Assert.Equal(
            AutoSaveTerminalStatus.RecoveryRequired,
            h.Service.CompleteForExit().Status);
    }

    [Fact]
    public void Multiple_actor_failures_preserve_ordered_paths_and_later_success()
    {
        using var h = new AutoSaveHarness();
        var first = h.AddActor("First");
        var middle = h.AddActor("Middle");
        h.AddActor("Last");
        h.FailWriteFor(first);
        h.FailWriteFor(middle);

        Assert.Equal(3, h.Service.SaveNow("multiple-failures"));
        h.WaitForWrite();

        var health = new AutoSaveHealthStore(h.Root).Read()!;
        Assert.Equal(3, health.IntendedActors);
        Assert.Equal(1, health.WrittenActors);
        Assert.Equal(
            new[]
            {
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} First.pose"),
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Middle.pose"),
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Last.pose"),
            },
            health.AffectedPaths);
        Assert.Equal(new[] { $"{h.PrefixNow()} Last.pose" }, h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void Every_pose_in_a_snapshot_records_where_it_was_taken()
    {
        var place = Substitute.For<IPlaceService>();
        place.Current.Returns(new CapturePlace(129u, "Limsa Lominsa Lower Decks"));
        using var h = new AutoSaveHarness { Place = place };
        h.AddActor("Alpha");
        h.AddActor("Beta");

        Assert.Equal(2, h.Service.SaveNow("place"));
        h.WaitForWrite();

        foreach (var name in h.SnapshotFiles(h.DayNow()))
        {
            var path = Path.Combine(h.Root, h.DayNow(), name);
            var read = AtomicPoseFileStore.Default.Read(path);
            Assert.True(read.Succeeded);
            Assert.Equal(129u, read.Pose!.TerritoryId);
            Assert.Equal("Limsa Lominsa Lower Decks", read.Pose.PlaceName);

            // The library's rail reads the METADATA probe, not the document.
            var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
            Assert.True(metadata.Succeeded);
            Assert.Equal("Limsa Lominsa Lower Decks", metadata.PlaceName);
        }
    }

    [Fact]
    public void A_snapshot_taken_with_no_place_service_writes_neither_member()
    {
        // The legacy shape, and the shape a listing must group by day alone:
        // ABSENT, not null and not a placeholder.
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha");

        Assert.Equal(1, h.Service.SaveNow("no-place"));
        h.WaitForWrite();

        var name = Assert.Single(h.SnapshotFiles(h.DayNow()));
        var json = File.ReadAllText(Path.Combine(h.Root, h.DayNow(), name));
        Assert.DoesNotContain(nameof(PoseFile.PlaceName), json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PoseFile.TerritoryId), json, StringComparison.Ordinal);

        var metadata = AtomicPoseFileStore.Default.ReadMetadata(
            Path.Combine(h.Root, h.DayNow(), name));
        Assert.True(metadata.Succeeded);
        Assert.Null(metadata.PlaceName);
    }

    private sealed class FailingHealthFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner =
            new SystemAutoSaveHealthFileSystem();

        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => throw new IOException("health admission failed");
        public void FlushToDisk(Stream stream) => throw new IOException("health flush failed");
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) =>
            throw new IOException("health replace failed");
        public void Move(string source, string destination) =>
            throw new IOException("health move failed");
        public void Delete(string path) { }
    }

    private sealed class BlockingFinalWrittenReplaceFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner =
            new SystemAutoSaveHealthFileSystem();
        private readonly ManualResetEventSlim _reached = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public string? FinalOperationId { get; set; }

        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);

        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(source));
            var operationId = document.RootElement.GetProperty("OperationId").GetString();
            var status = (AutoSaveHealthStatus)document.RootElement.GetProperty("Status").GetInt32();
            if (status == AutoSaveHealthStatus.Written && operationId == FinalOperationId)
            {
                _reached.Set();
                Assert.True(_release.Wait(TimeSpan.FromSeconds(5)),
                    "the final health transition never released");
            }
            _inner.Replace(source, destination, backup);
        }
        public void Move(string source, string destination) =>
            _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);

        public void WaitUntilFinalReplace() => Assert.True(
            _reached.Wait(TimeSpan.FromSeconds(5)),
            "the final health transition never reached its hold");

        public void Release() => _release.Set();
    }

    private sealed class FailingFlushHealthFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner =
            new SystemAutoSaveHealthFileSystem();
        private readonly int _failFlush;
        private int _flushCount;

        public FailingFlushHealthFileSystem(int failFlush) => _failFlush = failFlush;

        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream)
        {
            if (Interlocked.Increment(ref _flushCount) == _failFlush)
                throw new IOException("pending periodic cancellation health write failed");
            _inner.FlushToDisk(stream);
        }

        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) =>
            _inner.Replace(source, destination, backup);
        public void Move(string source, string destination) =>
            _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
    }
}
