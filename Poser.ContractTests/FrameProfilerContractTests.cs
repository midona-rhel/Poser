using Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the frame profiler's two load-bearing claims.
///
/// <para>THE ARITHMETIC. Scopes nest, so a label's own cost is its span minus
/// the spans of the scopes opened inside it; a label measured several times in
/// one frame contributes the sum; a label NOT measured this frame contributes
/// zero rather than keeping its last reading. The smoothed figure seeds on the
/// first frame — an average that starts at zero would read a real 4 ms pane as
/// 0.2 ms for the first second, which is exactly the window in which someone
/// selects an actor and looks at the panel.</para>
///
/// <para>THE OVERHEAD. The profiler is permanent tooling compiled into every
/// build, so a disabled scope has to cost nothing observable, and an enabled
/// one has to stop allocating as soon as its labels are known. A test can pin
/// allocation exactly (<see cref="GC.GetAllocatedBytesForCurrentThread"/>); it
/// cannot pin instruction count, and this file does not pretend to.</para>
///
/// <para>The clock is pinned throughout: wall-clock assertions in a test suite
/// are how a profiler's own tests become the flakiest thing in the build.
/// </para>
/// </summary>
public sealed class FrameProfilerContractTests : IDisposable
{
    private static readonly double MsPerTick = FrameProfiler.MillisecondsPerTick;

    public FrameProfilerContractTests()
    {
        FrameProfiler.Reset();
        FrameProfiler.SetEnabled(true);
        FrameProfiler.ManualClock = 0L;
    }

    public void Dispose()
    {
        FrameProfiler.ManualClock = null;
        FrameProfiler.SetEnabled(false);
        FrameProfiler.BeginFrame();
        FrameProfiler.EndFrame();
        FrameProfiler.Reset();
    }

    /// <summary>Advances the pinned clock and runs one measured span.</summary>
    private static void Span(string label, long ticks)
    {
        using var scope = FrameProfiler.Scope(label);
        FrameProfiler.ManualClock += ticks;
    }

    private static FrameProfiler.Sample Read(string label)
    {
        var buffer = new FrameProfiler.Sample[FrameProfiler.LabelCount];
        int written = FrameProfiler.Snapshot(buffer);
        for (int i = 0; i < written; i++)
            if (buffer[i].Label == label)
                return buffer[i];
        Assert.Fail($"no slot for {label}");
        return default;
    }

    // ── the arithmetic ───────────────────────────────────────────────────

    [Fact]
    public void A_nested_scope_is_charged_to_itself_and_not_to_its_parent()
    {
        FrameProfiler.BeginFrame();
        using (FrameProfiler.Scope("outer"))
        {
            FrameProfiler.ManualClock += 100L;   // outer's own work
            Span("inner", 400L);
            FrameProfiler.ManualClock += 100L;   // outer's own work
        }
        FrameProfiler.EndFrame();

        var outer = Read("outer");
        var inner = Read("inner");
        Assert.Equal(200L * MsPerTick, outer.AverageSelfMs, 9);
        Assert.Equal(600L * MsPerTick, outer.AverageInclusiveMs, 9);
        Assert.Equal(400L * MsPerTick, inner.AverageSelfMs, 9);
        Assert.Equal(400L * MsPerTick, inner.AverageInclusiveMs, 9);
    }

    [Fact]
    public void One_label_measured_several_times_in_a_frame_reports_the_sum()
    {
        FrameProfiler.BeginFrame();
        Span("row", 30L);
        Span("row", 50L);
        Span("row", 20L);
        FrameProfiler.EndFrame();

        var row = Read("row");
        Assert.Equal(100L * MsPerTick, row.AverageSelfMs, 9);
        Assert.Equal(3, row.Hits);
    }

    [Fact]
    public void The_average_seeds_on_the_first_frame_instead_of_starting_at_zero()
    {
        FrameProfiler.BeginFrame();
        Span("pane", 1000L);
        FrameProfiler.EndFrame();

        // Not 0.05 x the sample: a fresh label reads its real cost at once.
        Assert.Equal(1000L * MsPerTick, Read("pane").AverageSelfMs, 9);
    }

    [Fact]
    public void The_average_converges_on_a_steady_cost()
    {
        FrameProfiler.BeginFrame();
        Span("pane", 100L);
        FrameProfiler.EndFrame();

        for (int frame = 0; frame < 400; frame++)
        {
            FrameProfiler.BeginFrame();
            Span("pane", 900L);
            FrameProfiler.EndFrame();
        }

        Assert.Equal(900L * MsPerTick, Read("pane").AverageSelfMs, 6);
    }

    [Fact]
    public void The_average_moves_one_smoothing_step_per_frame()
    {
        FrameProfiler.BeginFrame();
        Span("pane", 0L);
        FrameProfiler.EndFrame();

        FrameProfiler.BeginFrame();
        Span("pane", 1000L);
        FrameProfiler.EndFrame();

        Assert.Equal(
            FrameProfiler.Smoothing * 1000L * MsPerTick,
            Read("pane").AverageSelfMs,
            9);
    }

    [Fact]
    public void A_label_that_stops_being_drawn_decays_instead_of_standing_still()
    {
        FrameProfiler.BeginFrame();
        Span("closed-window", 1000L);
        FrameProfiler.EndFrame();
        double seeded = Read("closed-window").AverageSelfMs;

        for (int frame = 0; frame < 200; frame++)
        {
            FrameProfiler.BeginFrame();
            Span("other", 10L);
            FrameProfiler.EndFrame();
        }

        var closed = Read("closed-window");
        Assert.Equal(0, closed.Hits);
        Assert.True(closed.AverageSelfMs < seeded * 0.01);
    }

    [Fact]
    public void The_peak_is_the_worst_frame_and_survives_quiet_ones()
    {
        FrameProfiler.BeginFrame();
        Span("spike", 100L);
        FrameProfiler.EndFrame();
        FrameProfiler.BeginFrame();
        Span("spike", 5000L);
        FrameProfiler.EndFrame();
        FrameProfiler.BeginFrame();
        Span("spike", 100L);
        FrameProfiler.EndFrame();

        Assert.Equal(5000L * MsPerTick, Read("spike").PeakSelfMs, 9);
        Assert.True(Read("spike").AverageSelfMs < 5000L * MsPerTick);

        FrameProfiler.ResetPeaks();
        Assert.Equal(0.0, Read("spike").PeakSelfMs, 9);
        Assert.Equal(0.0, FrameProfiler.PeakFrameMs, 9);
    }

    [Fact]
    public void The_frame_total_is_the_whole_callback_not_the_sum_of_the_labels()
    {
        FrameProfiler.BeginFrame();
        FrameProfiler.ManualClock += 70L;   // unmeasured work in the callback
        Span("a", 10L);
        Span("b", 20L);
        FrameProfiler.EndFrame();

        Assert.Equal(100L * MsPerTick, FrameProfiler.LastFrameMs, 9);
        Assert.Equal(100L * MsPerTick, FrameProfiler.AverageFrameMs, 9);
    }

    [Fact]
    public void The_switch_is_applied_at_a_frame_boundary_and_not_before()
    {
        FrameProfiler.BeginFrame();
        Assert.True(FrameProfiler.Enabled);

        // Switched off mid-frame. The frame that is already open keeps
        // recording, because its nesting stack cannot survive losing the
        // closes of scopes that have already opened.
        FrameProfiler.SetEnabled(false);
        Assert.False(FrameProfiler.Requested);
        Assert.True(FrameProfiler.Enabled);
        Span("mid-frame", 1000L);
        FrameProfiler.EndFrame();
        Assert.Equal(1000L * MsPerTick, Read("mid-frame").AverageSelfMs, 9);

        // The next boundary honours it, and nothing after that takes a slot.
        FrameProfiler.BeginFrame();
        Assert.False(FrameProfiler.Enabled);
        Span("after-the-switch", 1000L);
        FrameProfiler.EndFrame();
        Assert.Equal(1, FrameProfiler.LabelCount);
    }

    // ── the overhead ─────────────────────────────────────────────────────

    [Fact]
    public void A_disabled_scope_allocates_nothing()
    {
        FrameProfiler.SetEnabled(false);
        FrameProfiler.BeginFrame();
        FrameProfiler.EndFrame();
        Assert.False(FrameProfiler.Enabled);

        Churn(64);   // warm the JIT before the ledger is read
        long before = GC.GetAllocatedBytesForCurrentThread();
        Churn(10_000);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void An_enabled_frame_allocates_nothing_once_its_labels_are_known()
    {
        // The first pass interns the label slots; every pass after it must
        // run entirely out of the preallocated arrays.
        for (int frame = 0; frame < 4; frame++)
        {
            FrameProfiler.BeginFrame();
            Churn(64);
            FrameProfiler.EndFrame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 200; frame++)
        {
            FrameProfiler.BeginFrame();
            Churn(50);
            FrameProfiler.EndFrame();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Reading_the_ledger_into_a_reused_buffer_allocates_nothing()
    {
        FrameProfiler.BeginFrame();
        Churn(8);
        FrameProfiler.EndFrame();

        var buffer = new FrameProfiler.Sample[128];
        FrameProfiler.Snapshot(buffer);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int read = 0; read < 200; read++)
            FrameProfiler.Snapshot(buffer);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    /// <summary>Opens and closes the shell's own nesting shape, repeatedly.
    /// The labels are constants for the reason the profiler documents: a
    /// per-frame built label would allocate before the ledger ever ran.
    /// </summary>
    private static void Churn(int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            using (FrameProfiler.Scope("window"))
            using (FrameProfiler.Scope("column"))
            {
                using (FrameProfiler.Scope("section-a")) { }
                using (FrameProfiler.Scope("section-b")) { }
            }
        }
    }
}
