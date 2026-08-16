using System;
using NSubstitute;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

public sealed class AutoSaveServiceTriggerTests
{
    private static readonly DateTime T0 = new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Trigger_state_machine_gates_on_enabled_gpose_and_rearming_edges()
    {
        using var h = new AutoSaveHarness();
        h.Settings.IntervalSeconds = 60;
        h.Settings.Enabled = false;
        h.AddActor("Alpha");

        h.TickAt(T0);
        h.TickAt(T0.AddMinutes(10));
        Assert.Equal(0, h.CaptureCallCount);

        h.Settings.Enabled = true;
        h.TickAt(T0.AddMinutes(11));
        h.TickAt(T0.AddMinutes(11).AddSeconds(59));
        Assert.Equal(0, h.CaptureCallCount);
        h.TickAt(T0.AddMinutes(12));
        Assert.Equal(1, h.CaptureCallCount);

        h.GPose.IsGPosing.Returns(false);
        h.TickAt(T0.AddMinutes(12).AddSeconds(30));
        h.GPose.IsGPosing.Returns(true);
        h.TickAt(T0.AddMinutes(12).AddSeconds(31));
        h.TickAt(T0.AddMinutes(13).AddSeconds(30));
        Assert.Equal(1, h.CaptureCallCount);
        h.TickAt(T0.AddMinutes(13).AddSeconds(31));
        Assert.Equal(2, h.CaptureCallCount);
    }

    [Fact]
    public void Disposal_closes_admission_and_is_idempotent_after_the_worker_is_idle()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha");
        Assert.Equal(1, h.Service.SaveNow("manual"));
        h.WaitForWrite();

        h.Service.Dispose();
        h.Service.Dispose();

        Assert.Equal(0, h.Service.SaveNow("after-dispose"));
        Assert.Equal(1, h.CaptureCallCount);
    }
}
