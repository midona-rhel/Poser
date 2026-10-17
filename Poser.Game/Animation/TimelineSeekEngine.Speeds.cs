using System;
using System.Collections.Generic;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>Same-tick speed edges and layer weights on slots, props and holders.</summary>
internal sealed unsafe partial class TimelineSeekEngine
{
    // ── Same-tick speed edges ─────────────────────────────────────────
    // The game gives a speed change to the actor's prop timelines directly (the
    // container update's per-frame push into the W|2 groups) but to its slot
    // timelines only through seq+0x154 plus a dirty bit, one Advance later, and
    // Poser writes the Havok controls immediately: three ticks (speedtrace
    // 2026-10-16). Setting every timeline in the same tick lines them up; the
    // game's later pushes find identical values and are change-guarded no-ops.

    /// <summary>The prop weapon objects: DrawData weapons 0–2 and the summoned
    /// prop (which gets no per-frame push at all).</summary>
    private static nint Weapon(nint character, int index) =>
        index < 3 ? *(nint*)(character + NativeFields.Character_WeaponSlots + index * 0x70) : *(nint*)(character + NativeFields.Character_SummonedPropWeapon);

    /// <summary>The slot timeline takes exactly what the sequencer's consumer
    /// would push from seq+0x154 one Advance later.</summary>
    public void SetSlotTimelineSpeed(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, int slot, float speed)
    {
        if (!_n.Ready)
            return;
        nint timeline = AnimationNativeState.SlotTimeline(sequencer, slot);
        if (timeline != 0)
            _n.SetSpeed(timeline, speed);
    }

    /// <summary>A prop weapon's own Havok controls (W is its CharacterBase) at a
    /// speed. The game re-pushes the overall speed onto the prop TIMELINES at the end
    /// of every container update, after Poser's hook, so a layer's prop speed can
    /// only be held on its controls.</summary>
    public static void SetWeaponControlSpeeds(nint weapon, float speed)
    {
        if (weapon == 0)
            return;
        var weaponBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)weapon;
        var skeleton = weaponBase->Skeleton;
        if (skeleton == null)
            return;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
            for (int k = 0; k < 2; k++)
            {
                var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(k);
                if (animated == null)
                    continue;
                for (int c = 0; c < animated->AnimationControls.Length; c++)
                    if (animated->AnimationControls[c].Value is var control && control != null)
                        control->PlaybackSpeed = speed;
            }
    }

    /// <summary>Every live timeline in one prop weapon's W|2 group at a speed (the
    /// summoned prop's group, which the container update never pushes).</summary>
    public void SetWeaponTimelineSpeeds(nint weapon, float speed)
    {
        if (!Ready)
            return;
        nint group = PropGroup(weapon);
        int guard = 0;
        for (nint timeline = group != 0 ? *(nint*)(group + NativeFields.TimelineGroup_Timelines) : 0;
             timeline != 0 && guard < 64;
             timeline = *(nint*)(timeline + NativeFields.SchedulerTimeline_GroupNext), guard++)
            if (!TimelineState.Of(timeline).Dead && *(sbyte*)(timeline + NativeFields.SchedulerTimeline_State) >= 0)
                _n.SetSpeed(timeline, speed);
    }

    /// <summary>The C031's weapon slot (u16 data+0xE): 0–2 DrawData, 3 the summoned prop.</summary>
    public static int PropWeaponSlot(nint c031) => *(ushort*)(*(nint*)(c031 + NativeFields.SchedulerClip_Data) + NativeFields.C031Data_PropSlot);

    /// <summary>Every live timeline in the actor's W|2 prop groups takes the
    /// overall speed, walked like the game's TimelineGroup.SetSpeedAll: +0x18,
    /// next +0x88, skipping torn down (+0x273 bit 0) and detached (+0x10 sign).</summary>
    public void SetPropSpeeds(nint character, float overall)
    {
        if (!Ready)
            return;
        for (int w = 0; w < 4; w++)
        {
            nint group = PropGroup(Weapon(character, w));
            int guard = 0;
            for (nint timeline = group != 0 ? *(nint*)(group + NativeFields.TimelineGroup_Timelines) : 0;
                 timeline != 0 && guard < 64;
                 timeline = *(nint*)(timeline + NativeFields.SchedulerTimeline_GroupNext), guard++)
            {
                if (TimelineState.Of(timeline).Dead || *(sbyte*)(timeline + NativeFields.SchedulerTimeline_State) < 0)
                    continue;
                _n.SetSpeed(timeline, overall);
            }
        }
    }

    // ── Layer weight ──────────────────────────────────────────────────
    // The game has no per-slot weight. Each C009's animation holder
    // (inst = clip+0x98, holder = inst+0x78) multiplies its blend-tree weight by
    // holder+0x5C every pool update (FUN_1418FB860 → SetWeight, clamped to
    // [0, 1]); only holder init writes it (1.0). Holding it per slot never fights
    // the game; new holders (a restart, a new clip) need it again, so it is
    // re-applied every tick.

    /// <summary>Sets every holder of the slot timeline's tree to <paramref name="weight"/>.</summary>
    public void SetSlotWeight(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, nint character, int slot, float weight,
        IReadOnlySet<nint>? suppressed = null)
    {
        if (!Ready)
            return;
        nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
        if (root == 0)
            return;
        var tree = Collect(root);
        foreach (var clip in tree.Havok.Keys)
            SetHolderWeight(*(nint*)(clip + NativeFields.HavokAnimationClip_AnimObject), weight);
        // Props the layer summons (C031) run on their own bucket-3 timelines; their
        // C009 holders hang off the prop's actor control instance list. A prop held
        // invisible because a higher layer owns its weapon stays at 0.
        foreach (var (clip, _) in tree.Summons)
        {
            nint weapon = PropWeapon(character, clip);
            nint prop = CurrentPropTimeline(PropGroup(weapon), PropTimelineId(clip));
            if (prop != 0 && (suppressed == null || !suppressed.Contains(prop)))
                SetPropTimelineWeight(prop, weight);
        }
    }

    /// <summary>Every C009 holder of one prop timeline at a weight.</summary>
    public void SetPropTimelineWeight(nint prop, float weight)
    {
        nint control = prop != 0 ? PropControl(prop) : 0;
        int guard = 0;
        for (nint instance = control != 0 ? *(nint*)(control + NativeFields.ActorControl_Instances) : 0; instance != 0 && guard < 16;
             instance = *(nint*)(instance + NativeFields.ClipAnimation_Next), guard++)
            SetHolderWeight(instance, weight);
    }

    /// <summary>The group's current prop timeline whatever its id, or 0.</summary>
    public static nint CurrentPropTimelineAny(nint group)
    {
        if (group == 0)
            return 0;
        nint handle = *(nint*)(group + NativeFields.TimelineGroup_CurrentHandle);
        nint timeline = handle != 0 ? *(nint*)handle : 0;
        return timeline != 0 && *(nint*)(timeline + NativeFields.SchedulerTimeline_SelfHandle) == handle ? timeline : 0;
    }

    /// <summary>The ActionTimeline id a prop timeline plays (+0x224 − 0x20000).</summary>
    public static int PropTimelineIdOf(nint prop) => (int)(*(uint*)(prop + NativeFields.SchedulerTimeline_ActionKey) - NativeFields.SchedulerTimeline_ActionKeyBias);

    /// <summary>Fires a C031 over whatever prop timeline its weapon has (the start
    /// replaces it, as a natural fire does); needs the weapon loaded.</summary>
    public bool ReFireProp(nint character, nint c031)
    {
        if (!Ready || (*(byte*)(character + NativeFields.Character_TimelineFlags1CE8) & 0x10) != 0)
            return false;
        nint weapon = PropWeapon(character, c031);
        if (weapon == 0 || (*(byte*)(weapon + NativeFields.DrawObject_LoadState) & 0xF) != 3)
            return false;
        _n.StartPropTimeline(c031);
        return true;
    }

    private static void SetHolderWeight(nint instance, float weight)
    {
        nint holder = instance != 0 ? *(nint*)(instance + NativeFields.ClipAnimation_Holder) : 0;
        if (holder != 0 && *(float*)(holder + NativeFields.AnimationHolder_WeightMultiplier) != weight)
            *(float*)(holder + NativeFields.AnimationHolder_WeightMultiplier) = weight;
    }
}
