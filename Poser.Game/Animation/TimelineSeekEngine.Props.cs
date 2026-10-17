using System;
using System.Collections.Generic;

namespace Poser.Game.Animation;

/// <summary>Prop (C031) timelines: found in the game's prop group, seeked, synced, re-fired, corrected at birth.</summary>
internal sealed unsafe partial class TimelineSeekEngine
{
    // ── Props (C031) ──────────────────────────────────────────────────

    /// <summary>The weapon object a C031 plays on: slot u16 data+0xE; 0–2 are the
    /// DrawData weapons (chara+0x710 + slot×0x70), 3 the summoned prop (+0x860).</summary>
    public static nint PropWeapon(nint character, nint c031)
    {
        nint data = *(nint*)(c031 + 0x48);
        int slot = *(ushort*)(data + 0xE);
        if (slot > 3)
            return 0;
        return slot < 3 ? *(nint*)(character + 0x710 + slot * 0x70) : *(nint*)(character + 0x860);
    }

    public static int PropTimelineId(nint c031) => *(ushort*)(*(nint*)(c031 + 0x48) + 0xC);

    /// <summary>The bucket-3 group keyed <c>W|2</c>, or 0.</summary>
    public nint PropGroup(nint weapon)
    {
        nint manager = _l.TimelineManager;
        if (weapon == 0 || manager == 0)
            return 0;
        int guard = 0;
        for (nint group = *(nint*)(manager + 0x30); group != 0 && guard < 256; group = *(nint*)(group + 0x10), guard++)
            if (*(ulong*)(group + 0x30) == ((ulong)weapon | 2))
                return group;
        return 0;
    }

    /// <summary>The group's CURRENT timeline: *(*(group+0xA48)), a refcounted
    /// handle node; the replaced one stays listed for a tick, so never "first".</summary>
    public static nint CurrentPropTimeline(nint group, int timelineId)
    {
        if (group == 0)
            return 0;
        nint handle = *(nint*)(group + 0xA48);
        nint timeline = handle != 0 ? *(nint*)handle : 0;
        return timeline != 0 && *(uint*)(timeline + 0x224) == (uint)(timelineId + 0x20000)
            && *(nint*)(timeline + 0x180) == handle ? timeline : 0;
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
            *(byte*)(control + 0x6B8) = 1;
        for (int i = 0; i < 32; i++)
        {
            float step = Math.Min(target - *(float*)(prop + 0x34), TimelineState.Of(prop).Length);
            if (step <= 0.001f)
                break;
            _n.UpdateBanner(prop, step, 1);
            if ((*(byte*)(prop + 0x272) & 0x80) != 0)
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
        for (nint instance = *(nint*)(control + 0x30); instance != 0 && guard < 16; instance = *(nint*)(instance + 0x10), guard++)
        {
            nint clip = *(nint*)(instance + 0x60);
            if (clip == 0 || synced.Contains(clip) || _l.Classify(clip) != SeekClipKind.HavokAnimation
                || *(nint*)(clip + 0x98) != instance)
                continue;
            // A prop clip that just started (the C031 re-fire) has no length yet;
            // resolve it so it is synced now, not a frame later at frame 0.
            if (*(nint*)(instance + 0x78) != 0 && (*(byte*)(clip + 0xCC) & 0x40) == 0)
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
        if (!Ready || (*(byte*)(character + 0x1CE8) & 0x10) != 0)
            return false;
        nint weapon = PropWeapon(character, c031);
        if (weapon == 0 || (*(byte*)(weapon + 0x89) & 0xF) != 3)
            return false;
        nint group = PropGroup(weapon);
        if (group != 0 && *(nint*)(group + 0xA48) is var handle and not 0 && *(nint*)handle != 0)
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
            *(byte*)(control + 0x6B8) = 1;
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
        int timelineId = (int)(*(uint*)(prop + 0x224) - 0x20000);
        foreach (int slot in new[] { 0, 1 })
        {
            nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
            if (root == 0)
                continue;
            foreach (var (clip, start) in Collect(root).Summons)
            {
                if (PropWeapon(character, clip) != weapon || PropTimelineId(clip) != timelineId)
                    continue;
                float err = (TimelineState.Of(root).Clock - start) - *(float*)(prop + 0x34);
                if (err <= 0.001f)
                    return true;
                if (err >= 2f)
                {
                    log = $"prop {timelineId} is {err:0.###} frames behind its body; left alone";
                    return true;
                }
                SeekProp(prop, TimelineState.Of(root).Clock - start, *(float*)(prop + 0x240), new HashSet<nint>());
                return true;
            }
        }
        return true; // not a summon this actor's slots play: nothing to correct
    }
}
