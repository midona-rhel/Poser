using System;
using Dalamud.Plugin.Services;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// The scheduler natives the timeline seek (#533) calls, resolved by
/// signature. Any missing entry leaves <see cref="Ready"/> false and the scrub
/// port keeps its clock-write fallback; nothing half-works.
/// </summary>
internal sealed unsafe class TimelineSeekNatives
{
    /// <summary>SchedulerTimeline.UpdateBanner(tl, frames, flag 1): one Advance by
    /// <c>frames × tl+0x240</c>, with activation, events and C010 children.</summary>
    public readonly delegate* unmanaged<nint, float, byte, void> UpdateBanner;
    /// <summary>TimelineController.SetCurrentTime(tl, frames): raw clock write
    /// (+0x34, prev +0x38, +0x64, recomputes +0x74), no clip events.</summary>
    public readonly delegate* unmanaged<nint, float, void> SetCurrentTime;
    /// <summary>TimelineController.ActivateClips(tl, mode); mode 4 = the start's
    /// frame-0 activation.</summary>
    public readonly delegate* unmanaged<nint, int, void> ActivateClips;
    /// <summary>TimelineController.ProcessPending(tl, event) → handled.</summary>
    public readonly delegate* unmanaged<nint, int, byte> ProcessPending;
    /// <summary>TimelineController.ProcessAll(tl, event, payload) → handled.</summary>
    public readonly delegate* unmanaged<nint, int, nint, byte> ProcessAll;
    /// <summary>SchedulerTimeline.GetActorControl(tl): first actor node's
    /// control or 0 (null-safe).</summary>
    public readonly delegate* unmanaged<nint, nint> GetActorControl;
    /// <summary>SchedulerTimeline.SetSpeed(tl, speed): change-guarded write of
    /// +0x240 that fires ProcessAll(0x1E) to the clips.</summary>
    public readonly delegate* unmanaged<nint, float, void> SetSpeed;
    /// <summary>HavokAnimationClip.SyncLocalTime(clip): control LocalTime from
    /// the owner clock × ROOT speed; call only inside a speed-1 window.</summary>
    public readonly delegate* unmanaged<nint, void> SyncLocalTime;
    /// <summary>SummonAnimationClip.StartPropTimeline(c031): plays the prop's
    /// ActionTimeline in the W|2 bucket-3 group; gives up while W is loading.</summary>
    public readonly delegate* unmanaged<nint, byte> StartPropTimeline;
    /// <summary>VfxObject.SetSpeed(vfx, speed): +0x250 and the Apricot wrapper.
    /// Its own entry has no guard; the C012 speed-changed handler drops 0.</summary>
    public readonly delegate* unmanaged<nint, float, void> SetVfxSpeed;
    /// <summary>FUN_14190E1E0(c009): resolves the clip's animation length into
    /// +0xB0 (and +0xC4/+0xC8, +0xCC |= 0x40); the game runs it in the clip's
    /// first post-step, one frame after its start.</summary>
    public readonly delegate* unmanaged<nint, void> ResolveHavokLength;
    /// <summary>FUN_140433DD0(characterBase, speed): the model's own VFX list
    /// (+0xB0, count +0x9C: IMC ve/vw effects such as weapon glows) to a speed. The
    /// game's GPose speed push covers only the character's own model, never its
    /// weapon objects.</summary>
    public readonly delegate* unmanaged<nint, float, void> SetModelVfxSpeed;
    /// <summary>FUN_1418FE4A0(tl, reason, 0): stops a timeline and its clips (the
    /// call a C010 Stop makes on its child).</summary>
    public readonly delegate* unmanaged<nint, int, nint, void> StopTimeline;

    // Pending a patchcheck entry; unique on the analysed build (sig.py, 1 hit at 0x1418FE4A0).
    private const string SchedulerTimeline_Stop =
        "40 53 55 57 48 83 EC 20 40 32 FF 48 89 74 24 40 4C 89 74 24 48 4D 8B F0 8B EA 48 8B D9 83 FA 09";

    // Pending a patchcheck entry; unique on the analysed build (sig.py, 1 hit at 0x140433DD0).
    private const string CharacterBase_SetModelVfxSpeed =
        "40 53 48 83 EC 30 8B 81 9C 00 00 00 48 8B 99 B0 00 00 00 0F 29 74 24 20 0F 28 F1";

    public readonly bool Ready;
    public readonly string? Missing;

    public TimelineSeekNatives(ISigScanner scanner, IPluginLog log)
    {
        string? missing = null;
        nint Scan(string signature, string name)
        {
            try
            {
                if (scanner.TryScanText(signature, out var address) && address != nint.Zero)
                    return address;
            }
            catch (Exception ex)
            {
                log.Warning($"Animation: {name} scan failed ({ex.Message}).");
            }
            missing ??= name;
            return nint.Zero;
        }

        UpdateBanner = (delegate* unmanaged<nint, float, byte, void>)Scan(
            NativeSignatures.SchedulerTimeline_UpdateBanner, "SchedulerTimeline.UpdateBanner");
        SetCurrentTime = (delegate* unmanaged<nint, float, void>)Scan(
            NativeSignatures.TimelineController_SetCurrentTime, "TimelineController.SetCurrentTime");
        ActivateClips = (delegate* unmanaged<nint, int, void>)Scan(
            NativeSignatures.TimelineController_ActivateClips, "TimelineController.ActivateClips");
        ProcessPending = (delegate* unmanaged<nint, int, byte>)Scan(
            NativeSignatures.TimelineController_ProcessPending, "TimelineController.ProcessPending");
        ProcessAll = (delegate* unmanaged<nint, int, nint, byte>)Scan(
            NativeSignatures.TimelineController_ProcessAll, "TimelineController.ProcessAll");
        GetActorControl = (delegate* unmanaged<nint, nint>)Scan(
            NativeSignatures.SchedulerTimeline_GetActorControl, "SchedulerTimeline.GetActorControl");
        SetSpeed = (delegate* unmanaged<nint, float, void>)Scan(
            NativeSignatures.SchedulerTimeline_SetSpeed, "SchedulerTimeline.SetSpeed");
        SyncLocalTime = (delegate* unmanaged<nint, void>)Scan(
            NativeSignatures.HavokAnimationClip_SyncLocalTime, "HavokAnimationClip.SyncLocalTime");
        StartPropTimeline = (delegate* unmanaged<nint, byte>)Scan(
            NativeSignatures.SummonAnimationClip_StartPropTimeline, "SummonAnimationClip.StartPropTimeline");

        SetVfxSpeed = (delegate* unmanaged<nint, float, void>)Scan(
            NativeSignatures.VfxObject_SetSpeed, "VfxObject.SetSpeed");

        ResolveHavokLength = (delegate* unmanaged<nint, void>)Scan(
            NativeSignatures.HavokAnimationClip_ResolveLength, "HavokAnimationClip.ResolveLength");

        SetModelVfxSpeed = (delegate* unmanaged<nint, float, void>)Scan(
            CharacterBase_SetModelVfxSpeed, "CharacterBase.SetModelVfxSpeed");

        StopTimeline = (delegate* unmanaged<nint, int, nint, void>)Scan(
            SchedulerTimeline_Stop, "SchedulerTimeline.Stop");

        Missing = missing;
        Ready = missing == null;
        if (!Ready)
            log.Warning($"Animation: native timeline seek unavailable ({missing} not found); scrub uses clock writes.");
    }
}
