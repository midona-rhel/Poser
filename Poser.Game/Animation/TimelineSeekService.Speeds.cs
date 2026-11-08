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

/// <summary>Applies speed and weight changes to animation slots, props, and effects.</summary>
public sealed unsafe partial class TimelineSeekService
{
    // ── Speed edges ───────────────────────────────────────────────────

    /// <summary>Before a new timeline replaces a PAUSED slot (speed 0): the old
    /// one stops with fade 0 and the new one starts without a blend-in.</summary>
    internal void PrepareInstantSwap(Character* character, int slot)
    {
        if (!Available)
            return;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, slot);
        if (root == 0 || TimelineState.Of(root).Speed != 0f)
            return;
        *(uint*)(root + NativeFields.SchedulerTimeline_StopFadeFrames) = 0;
        _engine.SetNoBlendStart(root);
    }

    /// <summary>Same-tick speed edge for one slot timeline (see
    /// TimelineSeekEngine.SetSlotTimelineSpeed).</summary>
    internal void ApplySlotSpeed(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, int slot, float speed)
    {
        try { _engine.SetSlotTimelineSpeed(sequencer, slot, speed); }
        catch (Exception ex) { LogOnce("slotspeed", $"[TimelineSeek] slot speed edge faulted: {ex.Message}"); }
    }

    // The effective speed last given to a held layer's slot timeline (by its
    // identity: a replay can reuse the address).
    private readonly Dictionary<nint, ((nint Node, uint Id) Identity, float Speed)> _heldSpeeds = new();

    /// <summary>
    /// Holds layer speeds natively, every tick: the slot timeline at layer ×
    /// overall (its C009s carry it to their controls; unchanged speed values skip the native update), the controls of the props the layer summons, and its
    /// effects at 0 when it is paused (the C012 speed handler drops 0). A replayed
    /// timeline is a new one and takes the speed on its first tick.
    /// </summary>
    internal void HoldSlotSpeeds(Character* character, IReadOnlyDictionary<int, float> speeds, float overall)
    {
        if (!Available || speeds.Count == 0)
            return;
        try
        {
            var sequencer = &character->Timeline.TimelineSequencer;
            Dictionary<nint, (float Speed, int Tick)>? propSpeeds = null;
            foreach (var (slot, speed) in speeds)
            {
                nint root = AnimationNativeState.SlotTimeline(sequencer, slot);
                if (root == 0 || SlotEnded(character, slot))
                    continue;
                float effective = speed * overall;
                _engine.SetSlotTimelineSpeed(sequencer, slot, effective);
                foreach (var (clip, _) in _engine.Collect(root).Summons)
                {
                    nint weapon = TimelineSeekEngine.PropWeapon((nint)character, clip);
                    if (weapon == 0)
                        continue;
                    // Only a prop this clip is running: the weapon's current prop
                    // timeline is its row. Before it fires, or once another layer's
                    // prop replaced it, the weapon is someone else's.
                    if (TimelineSeekEngine.CurrentPropTimeline(_engine.PropGroup(weapon), TimelineSeekEngine.PropTimelineId(clip)) == 0)
                        continue;
                    if (_weaponSpeedHook == null)
                        // No hook: the push resets the prop timeline every frame; hold
                        // what is rendered (its controls) at least.
                        TimelineSeekEngine.SetWeaponControlSpeeds(weapon, effective);
                    // Directly as well: a speed edge can land after this frame's push,
                    // which would leave the prop a frame on the old speed (half a frame
                    // off at x0.5). The summoned prop gets no push at all. The native setter skips unchanged speed values.
                    _engine.SetWeaponTimelineSpeeds(weapon, effective);
                    if (TimelineSeekEngine.PropWeaponSlot(clip) != 3)
                    {
                        // Copy without stale entries, then publish once below.
                        if (propSpeeds == null)
                        {
                            propSpeeds = new Dictionary<nint, (float Speed, int Tick)>();
                            foreach (var (key, value) in _layerPropSpeeds)
                                if (value.Tick == _tick)
                                    propSpeeds[key] = value;
                        }
                        propSpeeds[weapon] = (speed, _tick);
                    }
                }
                var identity = TimelineState.Of(root).Identity;
                if (!_heldSpeeds.TryGetValue(root, out var last) || last.Identity != identity || last.Speed != effective)
                {
                    if (effective == 0f)
                        _engine.SetVfxSpeeds(root, 0f);
                    _heldSpeeds[root] = (identity, effective);
                }
            }
            if (_heldSpeeds.Count > 256)
                _heldSpeeds.Clear();
            if (propSpeeds != null)
                _layerPropSpeeds = propSpeeds;
        }
        catch (Exception ex) { LogOnce("slotspeedhold", $"[TimelineSeek] layer speed hold faulted: {ex.Message}"); }
    }

    // Characters whose actor-wide speed Poser holds, with the tick last held, and
    // whether slot 0 was set for a mounted rider.
    private readonly Dictionary<nint, (int Tick, bool MountSlot)> _actorHeld = new();

    /// <summary>
    /// The whole-actor speed (Poser's pause) where the game's push doesn't reach,
    /// every tick it is held: status/aura effects (pushed only on a speed change,
    /// spawned at 1), and a mounted rider's slot 0 (its push takes the mount's
    /// speed, +0x5E8). A slot with its own layer speed is HoldSlotSpeeds'.
    /// </summary>
    internal void HoldActorSpeed(Character* character, float overall, IReadOnlyDictionary<int, float> slotSpeeds)
    {
        if (!Available)
            return;
        try
        {
            nint address = (nint)character;
            if (overall != 1f)
                _engine.SetStatusVfxSpeed(address, overall);
            bool mounted = *(ushort*)(address + NativeFields.Character_MountId) != 0 && !slotSpeeds.ContainsKey(0);
            if (mounted)
                _engine.SetSlotTimelineSpeed(&character->Timeline.TimelineSequencer, 0, overall);
            _actorHeld[address] = (_tick, mounted);
        }
        catch (Exception ex) { LogOnce("actorspeed", $"[TimelineSeek] actor speed hold faulted: {ex.Message}"); }
    }

    /// <summary>An actor-wide hold Poser let go of: the status effects and a rider's
    /// slot 0 go back to the game's speed once.</summary>
    private void ReleaseActorSpeeds()
    {
        if (_actorHeld.Count == 0)
            return;
        List<nint>? gone = null;
        foreach (var (address, held) in _actorHeld)
            if (_tick - held.Tick > 2)
                (gone ??= new()).Add(address);
        if (gone == null)
            return;
        foreach (var address in gone)
        {
            _actorHeld.Remove(address);
            if (!_managed.Contains(address))
                continue;
            // Container Flags3 (+0x34E) bit 2: the next UpdateTimelines re-pushes the
            // speed natively (FUN_1408BDB50): slot 0 from its right source (the
            // rider's +0x5E8, or the mount's), the other slots, props, model
            // effects and every status effect.
            *((byte*)&((Character*)address)->Timeline + NativeFields.TimelineContainer_Flags3) |= 4;
        }
    }

    /// <summary>Holds layer weights (see TimelineSeekEngine.SetSlotWeight).</summary>
    internal void ApplySlotWeights(Character* character, IReadOnlyDictionary<int, float> weights)
    {
        if (weights.Count == 0)
            return;
        try
        {
            var suppressed = _suppressedProps;
            foreach (var (slot, weight) in weights)
                if (!SlotEnded(character, slot))
                    _engine.SetSlotWeight(&character->Timeline.TimelineSequencer, (nint)character, slot, weight, suppressed);
        }
        catch (Exception ex) { LogOnce("weight", $"[TimelineSeek] layer weight faulted: {ex.Message}"); }
    }

    /// <summary>Pause/resume edge for the actor's effects (the game drops speed 0).</summary>
    internal void ApplyVfxSpeed(Character* character, float overall)
    {
        try { _engine.SetActorVfxSpeeds(&character->Timeline.TimelineSequencer, (nint)character, overall); }
        catch (Exception ex) { LogOnce("vfxspeed", $"[TimelineSeek] VFX speed edge faulted: {ex.Message}"); }
    }

    /// <summary>Same-tick speed edge for the actor's prop timelines.</summary>
    internal void ApplyPropSpeed(Character* character, float overall)
    {
        try { _engine.SetPropSpeeds((nint)character, overall); }
        catch (Exception ex) { LogOnce("propspeed", $"[TimelineSeek] prop speed edge faulted: {ex.Message}"); }
    }
}
