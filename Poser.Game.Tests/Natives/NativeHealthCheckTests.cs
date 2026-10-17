using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Diagnostics;
using Poser.Game.Natives;
using Xunit;

namespace Poser.Game.Tests.Natives;

public sealed unsafe class NativeHealthCheckTests : IDisposable
{
    // Every pattern "matches" here; each reader's displacement is read from this buffer.
    private readonly nint _code = Marshal.AllocHGlobal(0x1000);

    public NativeHealthCheckTests() => new Span<byte>((void*)_code, 0x1000).Clear();

    public void Dispose() => Marshal.FreeHGlobal(_code);

    private NativeHealthReport Run(bool found)
    {
        var scanner = Substitute.For<ISigScanner>();
        scanner.TryScanText(Arg.Any<string>(), out Arg.Any<nint>()).Returns(call =>
        {
            call[1] = found ? _code : nint.Zero;
            return found;
        });
        var check = new NativeHealthCheck(scanner, new HookLedger(Substitute.For<IGameInteropProvider>()),
            Substitute.For<IPluginLog>());
        for (int i = 0; i < 400 && !check.Report.Complete; i++)
            Thread.Sleep(10);
        Assert.True(check.Report.Complete);
        return check.Report;
    }

    private static NativeHealthRow Row(NativeHealthReport report, string name) =>
        report.Rows.Single(r => r.Name == name);

    [Fact]
    public void An_offset_the_game_still_reads_is_ok_and_another_value_is_moved()
    {
        var speed = NativeRegistry.Offsets["SchedulerTimeline.Speed"];
        *(int*)(_code + speed.DisplacementIndex) = speed.Analysed;
        var report = Run(found: true);

        Assert.Equal(NativeHealthState.Ok, Row(report, "SchedulerTimeline.Speed").State);
        // Another 4-byte reader at the same displacement index now reads Speed's value.
        var other = NativeRegistry.Offsets.First(o => o.Key != "SchedulerTimeline.Speed"
            && o.Value.DisplacementIndex == speed.DisplacementIndex && o.Value.Size == 4
            && o.Value.Analysed != speed.Analysed
            && NativeFieldChecks.All.Any(f => f.Entry == o.Key && f.Delta == 0));
        var moved = Row(report, other.Key);
        Assert.Equal(NativeHealthState.Moved, moved.State);
        Assert.Contains($"0x{speed.Analysed:X}", moved.Detail);
        Assert.Contains($"0x{other.Value.Analysed:X}", moved.Detail);

        // The constants derived from each follow their entry.
        var speedField = NativeFieldChecks.All.First(f => f.Entry == "SchedulerTimeline.Speed");
        Assert.Equal(NativeHealthState.Ok, Row(report, "NativeFields." + speedField.Constant).State);
        var movedField = NativeFieldChecks.All.First(f => f.Entry == other.Key && f.Delta == 0);
        Assert.Equal(NativeHealthState.Moved, Row(report, "NativeFields." + movedField.Constant).State);
    }

    [Fact]
    public void A_patch_site_whose_bytes_changed_is_refused()
    {
        var report = Run(found: true);
        Assert.All(report.Rows.Where(r => r.Group == "Patch sites"),
            r => Assert.Equal(NativeHealthState.Missing, r.State));
        Assert.Equal(2, report.Rows.Count(r => r.Group == "Patch sites"));
    }

    [Fact]
    public void Nothing_found_reports_every_signed_entry_missing()
    {
        var report = Run(found: false);
        var signed = report.Rows.Where(r => r.Group is "Functions" or "Offsets" or "Data" or "Fields").ToList();
        Assert.NotEmpty(signed);
        Assert.All(signed, r => Assert.Equal(NativeHealthState.Missing, r.State));
        Assert.Equal(NativeRegistry.All.Length + NativeFieldChecks.All.Length,
            report.Rows.Count(r => r.Group != "Hooks") + NativeRegistry.All.Count(e => e.Kind == NativeKind.Hook));
    }

    [Fact]
    public void A_code_pattern_matches_with_wildcards()
    {
        byte* p = (byte*)_code;
        p[0] = 0x48; p[1] = 0x8B; p[2] = 0x05;
        Assert.True(NativeCode.Matches(_code, "48 ?? 05"));
        Assert.False(NativeCode.Matches(_code, "48 8B 06"));
    }
}
