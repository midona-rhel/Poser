using System;
using System.Threading;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Re-runs the game's KineDriver (.kdb bone drivers) for one posed skeleton, so
/// driven bones follow Poser's pose instead of the animation (#529).
/// KineDriver runs in frame task 23; Poser applies its pose in task 25, after it.
/// This repeats the game's own per-skeleton job (0x1418673D0 on build
/// 2026.10.06) once more, after the pose: lock, sync model space, connect
/// partials, update the module with dt 0 (no fade advance), unlock.
/// </summary>
internal sealed unsafe class KineDriverRerun
{
    // CharacterBase+0x160: the BoneKineDriverModule (null when no .kdb loaded
    // for this model). Its +0x08 is the Render.Skeleton it drives.
    private const int ModuleOffset = 0x160;
    private const int ModuleSkeletonOffset = 0x08;
    // Render.Skeleton+0xEC: the spin lock the game job takes (CAS 0 -> 1).
    private const int SkeletonLockOffset = 0xEC;

    private delegate void SyncAllModelSpaceFn(GameSkeleton* skeleton);
    private delegate void ConnectPartialsFromFn(GameSkeleton* skeleton, int partial);
    private delegate void UpdateModuleFn(nint module, float deltaTime);

    private readonly SyncAllModelSpaceFn? _syncAllModelSpace;
    private readonly ConnectPartialsFromFn? _connectPartialsFrom;
    private readonly UpdateModuleFn? _updateModule;

    public KineDriverRerun(IPluginLog log, ISigScanner scanner)
    {
        if (scanner.TryScanText("48 89 5C ?? ?? 56 48 83 EC 20 0F B7 41 50", out var sync)
            && scanner.TryScanText("4C 8B DC 56 41 54 41 57 48 81 EC 80 00 00 00", out var connect)
            && scanner.TryScanText("48 89 5C ?? ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 50 33 DB", out var update))
        {
            _syncAllModelSpace = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SyncAllModelSpaceFn>(sync);
            _connectPartialsFrom = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ConnectPartialsFromFn>(connect);
            _updateModule = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<UpdateModuleFn>(update);
        }
        else
        {
            log.Warning("KineDriver re-run unavailable: a signature was not found; driven bones keep following the animation.");
        }
    }

    public bool IsAvailable => _updateModule != null;

    /// <summary>Runs the skeleton's drivers once against the current pose.
    /// Returns whether they ran (no module, a module for another skeleton, or
    /// a busy lock skip this frame rather than wait).</summary>
    public bool Run(GameSkeleton* skeleton)
    {
        if (_updateModule == null || skeleton == null)
            return false;
        var owner = skeleton->Owner;
        if (owner == null)
            return false;
        var module = *(nint*)((byte*)owner + ModuleOffset);
        if (module == 0 || *(GameSkeleton**)(module + ModuleSkeletonOffset) != skeleton)
            return false;

        ref var spinLock = ref *(int*)((byte*)skeleton + SkeletonLockOffset);
        if (Interlocked.CompareExchange(ref spinLock, 1, 0) != 0)
            return false;
        try
        {
            _syncAllModelSpace!(skeleton);
            _connectPartialsFrom!(skeleton, 0);
            _updateModule(module, 0f);
        }
        finally
        {
            Volatile.Write(ref spinLock, 0);
        }
        return true;
    }
}
