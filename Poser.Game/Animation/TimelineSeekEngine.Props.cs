using System;
using System.Collections.Generic;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>Prop (C031) timelines: found in the game's prop group, seeked, synced, re-fired, corrected at birth.</summary>
internal sealed unsafe partial class TimelineSeekEngine
{
    // ── Props (C031) ──────────────────────────────────────────────────

    /// <summary>The weapon object a C031 plays on: slot u16 data+0xE; 0–2 are the
    /// DrawData weapons (chara+0x710 + slot×0x70), 3 the summoned prop (+0x860).</summary>
    public static nint PropWeapon(nint character, nint c031)
    {
        nint data = *(nint*)(c031 + NativeFields.SchedulerClip_Data);
        int slot = *(ushort*)(data + NativeFields.C031Data_PropSlot);
        if (slot > 3)
            return 0;
        return slot < 3 ? *(nint*)(character + NativeFields.Character_WeaponSlots + slot * 0x70) : *(nint*)(character + NativeFields.Character_SummonedPropWeapon);
    }

    public static int PropTimelineId(nint c031) => *(ushort*)(*(nint*)(c031 + NativeFields.SchedulerClip_Data) + NativeFields.C031Data_PropTimelineId);

    /// <summary>The bucket-3 group keyed <c>W|2</c>, or 0.</summary>
    public nint PropGroup(nint weapon)
    {
        nint manager = _l.TimelineManager;
        if (weapon == 0 || manager == 0)
            return 0;
        int guard = 0;
        for (nint group = *(nint*)(manager + NativeFields.ActionTimelineManager_Bucket3); group != 0 && guard < 256; group = *(nint*)(group + NativeFields.TimelineGroup_Next), guard++)
            // The group key struct sits at TimelineGroup.Key; the W|2 word is its second u64.
            if (*(ulong*)(group + NativeFields.TimelineGroup_KeyTagged) == ((ulong)weapon | 2))
                return group;
        return 0;
    }

    /// <summary>The group's CURRENT timeline: *(*(group+0xA48)), a refcounted
    /// handle node; the replaced one stays listed for a tick, so never "first".</summary>
    public static nint CurrentPropTimeline(nint group, int timelineId)
    {
        if (group == 0)
            return 0;
        nint handle = *(nint*)(group + NativeFields.TimelineGroup_CurrentHandle);
        nint timeline = handle != 0 ? *(nint*)handle : 0;
        return timeline != 0 && *(uint*)(timeline + NativeFields.SchedulerTimeline_ActionKey) == (uint)(timelineId + NativeFields.SchedulerTimeline_ActionKeyBias)
            && *(nint*)(timeline + NativeFields.SchedulerTimeline_SelfHandle) == handle ? timeline : 0;
    }

    /// <summary>Steps a prop timeline forward to <paramref name="target"/> in its
    /// own speed window and syncs its C009s; leaves it at
    /// <paramref name="ownerSpeed"/> (birth speed / per-frame push value).</summary>
    public void SeekProp(nint prop, float target, float ownerSpeed, HashSet<nint> synced)
    {
        OpenWindow(prop);
        nint control = _n.GetActorControl(prop);
        bool validControl = control != 0 && _l.IsActorControl(control);
        if (validControl)
            *(byte*)(control + NativeFields.ActorControl_NoBlendStart) = 1;
        for (int i = 0; i < 32; i++)
        {
            float step = Math.Min(target - *(float*)(prop + NativeFields.TimelineController_CurrentTimestamp), TimelineState.Of(prop).Length);
            if (step <= 0.001f)
                break;
            _n.UpdateBanner(prop, step, 1);
            if ((*(byte*)(prop + NativeFields.SchedulerTimeline_Flags272) & 0x80) != 0)
                break;
        }
        if (validControl)
            SyncPropControls(prop, control, synced);
        CloseWindow(prop, ownerSpeed);
    }

    /// <summary>The prop's C009s: actor-control instance list (+0x30, next
    /// +0x10), clip at inst+0x60; each synced once (same guard as the body).</summary>
    public int SyncPropControls(nint prop, nint control, HashSet<nint> synced)
    {
        int count = 0, guard = 0;
        for (nint instance = *(nint*)(control + NativeFields.ActorControl_Instances); instance != 0 && guard < 16; instance = *(nint*)(instance + NativeFields.ClipAnimation_Next), guard++)
        {
            nint clip = *(nint*)(instance + NativeFields.ClipAnimation_Clip);
            if (clip == 0 || synced.Contains(clip) || _l.Classify(clip) != SeekClipKind.HavokAnimation
                || *(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject) != instance)
                continue;
            // A prop clip that just started (the C031 re-fire) has no length yet;
            // resolve it so it is synced now, not a frame later at frame 0.
            if (*(nint*)(instance + NativeFields.ClipAnimation_Holder) != 0 && (*(byte*)(clip + NativeFields.HavokAnimationClip_Flags) & 0x40) == 0)
                _n.ResolveHavokLength(clip);
            if (!CanSyncNow(clip))
                continue;
            float speed = OpenWindow(prop);
            SyncNoEpsilon(clip);
            CloseWindow(prop, speed);
            synced.Add(clip);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Re-fires a C031 whose start gave up (after a redraw the C031 activates
    /// while the new weapon object is still loading, and FUN_141909F80 drops it):
    /// only once W is ready (+0x89 &amp; 0xF == 3), only when its group has no
    /// current timeline (never replace a live one), never for scheduler-driven
    /// actors (+0x1CE8 &amp; 0x10 takes an unchecked path). The new timeline starts
    /// at 0 for non-portrait actors; the caller seeks it.
    /// </summary>
    public bool FireProp(nint character, nint c031)
    {
        if (!Ready || (*(byte*)(character + NativeFields.Character_TimelineFlags1CE8) & 0x10) != 0)
            return false;
        nint weapon = PropWeapon(character, c031);
        if (weapon == 0 || (*(byte*)(weapon + NativeFields.DrawObject_LoadState) & 0xF) != 3)
            return false;
        nint group = PropGroup(weapon);
        if (group != 0 && *(nint*)(group + NativeFields.TimelineGroup_CurrentHandle) is var handle and not 0 && *(nint*)handle != 0)
            return false;
        _n.StartPropTimeline(c031);
        return true;
    }

    /// <summary>Starts the next clips on this timeline's actor control without a
    /// blend-in (+0x6B8 = 1; the game clears it after the next start).</summary>
    public void SetNoBlendStart(nint timeline)
    {
        nint control = _n.GetActorControl(timeline);
        if (control != 0 && _l.IsActorControl(control))
            *(byte*)(control + NativeFields.ActorControl_NoBlendStart) = 1;
    }

    public nint PropControl(nint prop)
    {
        nint control = _n.GetActorControl(prop);
        return control != 0 && _l.IsActorControl(control) ? control : 0;
    }

    // ── Prop birth correction ─────────────────────────────────────────
    // A C031 fires in the step where the body crosses its start, so the prop
    // timeline is born at clock 0 while the body is already a fraction past it
    // (0.36–0.45 frames measured). The game corrects this only for portrait
    // actors (the elapsed offset gated on chara+0x190 & 8). One forward step on
    // the new timeline gives every actor what portraits get.

    public static nint WeaponAt(nint character, int index) => Weapon(character, index);

    /// <summary>
    /// Corrects a newly current prop timeline of <paramref name="weapon"/>.
    /// Returns false while it can't be decided yet (not loaded, no C031 found),
    /// so the caller retries next tick; true once handled either way.
    /// </summary>
    public bool CorrectPropBirth(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer,
        nint character, nint weapon, nint prop, out string? log)
    {
        log = null;
        if (!TimelineState.Of(prop).Loaded)
            return false;
        int timelineId = (int)(*(uint*)(prop + NativeFields.SchedulerTimeline_ActionKey) - NativeFields.SchedulerTimeline_ActionKeyBias);
        foreach (int slot in new[] { 0, 1 })
        {
            nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
            if (root == 0)
                continue;
            foreach (var (clip, start) in Collect(root).Summons)
            {
                if (PropWeapon(character, clip) != weapon || PropTimelineId(clip) != timelineId)
                    continue;
                float err = (TimelineState.Of(root).Clock - start) - *(float*)(prop + NativeFields.TimelineController_CurrentTimestamp);
                if (err <= 0.001f)
                    return true;
                if (err >= 2f)
                {
                    log = $"prop {timelineId} is {err:0.###} frames behind its body; left alone";
                    return true;
                }
                SeekProp(prop, TimelineState.Of(root).Clock - start, *(float*)(prop + NativeFields.SchedulerTimeline_Speed), new HashSet<nint>());
                return true;
            }
        }
        return true; // not a summon this actor's slots play: nothing to correct
    }
}
