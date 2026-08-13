using System;
using System.IO;
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

        // LastSaveUtc is stamped by the capture half, before the write.
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
    public void SaveNow_drops_a_snapshot_that_arrives_while_the_previous_write_is_in_flight()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha");
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("first"));
        hold.WaitUntilHeld();

        // The worker still holds the latch, and the service drops rather than
        // queues — the next interval is a fresher capture than any backlog
        // entry would be.
        Assert.Equal(0, h.Service.SaveNow("second"));
        Assert.Equal(1, h.CaptureCallCount);

        hold.Release();
        h.WaitForWrite();
        Assert.Single(h.SnapshotFolders());

        // Once the worker is idle the very same call goes through.
        Assert.Equal(1, h.Service.SaveNow("third"));
        h.WaitForWrite();
    }
}
