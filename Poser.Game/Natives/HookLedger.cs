using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Poser.Game.Natives;

/// <summary>
/// Every hook Poser creates: the plugin's one <see cref="IGameInteropProvider"/>,
/// forwarding to Dalamud's and recording each hook with its target, its delegate and
/// the code that made it, so the native check can say which hooks exist and run.
/// </summary>
public sealed class HookLedger : IGameInteropProvider
{
    internal sealed record Entry(string Delegate, nint Address, nint Requested, string CreatedBy, IDalamudHook? Hook, string? Failure);

    private readonly IGameInteropProvider _inner;
    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    public HookLedger(IGameInteropProvider inner) => _inner = inner;

    internal IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToArray();
        }
    }

    private Hook<T> Record<T>(nint address, Func<Hook<T>> create) where T : Delegate
    {
        string createdBy = Caller();
        try
        {
            var hook = create();
            lock (_lock)
                _entries.Add(new Entry(typeof(T).Name, hook.Address, address, createdBy, hook, null));
            return hook;
        }
        catch (Exception ex)
        {
            lock (_lock)
                _entries.Add(new Entry(typeof(T).Name, address, address, createdBy, null, ex.Message));
            throw;
        }
    }

    /// <summary>The Poser source that asked for the hook: its file, or its type
    /// without a symbol file.</summary>
    private static string Caller()
    {
        var frames = new StackTrace(2, fNeedFileInfo: true).GetFrames();
        foreach (var frame in frames)
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type == null || type == typeof(HookLedger))
                continue;
            if (frame.GetFileName() is { Length: > 0 } file)
            {
                int at = file.Replace('\\', '/').LastIndexOf("/Poser.Game/", StringComparison.Ordinal);
                return at >= 0 ? file.Replace('\\', '/')[(at + "/Poser.Game/".Length)..] : System.IO.Path.GetFileName(file);
            }
            while (type.DeclaringType != null && type.Name.StartsWith('<'))
                type = type.DeclaringType;
            return type.Name;
        }
        return "unknown";
    }

    public Hook<T> HookFromAddress<T>(nint procAddress, T detour,
        IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate =>
        Record(procAddress, () => _inner.HookFromAddress(procAddress, detour, backend));

    public Hook<T> HookFromAddress<T>(nuint procAddress, T detour,
        IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate =>
        Record((nint)procAddress, () => _inner.HookFromAddress(procAddress, detour, backend));

    public unsafe Hook<T> HookFromAddress<T>(void* procAddress, T detour,
        IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate =>
        Record((nint)procAddress, () => _inner.HookFromAddress((nint)procAddress, detour, backend));

    public Hook<T> HookFromSignature<T>(string signature, T detour,
        IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate =>
        Record(0, () => _inner.HookFromSignature(signature, detour, backend));

    public Hook<T> HookFromSymbol<T>(string moduleName, string exportName, T detour,
        IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate =>
        Record(0, () => _inner.HookFromSymbol(moduleName, exportName, detour, backend));

    public Hook<T> HookFromImport<T>(ProcessModule? module, string moduleName, string functionName, uint hintOrOrdinal,
        T detour) where T : Delegate =>
        Record(0, () => _inner.HookFromImport(module, moduleName, functionName, hintOrOrdinal, detour));

    public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate =>
        Record(address, () => _inner.HookFromFunctionPointerVariable(address, detour));

    // Hooks made from [Signature] attributes aren't recorded; Poser declares none.
    public void InitializeFromAttributes(object self) => _inner.InitializeFromAttributes(self);
}
