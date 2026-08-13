using System;
using System.IO;
using NSubstitute;
using Poser.Core;
using Poser.Services;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

/// <summary>
/// When a snapshot happens: interval arming/disarming, gating on the config
/// flag and on GPose, and the GPose-exit edge.
///
/// <para>Counted in CAPTURES, because that is the half <c>Tick</c> decides. The
/// harness's <c>TickAt</c> waits out each tick's write worker, without which a
/// tick landing on a still-running write would be dropped by the service and
/// the count would silently lag.</para>
/// </summary>
public class AutoSaveServiceTriggerTests
{
    private static readonly DateTime T0 = new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Tick_never_saves_while_disabled()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = false;
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));
        h.TickAt(At(60));
        h.TickAt(At(6000));

        Assert.Equal(0, h.CaptureCallCount);
        Assert.Empty(Directory.GetDirectories(h.Root));
        Assert.Null(h.Service.LastSaveUtc);
    }

    [Fact]
    public void Tick_never_saves_outside_gpose()
    {
        using var h = new AutoSaveHarness();
        h.GPose.IsGPosing.Returns(false);
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));
        h.TickAt(At(60));
        h.TickAt(At(6000));

        Assert.Equal(0, h.CaptureCallCount);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public void Tick_arms_on_the_first_tick_and_saves_exactly_one_interval_later()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));
        Assert.Equal(0, h.CaptureCallCount);   // entering GPose never saves immediately

        h.TickAt(At(59));
        Assert.Equal(0, h.CaptureCallCount);

        h.TickAt(At(60));
        Assert.Equal(1, h.CaptureCallCount);
        Assert.Equal(At(60), h.Service.LastSaveUtc);
    }

    [Fact]
    public void Leaving_and_re_entering_gpose_re_arms_the_interval()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));                      // armed, due at 60

        h.GPose.IsGPosing.Returns(false);
        h.TickAt(At(30));                     // disarmed

        h.GPose.IsGPosing.Returns(true);
        h.TickAt(At(31));                     // re-armed, due at 91

        h.TickAt(At(60));
        Assert.Equal(0, h.CaptureCallCount);   // the pre-exit schedule is gone

        h.TickAt(At(90));
        Assert.Equal(0, h.CaptureCallCount);

        h.TickAt(At(91));
        Assert.Equal(1, h.CaptureCallCount);
    }

    [Fact]
    public void Disabling_mid_session_disarms_and_re_enabling_re_arms()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));                      // armed, due at 60
        h.Settings.Enabled = false;
        h.TickAt(At(30));                     // disarmed
        h.Settings.Enabled = true;
        h.TickAt(At(40));                     // re-armed, due at 100

        h.TickAt(At(99));
        Assert.Equal(0, h.CaptureCallCount);

        h.TickAt(At(100));
        Assert.Equal(1, h.CaptureCallCount);
    }

    [Fact]
    public void A_shorter_interval_configured_before_arming_is_honoured()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 10;
        h.AddActor("Alpha");

        h.TickAt(At(0));
        h.TickAt(At(9));
        Assert.Equal(0, h.CaptureCallCount);

        h.TickAt(At(10));
        Assert.Equal(1, h.CaptureCallCount);
    }

    /// <summary>
    /// Implemented contract (AutoSaveService.Tick): the interval is re-read on
    /// every tick, but the already-scheduled due time is not retro-shifted. A
    /// config change therefore takes effect at the NEXT scheduling — no restart
    /// and no re-configure call anywhere.
    /// </summary>
    [Fact]
    public void A_shorter_interval_configured_mid_session_applies_to_the_next_arming()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));                      // armed, due at 60
        h.TickAt(At(60));                     // save 1, re-armed at 120
        Assert.Equal(1, h.CaptureCallCount);

        h.Settings.IntervalSeconds = 10;      // no restart, no re-configure call

        h.TickAt(At(119));
        Assert.Equal(1, h.CaptureCallCount);

        h.TickAt(At(120));                    // save 2, re-armed with 10 -> 130
        Assert.Equal(2, h.CaptureCallCount);

        h.TickAt(At(129));
        Assert.Equal(2, h.CaptureCallCount);

        h.TickAt(At(130));
        Assert.Equal(3, h.CaptureCallCount);
    }

    [Fact]
    public void A_longer_interval_configured_mid_session_applies_to_the_next_arming()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));                      // armed, due at 60
        h.TickAt(At(60));                     // save 1, re-armed at 120
        Assert.Equal(1, h.CaptureCallCount);

        h.Settings.IntervalSeconds = 120;

        h.TickAt(At(120));                    // save 2, re-armed with 120 -> 240
        Assert.Equal(2, h.CaptureCallCount);

        h.TickAt(At(239));
        Assert.Equal(2, h.CaptureCallCount);

        h.TickAt(At(240));
        Assert.Equal(3, h.CaptureCallCount);
    }

    [Fact]
    public void Exit_capture_takes_one_final_snapshot()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        var alpha = h.AddActor("Alpha");
        h.AddActor("Beta", authored: false);

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, result.Status);
        Assert.Equal(1, h.CaptureCallCount);
        h.PoseFiles.Received(1).CreatePoseFile(alpha.Skeletons);

        h.WaitForWrite();
        Assert.Single(Directory.GetDirectories(h.Root));
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Alpha.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void Final_capture_is_idempotent_after_reserving_behind_periodic_work()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");

        using var hold = h.HoldWorker();
        var periodic = h.Service.SaveNow("interval");
        hold.WaitUntilHeld();
        var first = h.Service.CaptureForExit();
        var second = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, first.Status);
        Assert.True(first.DispatchAccepted);
        Assert.True(first.CaptureCompleted);
        Assert.Equal(first.Status, second.Status);
        Assert.True(second.DispatchAccepted);
        Assert.True(second.CaptureCompleted);
        Assert.Equal(first.CapturedActors, second.CapturedActors);
        Assert.Equal(2, h.CaptureCallCount);
        Assert.Equal(h.NowUtc, h.Service.LastSaveUtc);

        Assert.Equal(1, periodic);
        hold.Release();
        var terminal = h.Service.CompleteForExit();
        Assert.Equal(AutoSaveTerminalStatus.Written, terminal.Status);
        h.WaitForWrite();
    }

    [Fact]
    public void Final_capture_is_reserved_behind_active_periodic_work_and_is_not_dropped()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("interval"));
        hold.WaitUntilHeld();

        var final = h.Service.CaptureForExit();
        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, final.Status);
        Assert.True(final.DispatchAccepted);

        hold.Release();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveTerminalStatus.Written, terminal.Status);
        Assert.Equal(2, h.CaptureCallCount);
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Alpha (2).pose", $"{h.PrefixNow()} Alpha.pose" },
            h.SnapshotFiles(h.DayNow()));
    }

    [Fact]
    public void Periodic_admission_is_closed_after_final_reservation()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");

        var final = h.Service.CaptureForExit();
        var periodic = h.Service.SaveNow("interval-after-final");

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, final.Status);
        Assert.Equal(0, periodic);
        Assert.Equal(AutoSaveTerminalStatus.Written, h.Service.CompleteForExit().Status);
    }

    [Fact]
    public void Clean_on_exit_joins_active_periodic_work_before_deleting()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = true;
        h.AddActor("Alpha");
        using var hold = h.HoldWorker();

        Assert.Equal(1, h.Service.SaveNow("interval"));
        hold.WaitUntilHeld();

        var exit = h.Service.CaptureForExit();
        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, exit.Status);
        Assert.True(Directory.Exists(h.Root));

        hold.Release();
        var terminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveTerminalStatus.Cleaned, terminal.Status);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public void Clean_on_exit_reports_recovery_when_root_disappears_during_final_enumeration()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = true;
        h.SeedSnapshot("2026-03-04 05-00-00Z", withFile: true);
        h.Log.When(log => log.Info(
                Arg.Any<string>(),
                Arg.Any<object[]>()))
            .Do(_ =>
            {
                if (Directory.Exists(h.Root))
                    Directory.Delete(h.Root, recursive: true);
            });

        var capture = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, capture.Status);
        Assert.Equal(
            AutoSaveTerminalStatus.RecoveryRequired,
            h.Service.LastTerminalResult.Status);
        Assert.Contains(
            "could not remove every snapshot",
            h.Service.LastTerminalResult.Detail);
    }

    [Fact]
    public void Construction_does_not_snapshot_or_clean()
    {
        using var h = new AutoSaveHarness();
        h.SeedSnapshot("2026-03-04 05-00-00Z");
        h.AddActor("Alpha");

        _ = h.Service;

        Assert.Equal(0, h.CaptureCallCount);
        Assert.Single(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public void Leaving_gpose_with_CleanOnExit_deletes_every_snapshot_and_captures_nothing()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = true;
        h.AddActor("Alpha");

        h.SeedSnapshot("2026-03-04 05-00-00Z", withFile: true);
        h.SeedSnapshot("2026-03-04 05-01-00Z", withFile: true);
        h.SeedSnapshot("2026-03-04 05-02-00Z", withFile: true);

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, result.Status);
        Assert.Equal(0, h.CaptureCallCount);
        Assert.Empty(Directory.GetDirectories(h.Root));
        Assert.True(Directory.Exists(h.Root), "only the snapshots go, not the root");
    }

    [Fact]
    public void Leaving_gpose_while_disabled_neither_captures_nor_deletes()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = false;
        h.Settings.CleanOnExit = true;   // must be ignored while disabled
        h.AddActor("Alpha");

        h.SeedSnapshot("2026-03-04 05-00-00Z", withFile: true);
        h.SeedSnapshot("2026-03-04 05-01-00Z", withFile: true);

        var result = h.Service.CaptureForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, result.Status);
        Assert.Equal(0, h.CaptureCallCount);
        Assert.Equal(2, Directory.GetDirectories(h.Root).Length);
    }

    [Fact]
    public void Leaving_gpose_disarms_the_interval_even_when_disabled()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.AddActor("Alpha");

        h.TickAt(At(0));                                                  // armed, due at 60
        h.Settings.Enabled = false;
        var result = h.Service.CaptureForExit();  // disarms first
        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, result.Status);
        h.Settings.Enabled = true;

        h.TickAt(At(60));                                                 // re-arms, due at 120
        Assert.Equal(0, h.CaptureCallCount);

        h.TickAt(At(120));
        Assert.Equal(1, h.CaptureCallCount);
    }

    [Fact]
    public void New_disabled_gpose_session_does_not_reuse_completed_exit_evidence()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");

        var firstCapture = h.Service.CaptureForExit();
        var firstTerminal = h.Service.CompleteForExit();
        var firstHealth = h.Service.LastHealthRecord;

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, firstCapture.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, firstTerminal.Status);
        Assert.NotNull(firstHealth);
        Assert.Equal(1, h.CaptureCallCount);

        h.GPose.IsGPosing.Returns(false);
        h.TickAt(At(1));
        h.Settings.Enabled = false;
        h.GPose.IsGPosing.Returns(true);
        h.TickAt(At(2));
        h.TickAt(At(3));
        h.TickAt(At(4));

        var secondCapture = h.Service.CaptureForExit();
        var secondTerminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.NotCaptured, secondCapture.Status);
        Assert.Contains("disabled", secondCapture.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AutoSaveTerminalStatus.NotAttempted, secondTerminal.Status);
        Assert.Equal(AutoSaveTerminalStatus.NotAttempted,
            h.Service.LastTerminalResult.Status);
        Assert.Equal(1, h.CaptureCallCount);
        Assert.Null(h.Service.LastSaveUtc);
        Assert.Equal(firstHealth!.OperationId, h.Service.LastHealthRecord!.OperationId);
        Assert.Single(h.SnapshotFolders());
    }

    [Fact]
    public void Reenabling_same_session_is_idempotent_but_reentry_starts_a_fresh_session()
    {
        using var h = new AutoSaveHarness();
        h.Settings.Enabled = true;
        h.Settings.CleanOnExit = false;
        h.AddActor("Alpha");

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted,
            h.Service.CaptureForExit().Status);
        Assert.Equal(AutoSaveTerminalStatus.Written,
            h.Service.CompleteForExit().Status);

        h.GPose.IsGPosing.Returns(false);
        h.Settings.Enabled = false;
        h.TickAt(At(1));
        h.GPose.IsGPosing.Returns(true);
        h.TickAt(At(2));
        Assert.Equal(AutoSaveCaptureStatus.NotCaptured,
            h.Service.CaptureForExit().Status);
        Assert.Equal(AutoSaveTerminalStatus.NotAttempted,
            h.Service.CompleteForExit().Status);

        // Toggling the setting does not create a new GPose session. The
        // completed disabled exit therefore remains idempotent.
        h.Settings.Enabled = true;
        h.TickAt(At(3));
        Assert.Equal(AutoSaveCaptureStatus.NotCaptured,
            h.Service.CaptureForExit().Status);
        Assert.Equal(AutoSaveTerminalStatus.NotAttempted,
            h.Service.CompleteForExit().Status);
        Assert.Equal(1, h.CaptureCallCount);

        h.GPose.IsGPosing.Returns(false);
        h.TickAt(At(4));
        h.GPose.IsGPosing.Returns(true);
        h.TickAt(At(5));

        var freshCapture = h.Service.CaptureForExit();
        var freshTerminal = h.Service.CompleteForExit();

        Assert.Equal(AutoSaveCaptureStatus.DispatchStarted, freshCapture.Status);
        Assert.Equal(AutoSaveTerminalStatus.Written, freshTerminal.Status);
        Assert.Equal(2, h.CaptureCallCount);
    }

    [Fact]
    public void Constructor_does_not_subscribe_to_the_gpose_exit_event()
    {
        using var h = new AutoSaveHarness();

        _ = h.Service;

        h.EventBus.DidNotReceive().Subscribe(
            Arg.Any<Action<Poser.Core.GPoseStateChangedEvent>>());
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var h = new AutoSaveHarness();
        var service = h.Service;

        service.Dispose();
        service.Dispose();

        h.EventBus.DidNotReceive().Unsubscribe(
            Arg.Any<Action<Poser.Core.GPoseStateChangedEvent>>());
    }
}
