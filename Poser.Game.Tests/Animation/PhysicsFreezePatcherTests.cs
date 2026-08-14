using System.Reflection;
using Dalamud.Game;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// Drives every patch stage of <see cref="PhysicsFreezePatcher"/> through the
/// <see cref="IPhysicsPatchNative"/> seam: a fake memory window stands in for
/// the game's code pages, so startup validation, both rollback paths, foreign
/// -byte refusal, and the dispose restore are all exact byte assertions.
/// </summary>
public sealed class PhysicsFreezePatcherTests
{
    // The site layout the patcher validates at startup: region 1 is the
    // signature's own first instruction, region 2 sits 0x9 before it with a
    // pinned 0F 11 opcode and a free operand byte (0x77 here on purpose, to
    // prove the restore uses CAPTURED bytes, not hard-coded ones).
    private static readonly byte[] Region1 = [0x0F, 0x11, 0x48, 0x10];
    private static readonly byte[] Region2 = [0x0F, 0x11, 0x77];
    private static readonly byte[] Nops1 = [0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] Nops2 = [0x90, 0x90, 0x90];

    [Fact]
    public void Available_when_signature_and_both_regions_match()
    {
        var native = new FakeNative();

        var patcher = CreatePatcher(native, out _);

        Assert.True(patcher.IsAvailable);
        Assert.Null(patcher.UnavailableDetail);
        Assert.False(patcher.IsFrozen);
    }

    [Fact]
    public void Scan_miss_is_fail_closed()
    {
        var native = new FakeNative { ScanSucceeds = false };

        var patcher = CreatePatcher(native, out _);

        Assert.False(patcher.IsAvailable);
        Assert.Contains("signature", patcher.UnavailableDetail!, StringComparison.OrdinalIgnoreCase);
        var result = patcher.SetFrozen(true);
        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void Unexpected_region1_bytes_are_fail_closed()
    {
        var native = new FakeNative();
        native.WriteAt(FakeNative.Site, [0xCC, 0x11, 0x48, 0x10]);

        var patcher = CreatePatcher(native, out _);

        Assert.False(patcher.IsAvailable);
        Assert.Contains("unexpected", patcher.UnavailableDetail!, StringComparison.OrdinalIgnoreCase);
        Assert.False(patcher.SetFrozen(true).Success);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void Unexpected_region2_bytes_are_fail_closed()
    {
        var native = new FakeNative();
        native.WriteAt(FakeNative.Site - 0x9, [0x90, 0x11, 0x77]);

        var patcher = CreatePatcher(native, out _);

        Assert.False(patcher.IsAvailable);
        Assert.Contains("companion", patcher.UnavailableDetail!, StringComparison.OrdinalIgnoreCase);
        Assert.False(patcher.SetFrozen(true).Success);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void Native_read_fault_at_startup_is_fail_closed()
    {
        var native = new FakeNative { FailReads = true };

        var patcher = CreatePatcher(native, out _);

        Assert.False(patcher.IsAvailable);
        Assert.False(patcher.SetFrozen(true).Success);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void Freeze_nops_both_regions_and_unfreeze_restores_exact_startup_bytes()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out _);

        Assert.True(patcher.SetFrozen(true).Success);
        Assert.True(patcher.IsFrozen);
        Assert.Equal(Nops1, native.At(FakeNative.Site, 4));
        Assert.Equal(Nops2, native.At(FakeNative.Site - 0x9, 3));

        Assert.True(patcher.SetFrozen(false).Success);
        Assert.False(patcher.IsFrozen);
        Assert.Equal(Region1, native.At(FakeNative.Site, 4));
        // The captured operand byte (0x77) comes back exactly.
        Assert.Equal(Region2, native.At(FakeNative.Site - 0x9, 3));
    }

    [Fact]
    public void Redundant_set_is_a_no_op()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out _);

        Assert.True(patcher.SetFrozen(false).Success);
        Assert.Equal(0, native.Writes);
        Assert.True(patcher.SetFrozen(true).Success);
        Assert.True(patcher.SetFrozen(true).Success);
        Assert.Equal(2, native.Writes);
    }

    [Fact]
    public void Freeze_refuses_foreign_bytes_at_site()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out _);
        // Another tool wrote here after startup validation.
        native.WriteAt(FakeNative.Site, [0x0F, 0x11, 0x48, 0xEE]);

        var result = patcher.SetFrozen(true);

        Assert.False(result.Success);
        Assert.Contains("refusing to patch", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.False(patcher.IsFrozen);
        Assert.Equal(0, native.Writes);
    }

    [Fact]
    public void Unfreeze_refuses_foreign_bytes_while_frozen()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out _);
        Assert.True(patcher.SetFrozen(true).Success);
        // Another tool overwrote the live patch.
        native.WriteAt(FakeNative.Site - 0x9, [0xE9, 0x90, 0x90]);

        var result = patcher.SetFrozen(false);

        Assert.False(result.Success);
        Assert.Contains("refusing to restore", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(patcher.IsFrozen);
        Assert.Equal(2, native.Writes);
    }

    [Fact]
    public void Fault_on_second_freeze_write_rolls_back_the_first_region()
    {
        var native = new FakeNative { FailOnWriteNumber = 2 };
        var patcher = CreatePatcher(native, out _);

        var result = patcher.SetFrozen(true);

        Assert.False(result.Success);
        Assert.False(patcher.IsFrozen);
        // Fully original again: write 1 NOPed region 1, write 2 faulted,
        // write 3 rolled region 1 back.
        Assert.Equal(Region1, native.At(FakeNative.Site, 4));
        Assert.Equal(Region2, native.At(FakeNative.Site - 0x9, 3));
        Assert.Equal(3, native.Writes);
    }

    [Fact]
    public void Fault_on_second_restore_repatches_the_first_region()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out _);
        Assert.True(patcher.SetFrozen(true).Success);
        native.FailOnWriteNumber = 4;

        var result = patcher.SetFrozen(false);

        Assert.False(result.Success);
        // Fully frozen still, so IsFrozen == true stays truthful.
        Assert.True(patcher.IsFrozen);
        Assert.Equal(Nops1, native.At(FakeNative.Site, 4));
        Assert.Equal(Nops2, native.At(FakeNative.Site - 0x9, 3));
        Assert.Equal(5, native.Writes);
    }

    [Fact]
    public void Page_protection_is_restored_when_a_write_faults()
    {
        var native = new FakeNative { FailOnWriteNumber = 1 };
        var patcher = CreatePatcher(native, out _);

        Assert.False(patcher.SetFrozen(true).Success);

        Assert.Equal(2, native.PermissionChanges.Count);
        Assert.Equal(
            (FakeNative.Site, 4, MemoryProtection.ExecuteReadWrite),
            native.PermissionChanges[0]);
        // The finally put the previous protection back despite the fault.
        Assert.Equal(
            (FakeNative.Site, 4, FakeNative.PreviousProtection),
            native.PermissionChanges[1]);
    }

    [Fact]
    public void Dispose_restores_the_patch_and_is_idempotent()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out var log);
        Assert.True(patcher.SetFrozen(true).Success);

        patcher.Dispose();

        Assert.Equal(Region1, native.At(FakeNative.Site, 4));
        Assert.Equal(Region2, native.At(FakeNative.Site - 0x9, 3));
        Assert.Equal(4, native.Writes);
        Assert.Empty(log.Errors);

        patcher.Dispose();
        Assert.Equal(4, native.Writes);

        var afterDispose = patcher.SetFrozen(true);
        Assert.False(afterDispose.Success);
        Assert.Contains("disposed", afterDispose.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_with_nothing_patched_writes_nothing()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out var log);

        patcher.Dispose();

        Assert.Equal(0, native.Writes);
        Assert.Empty(log.Errors);
    }

    [Fact]
    public void Failed_unpatch_on_dispose_is_reported_as_an_error()
    {
        var native = new FakeNative();
        var patcher = CreatePatcher(native, out var log);
        Assert.True(patcher.SetFrozen(true).Success);
        native.FailOnWriteNumber = 4;

        patcher.Dispose();

        var error = Assert.Single(log.Errors);
        Assert.Contains("failed to restore", error, StringComparison.OrdinalIgnoreCase);
        // The rollback kept the site fully patched rather than half-restored.
        Assert.Equal(Nops1, native.At(FakeNative.Site, 4));
        Assert.Equal(Nops2, native.At(FakeNative.Site - 0x9, 3));
    }

    private static PhysicsFreezePatcher CreatePatcher(FakeNative native, out LogProxy log)
    {
        log = LogProxy.Create();
        return new PhysicsFreezePatcher(
            DispatchProxy.Create<ISigScanner, DefaultProxy>(), log.Log, native);
    }

    /// <summary>
    /// A 64-byte window standing in for the code pages around the freeze
    /// site. Reads and writes index into it; a 1-based write number can be
    /// made to fault, and permission changes are recorded verbatim.
    /// </summary>
    private sealed class FakeNative : IPhysicsPatchNative
    {
        public const nint Site = 0x2020;
        public const MemoryProtection PreviousProtection = MemoryProtection.ExecuteRead;
        private const nint Base = Site - 0x20;

        private readonly byte[] _memory = new byte[0x40];

        public bool ScanSucceeds { get; set; } = true;
        public bool FailReads { get; set; }
        public int FailOnWriteNumber { get; set; }
        public int Writes { get; private set; }
        public List<(nint Address, int Length, MemoryProtection NewProtection)> PermissionChanges { get; } = new();

        public FakeNative()
        {
            WriteAt(Site, Region1);
            WriteAt(Site - 0x9, Region2);
        }

        public void WriteAt(nint address, byte[] data) =>
            data.CopyTo(_memory, (int)(address - Base));

        public byte[] At(nint address, int length) =>
            _memory[(int)(address - Base)..((int)(address - Base) + length)];

        public bool TryScanFreezeSite(ISigScanner scanner, out nint address)
        {
            address = ScanSucceeds ? Site : 0;
            return ScanSucceeds;
        }

        public byte[] ReadRaw(nint address, int length) =>
            FailReads
                ? throw new InvalidOperationException("test read fault")
                : At(address, length);

        public void WriteRaw(nint address, byte[] data)
        {
            if (++Writes == FailOnWriteNumber)
                throw new InvalidOperationException("test write fault");
            WriteAt(address, data);
        }

        public MemoryProtection ChangePermission(
            nint address, int length, MemoryProtection newProtection)
        {
            PermissionChanges.Add((address, length, newProtection));
            return PreviousProtection;
        }
    }

    private class LogProxy : DispatchProxy
    {
        public IPluginLog Log { get; private set; } = null!;
        public List<string> Errors { get; } = new();

        public static LogProxy Create()
        {
            var log = DispatchProxy.Create<IPluginLog, LogProxy>();
            var proxy = (LogProxy)(object)log;
            proxy.Log = log;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Error" && args is [string message, ..])
                Errors.Add(message);
            return null;
        }
    }

    private class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.ReturnType is { IsValueType: true } type && type != typeof(void)
                ? Activator.CreateInstance(type)
                : null;
    }
}
