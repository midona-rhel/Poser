using System;
using System.Collections.Generic;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>VFX speeds and the orphaned-instance guard.</summary>
internal sealed unsafe partial class TimelineSeekEngine
{
    // ── VFX speed ─────────────────────────────────────────────────────
    // The C012 speed-changed handler (slot 35) forwards only speeds > 0, so
    // neither Poser's pause nor a speed window's restore to 0 ever reaches the
    // effects: they kept simulating next to a frozen actor. Paused effects are
    // set directly, and set back explicitly on resume (a completed C012 whose
    // effect still lives, like the bubbles, gets no speed event at all).

    /// <summary>A C012's VfxObject: inst=clip+0x98 → *(inst+0x28) → vfunc 0x68.</summary>
    private static nint VfxOf(nint clip) => VfxOfInstance(*(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject));

    private static nint VfxOfInstance(nint instance)
    {
        nint holder = instance != 0 ? *(nint*)(instance + NativeFields.C012Instance_Holder) : 0;
        if (holder == 0)
            return 0;
        var getVfx = (delegate* unmanaged<nint, nint>)(*(nint*)(*(nint*)holder + NativeFields.C012Holder_VfGetVfx));
        return getVfx(holder);
    }

    /// <summary>Every VFX object of a tree: one per C012/C049, up to 8 per C173.</summary>
    private static List<nint> VfxObjects(Tree tree)
    {
        var objects = new List<nint>();
        foreach (var clip in tree.Vfx)
            if (VfxOf(clip) is var vfx and not 0)
                objects.Add(vfx);
        foreach (var clip in tree.AsyncVfx)
        {
            nint array = *(nint*)(clip + NativeFields.C173_Instances);
            nint data = *(nint*)(clip + NativeFields.SchedulerClip_Data);
            if (array == 0 || data == 0)
                continue;
            int count = Math.Clamp(*(int*)(data + NativeFields.C173Data_InstanceCount), 1, 8);
            for (int i = 0; i < count; i++)
                if (VfxOfInstance(*(nint*)(array + i * 8)) is var vfx and not 0)
                    objects.Add(vfx);
        }
        return objects;
    }

    /// <summary>Sets every live VFX object of a timeline tree to <paramref name="speed"/>.</summary>
    public void SetVfxSpeeds(nint root, float speed)
    {
        if (!Ready || root == 0)
            return;
        foreach (var vfx in VfxObjects(Collect(root)))
            if (*(float*)(vfx + NativeFields.VfxObject_Speed) != speed)
                _n.SetVfxSpeed(vfx, speed);
    }

    /// <summary>The weapon objects' own model VFX (glows, idle effects).</summary>
    public void SetWeaponVfxSpeeds(nint character, float speed)
    {
        if (!Ready)
            return;
        for (int w = 0; w < 4; w++)
            if (Weapon(character, w) is var weapon and not 0)
                _n.SetModelVfxSpeed(weapon, speed);
    }

    /// <summary>The actor's effects: slot timelines 0/1/2/3/6 and the live prop
    /// timelines of its four weapon objects.</summary>
    public void SetActorVfxSpeeds(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer,
        nint character, float speed)
    {
        if (!Ready)
            return;
        for (int slot = 0; slot < 14; slot++)
            SetVfxSpeeds(AnimationNativeState.SlotTimeline(sequencer, slot), speed);
        SetWeaponVfxSpeeds(character, speed);
        for (int w = 0; w < 4; w++)
        {
            nint group = PropGroup(Weapon(character, w));
            int guard = 0;
            for (nint timeline = group != 0 ? *(nint*)(group + NativeFields.TimelineGroup_Timelines) : 0;
                 timeline != 0 && guard < 64;
                 timeline = *(nint*)(timeline + NativeFields.SchedulerTimeline_GroupNext), guard++)
                if (!TimelineState.Of(timeline).Dead && *(sbyte*)(timeline + NativeFields.SchedulerTimeline_State) >= 0)
                    SetVfxSpeeds(timeline, speed);
        }
    }

    // ── VFX orphan guard ──────────────────────────────────────────────

    /// <summary>Handles of this tree's live C012 VFX objects
    /// (inst=clip+0x98 → *(inst+0x28) → vfunc 0x68 → vfx; wrapper vfx+0x2A0; handle +0x60).</summary>
    public HashSet<ulong> VfxHandles(Tree tree)
    {
        var handles = new HashSet<ulong>();
        foreach (var vfx in VfxObjects(tree))
        {
            nint wrapper = *(nint*)(vfx + NativeFields.VfxObject_Wrapper);
            ulong handle = wrapper != 0 ? *(ulong*)(wrapper + NativeFields.VfxResourceInstance_Handle) : 0;
            if (handle != 0)
                handles.Add(handle);
        }
        return handles;
    }

    /// <summary>Whether any of the tree's C012 effects still has a live engine
    /// instance (same generation, not released) in the Apricot slot table.</summary>
    private bool HasLiveVfx(Tree tree)
    {
        nint js = _l.VfxSlotTable;
        nint table = js != 0 ? *(nint*)(js + NativeFields.ApricotJobSystem_SlotTable) : 0;
        if (table == 0)
            return tree.Vfx.Count + tree.AsyncVfx.Count > 0;
        foreach (var handle in VfxHandles(tree))
        {
            uint index = (uint)(handle >> 32), generation = (uint)handle;
            if (index >= 0x800 || generation == 0)
                continue;
            nint entry = table + NativeFields.ApricotJobSystem_SlotArray + (nint)index * 0x88;
            if (*(uint*)(entry + NativeFields.ApricotJobSlot_Generation) == generation && *(ushort*)(entry + NativeFields.ApricotJobSlot_State) != 3 && *(nint*)(entry + NativeFields.ApricotJobSlot_Instance) != 0)
                return true;
        }
        return false;
    }

    /// <summary>Releases the handles this actor held before the seek that are
    /// still live (same generation, state != 3) but no longer held by any of its
    /// wrappers: engine instances a kill+respawn orphaned. Never touches anything
    /// outside <paramref name="before"/>.</summary>
    public int ReleaseOrphans(HashSet<ulong> before, HashSet<ulong> now)
    {
        nint js = _l.VfxSlotTable;
        nint table = js != 0 ? *(nint*)(js + NativeFields.ApricotJobSystem_SlotTable) : 0;
        if (table == 0)
            return 0;
        int released = 0;
        foreach (var handle in before)
        {
            if (now.Contains(handle))
                continue;
            uint index = (uint)(handle >> 32), generation = (uint)handle;
            if (index >= 0x800 || generation == 0)
                continue;
            nint entry = table + NativeFields.ApricotJobSystem_SlotArray + (nint)index * 0x88;
            if (*(uint*)(entry + NativeFields.ApricotJobSlot_Generation) != generation || *(ushort*)(entry + NativeFields.ApricotJobSlot_State) == 3 || *(nint*)(entry + NativeFields.ApricotJobSlot_Instance) == 0)
                continue;
            _l.ReleaseVfx(handle, 1);
            released++;
        }
        return released;
    }
}
