#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool TraceReadMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    private static unsafe bool TraceRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        fixed (T* buffer = &value)
            return address != 0 && TraceReadMemory(-1, address, buffer, (nuint)sizeof(T), out var read)
                && read == (nuint)sizeof(T);
    }

    private StreamWriter? _lifetimeTrace;
    private DateTime _lifetimeTraceUntil;
    private nint _skeletonListAddress;
    private readonly Dictionary<nint, string> _lifetimeRows = new();
    private long _lifetimeFrame;

    private object ConfigureLifetimeTrace(Dictionary<string, string> query)
    {
        if (query.GetValueOrDefault("stop") == "1") StopLifetimeTrace();
        if (!query.TryGetValue("path", out var path))
            return new { active = _lifetimeTrace != null, frame = _lifetimeFrame };
        if (!Path.IsPathFullyQualified(path) || File.Exists(path))
            return new { error = "Choose a new absolute trace path." };
        StopLifetimeTrace();
        // Observed 7.56 native animation walker: RIP-relative list head,
        // Render::Skeleton next at +0x18, owner at +0xB8. Read-only RPM;
        // never invoke a virtual function through the suspect owner.
        var instruction = _idleScanner.ScanText("48 8B 1D ?? ?? ?? ?? 48 85 DB 0F 84 ?? ?? ?? ?? 48 89 7C 24 68");
        if (!TraceRead(instruction + 3, out int displacement))
            return new { error = "Cannot read native skeleton-list instruction." };
        _skeletonListAddress = instruction + 7 + displacement;
        _lifetimeRows.Clear();
        _lifetimeFrame = 0;
        _lifetimeTraceUntil = DateTime.UtcNow.AddSeconds(120);
        _lifetimeTrace = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _lifetimeTrace.WriteLine($"{DateTime.UtcNow:O} START head=0x{_skeletonListAddress:X}");
        _framework.Update += TraceLifetimeFrame;
        TraceLifetimeFrame(_framework);
        return new { active = true, path };
    }

    private void StopLifetimeTrace()
    {
        _framework.Update -= TraceLifetimeFrame;
        _lifetimeTrace?.WriteLine($"{DateTime.UtcNow:O} STOP");
        _lifetimeTrace?.Dispose();
        _lifetimeTrace = null;
    }

    private unsafe void TraceLifetimeFrame(IFramework framework)
    {
        if (_lifetimeTrace == null) return;
        if (DateTime.UtcNow >= _lifetimeTraceUntil) { StopLifetimeTrace(); return; }
        try
        {
            ++_lifetimeFrame;
            var current = new HashSet<nint>();
            TraceRead(_skeletonListAddress, out nint skeleton);
            while (skeleton != 0 && current.Count < 4096 && current.Add(skeleton))
            {
                TraceRead(skeleton + 0xB8, out nint owner);
                bool readable = TraceRead(owner, out ulong vtable);
                TraceRead(owner + 0x89, out byte kind);
                TraceRead((nint)(&((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)owner)->Skeleton), out nint back);
                string row = $"skeleton=0x{skeleton:X} owner=0x{owner:X} readable={readable} vt=0x{vtable:X} kind={kind & 15} back=0x{back:X}";
                if (!_lifetimeRows.TryGetValue(skeleton, out var previous) || previous != row)
                {
                    _lifetimeTrace.WriteLine($"{DateTime.UtcNow:O} frame={_lifetimeFrame} {row}");
                    _lifetimeRows[skeleton] = row;
                }
                if (!TraceRead(skeleton + 0x18, out skeleton)) break;
            }
            foreach (var old in new List<nint>(_lifetimeRows.Keys))
                if (!current.Contains(old))
                {
                    _lifetimeTrace.WriteLine($"{DateTime.UtcNow:O} frame={_lifetimeFrame} REMOVED {_lifetimeRows[old]}");
                    _lifetimeRows.Remove(old);
                }
            _lifetimeTrace.WriteLine($"{DateTime.UtcNow:O} frame={_lifetimeFrame} END nodes={current.Count} gpose={_gpose.IsGPosing} load={_scenes.Progress?.Phase}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Lifetime trace stopped: {ex.Message}");
            StopLifetimeTrace();
        }
    }
}
#endif
