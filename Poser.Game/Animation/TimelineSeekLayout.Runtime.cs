using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// Build identities for the timeline seek (#533), all resolved from game code:
/// clip classes by the type id at clip+0x84, looked up BY TAG NAME in the
/// scheduler's clip type table (survives renumbering and moved vtables);
/// vtables only for the two classes with no type field; the default event
/// payload, ActionTimelineManager and the Apricot job system by their
/// RIP-relative uses. Every hard-coded field offset the seek relies on is
/// checked against its resolved value; any mismatch disables the seek.
/// </summary>
internal sealed unsafe class TimelineSeekLayout : ITimelineSeekLayout
{
    // Type table rows (FUN_1418C5330 writes clip+0x84 = the row's id):
    // 216 rows × 0x30 — +0x00 u32 id, +0x10 char* tag ("C009").
    private const int TypeRows = 0xD8, TypeRowSize = 0x30;

    private readonly int _c009 = -1, _c010 = -1, _c012 = -1, _c049 = -1, _c031 = -1, _c173 = -1;
    private readonly nint _childTimelineVtbl, _actorControlVtbl, _timelineManagerPtr, _jobSystem, _scheduleManagementPtr;
    private readonly delegate* unmanaged<ulong, byte, void> _release;

    public TimelineSeekLayout(ISigScanner scanner, IPluginLog log)
    {
        string? missing = null;
        nint Data(DataPattern pattern)
        {
            if (NativeData.TryResolve(scanner, pattern, out var address) && address != 0)
                return address;
            missing ??= pattern.Name;
            return 0;
        }

        nint typeTable = Data(NativeData.SchedulerClip_TypeTable);
        _timelineManagerPtr = Data(NativeData.ActionTimelineManager_InstancePtr);
        DefaultPayload = Data(NativeData.Scheduler_DefaultEventPayload);
        _jobSystem = Data(NativeData.ApricotJobSystem_Instance);
        _actorControlVtbl = Data(NativeData.SchedulerActorControl_Vtbl);
        _childTimelineVtbl = Data(NativeData.ChildTimeline_Vtbl);
        _scheduleManagementPtr = Data(NativeData.ScheduleManagement_InstancePtr);
        if (scanner.TryScanText(NativeSignatures.ApricotJobSystem_ReleaseInstance, out var release) && release != 0)
            _release = (delegate* unmanaged<ulong, byte, void>)release;
        else
            missing ??= "ApricotJobSystem.ReleaseInstance";

        if (typeTable != 0)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int row = 0; row < TypeRows; row++)
            {
                byte* entry = (byte*)typeTable + row * TypeRowSize;
                var tag = *(nint*)(entry + 0x10);
                if (tag == 0)
                    continue;
                ids.TryAdd(new string((sbyte*)tag, 0, 4), *(int*)entry);
            }
            int Id(string tag)
            {
                if (ids.TryGetValue(tag, out var id))
                    return id;
                missing ??= $"clip type {tag}";
                return -1;
            }
            _c009 = Id("C009");
            _c010 = Id("C010");
            _c012 = Id("C012");
            _c031 = Id("C031");
            _c049 = Id("C049");
            _c173 = Id("C173");
        }

        // The seek's hard-coded field offsets, each against the game's own use.
        void Offset(OffsetPattern pattern, int expected)
        {
            if (!NativeOffsets.TryResolve(scanner, pattern, out var actual))
                missing ??= pattern.Name;
            else if (actual != expected)
                missing ??= $"{pattern.Name} moved (0x{actual:X} != 0x{expected:X})";
        }
        Offset(NativeOffsets.SchedulerClip_TypeId, 0x84);
        Offset(NativeOffsets.SchedulerTimeline_Speed, 0x240);
        Offset(NativeOffsets.SchedulerState_PauseMask, 0x54);
        Offset(NativeOffsets.TimelineController_CurrentTimestamp, 0x34);
        Offset(NativeOffsets.TimelineGroup_CurrentHandle, 0xA48);
        Offset(NativeOffsets.SchedulerTimeline_ActionKey, 0x224);
        Offset(NativeOffsets.SchedulerTimeline_SelfHandle, 0x180);
        Offset(NativeOffsets.SchedulerTimeline_GroupNext, 0x88);
        Offset(NativeOffsets.Character_WeaponSlots, 0x710);
        Offset(NativeOffsets.Character_SummonedPropWeapon, 0x860);
        Offset(NativeOffsets.VfxObject_Wrapper, 0x2A0);
        Offset(NativeOffsets.VfxClip_Instance, 0x98);
        Offset(NativeOffsets.HavokAnimationClip_AnimObject, 0x98);
        Offset(NativeOffsets.ApricotJobSystem_SlotTable, 0x1498);
        Offset(NativeOffsets.ScheduleManagement_FrameDelta, 0x50);
        Offset(NativeOffsets.SchedulerTimeline_StopFadeFrames, 0x220);
        // +0x6B8 (no-blend start) has no unique reader; it's the u32 at byte 2 of
        // ActorControl.SetNoBlendStart (88 91 B8 06 00 00).
        if (!scanner.TryScanText(NativeSignatures.ActorControl_SetNoBlendStart, out var noBlend) || noBlend == 0)
            missing ??= "ActorControl.SetNoBlendStart";
        else if (*(int*)(noBlend + 2) != 0x6B8)
            missing ??= $"ActorControl.NoBlendStart moved (0x{*(int*)(noBlend + 2):X})";

        Missing = missing;
        if (missing != null)
            log.Warning($"Animation: native timeline seek layout unavailable ({missing}); scrub uses clock writes.");
    }

    public bool Ready => Missing == null;
    public string? Missing { get; }

    public SeekClipKind Classify(nint clip)
    {
        int type = *(int*)(clip + 0x84);
        if (type == _c010) return SeekClipKind.ChildTimeline;
        if (type == _c009) return SeekClipKind.HavokAnimation;
        if (type == _c012 || type == _c049) return SeekClipKind.Vfx;
        if (type == _c173) return SeekClipKind.AsyncVfx;
        if (type == _c031) return SeekClipKind.SummonAnimation;
        return SeekClipKind.Other;
    }

    public bool IsChildTimeline(nint timeline) => *(nint*)timeline == _childTimelineVtbl;
    public bool IsActorControl(nint control) => *(nint*)control == _actorControlVtbl;
    public nint DefaultPayload { get; }
    public nint TimelineManager => _timelineManagerPtr != 0 ? *(nint*)_timelineManagerPtr : 0;
    public nint VfxSlotTable => _jobSystem;
    public nint ScheduleManagement => _scheduleManagementPtr != 0 ? *(nint*)_scheduleManagementPtr : 0;
    public delegate* unmanaged<ulong, byte, void> ReleaseVfx => _release;
}
