using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Natives;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Animation;

/// <summary>Props: the highest layer's prop wins, lower layers' fades leave it alone, layer speeds reach their props, newly born props are corrected.</summary>
public sealed unsafe partial class TimelineSeekService
{
    /// <summary>
    /// The per-frame push of the overall speed into a prop weapon's group: a prop
    /// summoned by a layer with its own speed gets overall × that layer's speed, so
    /// its timeline (its clips, effects and length) runs on its layer's time.
    /// Everything else passes through. Never throws into the game.
    /// </summary>
    private void WeaponSpeedDetour(nint manager, nint weapon, float speed)
    {
        try
        {
            var map = _layerPropSpeeds;
            if (weapon != 0 && map.TryGetValue(weapon, out var held) && held.Tick == _tick)
                speed *= held.Speed;
        }
        catch
        {
            // A lookup failure only loses the layer factor for this frame.
        }
        _weaponSpeedHook!.Original(manager, weapon, speed);
    }

    // ── Prop priority ─────────────────────────────────────────────────
    // One prop model per request group: every C198 Start writes its model into
    // the cell unconditionally, so the most recent request won. The highest
    // layer's prop wins instead (Upper Body over Full body).

    /// <summary>The model the highest layer with a started, active C198 asks for,
    /// per actor control and group.</summary>
    private void UpdateLayerProps()
    {
        if (_applyRequestsHook == null)
            return;
        var next = new Dictionary<nint, (ulong, ulong)>();
        var suppressed = new HashSet<nint>();
        var liveFades = new HashSet<nint>();
        var fadeOwners = new Dictionary<nint, (nint[] Roots, int Owner0)>();
        foreach (nint character in _managed)
        {
            var sequencer = &((Character*)character)->Timeline.TimelineSequencer;
            var roots = new nint[14];
            for (int slot = 0; slot < 14; slot++)
                roots[slot] = AnimationNativeState.SlotTimeline(sequencer, slot);
            nint control = 0;
            ulong group0 = 0, group1 = 0;
            int owner0 = -1, owner1 = -1;
            var summons = new List<(int Slot, nint Clip, int WeaponSlot)>();
            var requests = new List<(int Slot, nint Clip, int Group)>();
            var fades = new List<(int Slot, nint Clip)>();
            for (int slot = 0; slot < 14; slot++)
            {
                nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
                if (root == 0)
                    continue;
                if (control == 0)
                    control = _engine.PropControl(root);
                var tree = _engine.Collect(root);
                foreach (var clip in tree.WeaponRequests)
                {
                    // Started (+0x88 bit 4) and still active (+0x10 bit 4).
                    if ((*(byte*)(clip + NativeFields.SchedulerClip_Flags88) & 0x10) == 0 || (*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x10) == 0)
                        continue;
                    nint data = *(nint*)(clip + NativeFields.SchedulerClip_Data);
                    int group = *(ushort*)(data + NativeFields.C198Data_Group);
                    ulong model = *(ulong*)(data + NativeFields.C198Data_Model);
                    // Slots ascend, so a later (higher) layer overrides.
                    if (group == 0) { group0 = model; owner0 = slot; }
                    else if (group == 1) { group1 = model; owner1 = slot; }
                    requests.Add((slot, clip, group));
                }
                // A C043 Summon Weapon of type 8 asks for a group-0 prop the same way.
                foreach (var clip in tree.SummonWeapons)
                {
                    if ((*(byte*)(clip + NativeFields.SchedulerClip_Flags88) & 0x10) == 0 || (*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x10) == 0)
                        continue;
                    nint data = *(nint*)(clip + NativeFields.SchedulerClip_Data);
                    if (*(int*)(data + NativeFields.C043Data_RequestType) != 8)
                        continue;
                    group0 = *(ulong*)(data + NativeFields.C043Data_Model);
                    owner0 = slot;
                }
                foreach (var clip in tree.Invisibilities)
                    fades.Add((slot, clip));
                foreach (var (clip, _) in tree.Summons)
                    if ((*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x10) != 0)
                        summons.Add((slot, clip, TimelineSeekEngine.PropWeaponSlot(clip)));
            }
            if (control != 0 && (group0 != 0 || group1 != 0))
                next[control] = (group0, group1);
            ArbitratePropAnimations(character, summons, owner0, owner1, suppressed);
            // A lower layer's C198 whose prop a higher layer replaced would see its
            // model leave the actor and stop itself for good (self-stop, §7.7): the
            // prop was gone once the higher layer ended. Re-armed each tick.
            foreach (var (slot, clip, group) in requests)
                if (slot < (group == 0 ? owner0 : owner1))
                    *(byte*)(clip + NativeFields.C198_ModelSeen) = 0;
            MaskLowerFades(character, fades, owner0, liveFades);
            fadeOwners[character] = (roots, owner0);
        }
        _fadeOwners = fadeOwners;
        _layerProps = next;
        // A lower layer's C094 gets its prop-slot bit back once no higher layer owns
        // the prop (only clips still in a live tree are touched).
        foreach (var clip in new List<nint>(_maskedFades.Keys))
        {
            if (_maskWanted.Contains(clip))
                continue;
            if (liveFades.Contains(clip))
                *(byte*)(clip + NativeFields.C094_SlotMask) |= _maskedFades[clip];
            _maskedFades.Remove(clip);
        }
        _maskWanted.Clear();
        // A prop no longer held down gets its holders back (the layer weight, if
        // any, is re-applied on top every tick). Last tick's prop may be gone: only
        // one still current in a managed actor's prop group is touched.
        if (_suppressedProps.Count > 0)
        {
            var live = LivePropTimelines();
            foreach (nint prop in _suppressedProps)
                if (!suppressed.Contains(prop) && live.Contains(prop))
                    _engine.SetPropTimelineWeight(prop, 1f);
        }
        _suppressedProps = suppressed;
    }

    /// <summary>Every managed actor's current, live prop timelines this tick.</summary>
    private HashSet<nint> LivePropTimelines()
    {
        var live = new HashSet<nint>();
        foreach (nint character in _managed)
            for (int w = 0; w < 4; w++)
            {
                nint prop = TimelineSeekEngine.CurrentPropTimelineAny(
                    _engine.PropGroup(TimelineSeekEngine.WeaponAt(character, w)));
                if (prop != 0 && !TimelineState.Of(prop).TornDown)
                    live.Add(prop);
            }
        return live;
    }

    // C094s whose DrawData prop-slot bit (bit 3: slot 2, the group-0 prop) Poser
    // cleared, with the bits taken; and the ones still wanted cleared this tick.
    private readonly Dictionary<nint, byte> _maskedFades = new();
    private readonly HashSet<nint> _maskWanted = new();

    /// <summary>
    /// A lower layer's C094 fades the prop slot it shares with a higher layer's
    /// prop (each C094 multiplies into the slot's alpha): Toast's fade-in hid the
    /// apple. While a higher layer owns the group-0 prop, the lower layers' C094s
    /// lose their slot-2 bit (clip+0xD3 bit 3) and the slot's alpha goes back to 1
    /// once (DrawData slot 2 +0x68, §7.8).
    /// </summary>
    private void MaskLowerFades(nint character, List<(int Slot, nint Clip)> fades, int owner0, HashSet<nint> live)
    {
        const byte PropSlotBit = 1 << 3;
        foreach (var (slot, clip) in fades)
        {
            live.Add(clip);
            if (owner0 < 0 || slot >= owner0)
                continue;
            _maskWanted.Add(clip);
            if ((*(byte*)(clip + NativeFields.C094_SlotMask) & PropSlotBit) == 0)
                continue;
            *(byte*)(clip + NativeFields.C094_SlotMask) &= unchecked((byte)~PropSlotBit);
            _maskedFades[clip] = PropSlotBit;
            // FUN_1408B0B00(ddc = &chara->DrawData, slot, alpha): slot 2's alpha at
            // ddc + 2·0x70 + 0x68. Stored, not multiplied: a higher layer's own
            // active C094 writes over it the same tick.
            *(float*)((byte*)&((Character*)character)->DrawData + 2 * 0x70 + NativeFields.DrawDataContainer_SlotAlpha) = 1f;
            LogOnce($"fademask:{clip}", $"[TimelineSeek] slot {slot}'s fade no longer reaches slot {owner0}'s prop");
        }
    }

    /// <summary>
    /// Before a C094 applies its alphas: one of a layer below the group-0 prop's
    /// owner loses its slot-2 bit first, so it can't fade the higher layer's prop,
    /// not even on its start frame (Start applies at once, before the tick's
    /// MaskLowerFades). The clip's root (+0x38) names its slot. Never throws into
    /// the game.
    /// </summary>
    private void ApplyFadeMaskDetour(nint clip, ulong colourArg, nint values)
    {
        try
        {
            if (!_fadeThreadLogged)
            {
                _fadeThreadLogged = true;
                _log.Information($"[TimelineSeek] C094 mask runs on the framework thread: {_framework.IsInFrameworkUpdateThread}");
            }
            nint root = clip != 0 ? *(nint*)(clip + NativeFields.SchedulerClip_RootTimeline) : 0;
            if (root != 0 && (*(byte*)(clip + NativeFields.C094_SlotMask) & (1 << 3)) != 0)
                foreach (var (roots, owner0) in _fadeOwners.Values)
                {
                    int slot = Array.IndexOf(roots, root);
                    if (slot < 0)
                        continue;
                    if (owner0 > slot)
                    {
                        *(byte*)(clip + NativeFields.C094_SlotMask) &= unchecked((byte)~(1 << 3));
                        _maskedFades[clip] = 1 << 3;
                        _maskWanted.Add(clip);
                    }
                    break;
                }
        }
        catch
        {
            // Losing the mask for one apply is all a failure costs.
        }
        _fadeMaskHook!.Original(clip, colourArg, values);
    }

    /// <summary>
    /// One prop timeline per weapon: the most recent C031 to fire takes it. The
    /// highest layer's C031 on that weapon owns it; if a lower one fired over it,
    /// the higher one is fired again (once per such birth; the birth correction
    /// seeks it to its layer's frame). A lower layer's prop animation on a weapon
    /// whose model a higher layer requested without animating it (Eat Apple's
    /// apple under Toast's tankard animation) is held at weight 0.
    /// </summary>
    private void ArbitratePropAnimations(
        nint character, List<(int Slot, nint Clip, int WeaponSlot)> summons, int owner0, int owner1, HashSet<nint> suppressed)
    {
        foreach (int weaponSlot in new[] { 2, 3 })
        {
            int modelOwner = weaponSlot == 2 ? owner0 : owner1;
            (int Slot, nint Clip, int WeaponSlot)? highest = null;
            foreach (var summon in summons)
                if (summon.WeaponSlot == weaponSlot && (highest == null || summon.Slot > highest.Value.Slot))
                    highest = summon;
            if (highest is not { } top)
                continue;
            nint weapon = TimelineSeekEngine.PropWeapon(character, top.Clip);
            nint current = TimelineSeekEngine.CurrentPropTimelineAny(_engine.PropGroup(weapon));
            if (current == 0 || !TimelineState.Of(current).Loaded)
                continue;
            int currentId = TimelineSeekEngine.PropTimelineIdOf(current);
            // The model belongs to a higher layer that doesn't animate it.
            if (modelOwner > top.Slot)
            {
                suppressed.Add(current);
                _engine.SetPropTimelineWeight(current, 0f);
                continue;
            }
            if (currentId == TimelineSeekEngine.PropTimelineId(top.Clip))
                continue;
            // A lower layer's C031 replaced the highest one's prop timeline.
            if (_propRefired.TryGetValue(weapon, out var refiredOver) && refiredOver == current)
                continue;
            _propRefired[weapon] = current;
            if (_engine.ReFireProp(character, top.Clip))
                LogOnce($"refire:{TimelineSeekEngine.PropTimelineId(top.Clip)}",
                    $"[TimelineSeek] prop {TimelineSeekEngine.PropTimelineId(top.Clip)} of slot {top.Slot} re-fired over a lower layer's prop {currentId}");
        }
        if (_propRefired.Count > 64)
            _propRefired.Clear();
    }

    /// <summary>
    /// Before the applier reads the cells: a lower layer's request put its model
    /// in; the highest layer's goes back. Only the model is written: the type-8
    /// handler keeps the loaded model alive while the cell names it, and a cell
    /// naming another model lets the loaded one lapse and loads the new one (so
    /// when the higher layer ends, the lower layer's prop comes in by itself).
    /// Counts and states are never touched, so each Stop stays balanced. Never
    /// throws into the game.
    /// </summary>
    private void ApplyWeaponRequestsDetour(nint control)
    {
        try
        {
            if (!_applierThreadLogged)
            {
                _applierThreadLogged = true;
                _log.Information($"[TimelineSeek] prop request applier runs on the framework thread: {_framework.IsInFrameworkUpdateThread}");
            }
            var map = _layerProps;
            if (control != 0 && map.TryGetValue(control, out var wanted))
            {
                HoldProp(control, 0, wanted.Group0);
                HoldProp(control, 1, wanted.Group1);
            }
        }
        catch
        {
            // Losing the priority for one apply is all a failure costs.
        }
        _applyRequestsHook!.Original(control);
    }

    private void HoldProp(nint control, int group, ulong wanted)
    {
        if (wanted == 0)
            return;
        if (*(uint*)(control + NativeFields.ActorControl_WeaponRequestCount + (8 + group * 9) * 4) == 0)
            return; // nobody requests this group any more
        ulong* model = (ulong*)(control + NativeFields.ActorControl_RequestedModel + group * 8);
        if (*model != wanted)
            *model = wanted;
    }

    // ── Prop birth correction ─────────────────────────────────────────

    /// <summary>One forward correction for each newly current prop timeline of
    /// Poser's actors; skipped for an actor whose seek is still in flight (the
    /// seek steps its props itself).</summary>
    private void CorrectPropBirths()
    {
        HashSet<nint>? seeking = null;
        foreach (var key in _work.Keys)
            (seeking ??= new()).Add(key.Character);
        foreach (nint character in _managed)
        {
            for (int w = 0; w < 4; w++)
            {
                var key = (character, w);
                nint weapon = TimelineSeekEngine.WeaponAt(character, w);
                nint group = _engine.PropGroup(weapon);
                nint current = group == 0 ? 0 : *(nint*)(group + NativeFields.TimelineGroup_CurrentHandle) is var h and not 0 ? *(nint*)h : 0;
                _propTimelines.TryGetValue(key, out var last);
                if (current == last)
                    continue;
                if (current == 0 || seeking?.Contains(character) == true || _restores.ContainsKey(character))
                {
                    _propTimelines[key] = current;
                    continue;
                }
                var sequencer = &((Character*)character)->Timeline.TimelineSequencer;
                if (!_engine.CorrectPropBirth(sequencer, character, weapon, current, out var note))
                    continue; // not loaded yet: decide next tick
                _propTimelines[key] = current;
                if (note != null)
                    LogOnce($"birth:{note}", $"[TimelineSeek] {note}");
            }
        }
        if (_propTimelines.Count > 64)
            _propTimelines.Clear();
    }
}
