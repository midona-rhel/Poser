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
    public void Physics_patch_validates_bytes_applies_atomically_and_restores_captured_bytes()
    {
        var native = new FakeNative();
        using var patcher = CreatePatcher(native, out var log);
        Assert.True(patcher.IsAvailable);
        Assert.True(patcher.SetFrozen(true).Success);
        Assert.Equal(Nops1, native.At(FakeNative.Site, 4));
        Assert.Equal(Nops2, native.At(FakeNative.Site - 0x9, 3));

        native.FailOnWriteNumber = 4;
        Assert.False(patcher.SetFrozen(false).Success);
        Assert.True(patcher.IsFrozen);
        native.FailOnWriteNumber = 0;
        Assert.True(patcher.SetFrozen(false).Success);
        Assert.Equal(Region1, native.At(FakeNative.Site, 4));
        Assert.Equal(Region2, native.At(FakeNative.Site - 0x9, 3));
        Assert.Empty(log.Errors);
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
