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

        Missing = missing;
        Ready = missing == null;
        if (!Ready)
            log.Warning($"Animation: native timeline seek unavailable ({missing} not found); scrub uses clock writes.");
    }
}
