using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Natives;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Poser.Game.Animation;

/// <summary>
/// Runs native timeline seeks (#533) on the framework tick. The scrub UI calls
/// from the ImGui draw callback; it only records the latest target per
/// actor/slot, and the seek runs once on the next tick (coalesced), followed by
/// that seek's deferred frames. It also corrects newly born prop timelines and
/// brings a redrawn actor back on the frame it was on.
/// </summary>
public sealed unsafe class TimelineSeekService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private readonly TimelineSeekEngine _engine;
    private readonly Poser.Game.Services.IActorManager _actors;
    // Written by the UI (draw callback), drained by the framework tick.
    private readonly object _targetLock = new();
    private readonly Dictionary<(ActorId Actor, int Slot), float> _targets = new();
    // Deferred work per (character address, slot); an address is only touched
    // while it is still one of Poser's actors this tick.
    private readonly Dictionary<(nint Character, int Slot), TimelineSeekWork> _work = new();
    // Havok control seconds → timeline frames, per slot timeline.
    private readonly Dictionary<(ActorId Actor, int Slot), (nint Root, float Offset)> _offsets = new();
    private readonly HashSet<string> _logged = new();
    // Last seen current prop timeline per (character, weapon slot); a change is a birth.
    private readonly Dictionary<(nint Character, int Weapon), nint> _propTimelines = new();
    private readonly HashSet<nint> _managed = new();
    private readonly Dictionary<nint, Restore> _restores = new();
    // Armed loops: (actor, slot) → the timeline id the loop belongs to. Written by
    // the UI, read by the tick (under _targetLock).
    private readonly Dictionary<(ActorId Actor, int Slot), ushort> _loops = new();
    // Layers Poser applied (under _targetLock) → the timeline id they play (0 until
    // the tick sees it live). The game ends them only at their end, never paused.
    private readonly Dictionary<(ActorId Actor, int Slot), ushort> _owned = new();
    // Slots whose owned layer the game ended (under _targetLock): the layer's held
    // speed and weight don't act on what plays there next.
    private readonly HashSet<(nint Character, int Slot)> _endedSlots = new();
    // An owned layer's length in seconds, as last read while it played.
    private readonly Dictionary<(ActorId Actor, int Slot), float> _ownedLengths = new();
    // The slot timeline whose pre-end notify lead (+0x26E) is switched off, and its value.
    private readonly Dictionary<(ActorId Actor, int Slot), (nint Root, (nint Node, uint Id) Identity, short Original)> _leads = new();
    // Layers let go of whose lead still needs its value back (under _targetLock).
    private readonly List<(ActorId Actor, int Slot)> _leadRestores = new();
    // Scrub targets for a slot timeline still loading (a replayed layer), in seconds
    // from its begin, with the tick they were asked on (under _targetLock).
    private readonly Dictionary<(ActorId Actor, int Slot), (float Seconds, int Tick)> _pendingTargets = new();
    // Last remaining-frames reading per armed loop (its decrease is the loop's margin).
    private readonly Dictionary<(ActorId Actor, int Slot), (nint Root, float Value, float Clock)> _remaining = new();

    private delegate void DisableDrawFn(nint character);
    private readonly Hook<DisableDrawFn>? _disableDrawHook;

    // FUN_1418CAB50(ActionTimelineManager*, Weapon* W, float speed): the container
    // update's per-frame push of the overall speed into W's prop group (every
    // drawn character, main thread; also its dirty and init paths). Pending a
    // patchcheck entry; unique on the analysed build (sig.py, 1 hit).
    private const string ActionTimelineManager_SetWeaponSpeed =
        Poser.Game.Natives.NativeSignatures.ActionTimelineManager_SetPropSpeed;
    private delegate void WeaponSpeedFn(nint manager, nint weapon, float speed);
    private readonly Hook<WeaponSpeedFn>? _weaponSpeedHook;
    // Prop weapons whose current prop belongs to a layer with its own speed: the
    // layer's factor, stamped with the tick that wrote it (a stale entry is ignored).
    // Never mutated once published: each write builds a new map and swaps the
    // reference, so the detour can read it from any thread.
    private volatile Dictionary<nint, (float Speed, int Tick)> _layerPropSpeeds = new();
    private int _tick;

    // FUN_141900F50(actorControl): the single consumer of the actor control's
    // weapon-request cells (+0x6D0 state, +0x718 count, model per group at
    // +0xF8), run after the timeline step for every control with +0x6CC set. Pending
    // a patchcheck entry; unique on the analysed build (sig.py, 1 hit).
    private const string ActorControl_ApplyWeaponRequests =
        Poser.Game.Natives.NativeSignatures.ActorControl_ApplyWeaponRequests;
    private delegate void ApplyWeaponRequestsFn(nint actorControl);
    private readonly Hook<ApplyWeaponRequestsFn>? _applyRequestsHook;
    // C094.ApplyMask 0x14191A260(clip, colourArg, float* values): the only writer
    // of the DrawData weapon-slot alphas (values[1..3]), slot s only while clip+0xD3
    // bit s+1 is set; also run by C094 Start (the first frame). Main thread, inside
    // the timeline step.
    private delegate void ApplyFadeMaskFn(nint clip, ulong colourArg, nint values);
    private readonly Hook<ApplyFadeMaskFn>? _fadeMaskHook;
    // Per Poser character: its 14 slot timelines and the slot owning the group-0
    // prop (-1 none), as of this tick. Main thread only; swapped whole.
    private Dictionary<nint, (nint[] Roots, int Owner0)> _fadeOwners = new();
    private bool _fadeThreadLogged;
    // Per Poser actor control: the prop model the highest layer requests, per
    // request group (0 = none). Rebuilt each tick and swapped whole.
    private volatile Dictionary<nint, (ulong Group0, ulong Group1)> _layerProps = new();
    private bool _applierThreadLogged;
    // Prop timelines a lower layer animates on a weapon a higher layer owns: held
    // at weight 0 so they can't move the higher layer's prop.
    private HashSet<nint> _suppressedProps = new();
    // Per weapon: the lower layer's prop timeline a higher layer's C031 was
    // re-fired over (once per such birth).
    private readonly Dictionary<nint, nint> _propRefired = new();

    /// <summary>What a redraw is about to throw away, captured at the entry of
    /// Character::DisableDraw (it tears down the props, then the timeline
    /// container: every slot timeline stops).</summary>
    private sealed class Restore
    {
        public readonly (int Slot, ushort Id, float Clock)[] Slots;
        public readonly bool[] Done;
        // The address alone can be reused by a new object within the restore
        // window; the object index and entity id must match too.
        public readonly ushort ObjectIndex;
        public readonly uint EntityId;
        public bool Replayed;
        public bool SpeedLogged;
        public int Frame;

        /// <summary>Another redraw arrived: whatever was replayed is gone again.</summary>
        public void Restart()
        {
            Replayed = false;
            Array.Clear(Done);
            Frame = 0;
        }
        public Restore((int, ushort, float)[] slots, ushort objectIndex, uint entityId)
        {
            Slots = slots;
            Done = new bool[slots.Length];
            ObjectIndex = objectIndex;
            EntityId = entityId;
        }
    }

    private const int RestoreFrames = 600;

    public TimelineSeekService(
        IFramework framework, ISigScanner scanner, IGameInteropProvider hooking, IPluginLog log,
        AnimationNativeState native, Poser.Game.Services.IActorManager actors)
    {
        _actors = actors;
        _framework = framework;
        _log = log;
        _native = native;
        var natives = new TimelineSeekNatives(scanner, log);
        var layout = new TimelineSeekLayout(scanner, log);
        _engine = new TimelineSeekEngine(natives, layout);
        if (_engine.Ready)
        {
            try
            {
                if (scanner.TryScanText(NativeSignatures.Character_DisableDraw, out var disableDraw) && disableDraw != 0)
                {
                    _disableDrawHook = hooking.HookFromAddress<DisableDrawFn>(disableDraw, DisableDrawDetour);
                    _disableDrawHook.Enable();
                }
                else
                    log.Warning("Animation: Character.DisableDraw not found; a redraw restores the actor stopped.");
            }
            catch (Exception ex)
            {
                log.Warning($"Animation: DisableDraw hook failed ({ex.Message}); a redraw restores the actor stopped.");
            }
            try
            {
                if (scanner.TryScanText(Poser.Game.Natives.NativeSignatures.C094_ApplyMask, out var fadeMask) && fadeMask != 0)
                {
                    _fadeMaskHook = hooking.HookFromAddress<ApplyFadeMaskFn>(fadeMask, ApplyFadeMaskDetour);
                    _fadeMaskHook.Enable();
                }
                else
                    log.Warning("Animation: C094.ApplyMask not found; a lower layer's fade blinks a higher layer's prop for a frame.");
            }
            catch (Exception ex)
            {
                log.Warning($"Animation: C094.ApplyMask hook failed ({ex.Message}).");
            }
            try
            {
                if (scanner.TryScanText(ActorControl_ApplyWeaponRequests, out var applier) && applier != 0)
                {
                    _applyRequestsHook = hooking.HookFromAddress<ApplyWeaponRequestsFn>(applier, ApplyWeaponRequestsDetour);
                    _applyRequestsHook.Enable();
                }
                else
                    log.Warning("Animation: the prop request applier was not found; the most recent layer's prop wins.");
            }
            catch (Exception ex)
            {
                log.Warning($"Animation: prop request applier hook failed ({ex.Message}); the most recent layer's prop wins.");
            }
            try
            {
                if (scanner.TryScanText(ActionTimelineManager_SetWeaponSpeed, out var weaponSpeed) && weaponSpeed != 0)
                {
                    _weaponSpeedHook = hooking.HookFromAddress<WeaponSpeedFn>(weaponSpeed, WeaponSpeedDetour);
                    _weaponSpeedHook.Enable();
                }
                else
                    log.Warning("Animation: the prop group speed push was not found; a layer's props follow its speed on their controls only.");
            }
            catch (Exception ex)
            {
                log.Warning($"Animation: prop group speed hook failed ({ex.Message}); a layer's props follow its speed on their controls only.");
            }
        }
        _framework.Update += OnUpdate;
    }

    public bool Available => _engine.Ready;

    /// <summary>The engine's tree walks, for the scrub fallback.</summary>
    internal TimelineSeekEngine Engine => _engine;

    /// <summary>
    /// Whether the native seek can move this actor's slot right now (a live,
    /// loaded, float-frame timeline). False keeps the caller on its fallback.
    /// </summary>
    internal bool Supports(Character* character, int slot)
    {
        if (!Available || character == null)
            return false;
        // Scheduler-driven actors (event/cutscene: +0x1CE8 bit 4) run their
        // timelines from a scheduler instance; leave them to the clock writes.
        if ((*(byte*)((nint)character + 0x1CE8) & 0x10) != 0)
            return false;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, slot);
        var state = TimelineState.Of(root);
        return state.Seekable && !state.IntegerFrame;
    }

    /// <summary>A layer's scrub reading from its slot timeline clock, in seconds
    /// from the timeline's begin, or null when the native seek can't move it.</summary>
    internal Poser.Domain.Animation.ScrubControlReading? TimelineReading(Character* character, AnimationSlot slot)
    {
        if (!Supports(character, (int)slot))
            return null;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, (int)slot);
        // Clamped at its end, the clock stands still while the controls loop: the
        // layer's time is its controls'.
        if (TimelineState.Of(root).ClampedAtEnd)
            return _engine.ClampedReading(root) is { } clamped
                ? new(Poser.Domain.Animation.ScrubControlId.Timeline(slot), clamped.Time, clamped.Duration, TimelineState.Of(root).Speed)
                : null;
        float begin = TimelineState.Of(root).Begin, end = TimelineState.Of(root).End;
        if (!(end > begin))
            end = begin + TimelineState.Of(root).Length;
        // +0x40 counts the TMB's padding: past the last animation's end every clip is
        // done and the timeline finalizes (ends) on its next Advance.
        var tree = _engine.Collect(root);
        if (tree.PoseEnd > begin + 1f && !tree.PoseEndPending)
            end = Math.Min(end, tree.PoseEnd);
        if (!(end > begin))
            return null;
        return new(Poser.Domain.Animation.ScrubControlId.Timeline(slot),
            (TimelineState.Of(root).Clock - begin) / 30f, (end - begin) / 30f, TimelineState.Of(root).Speed);
    }

    /// <summary>Records the latest scrub target for a layer scrubbed by its
    /// timeline clock: seconds from the timeline's begin.</summary>
    internal void RequestTimeline(ActorId actor, Character* character, AnimationSlot slot, float seconds)
    {
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, (int)slot);
        if (root == 0)
            return;
        if (TimelineState.Of(root).ClampedAtEnd)
        {
            lock (_targetLock)
                _controlTargets[(actor, (int)slot)] = seconds;
            return;
        }
        lock (_targetLock)
        {
            _pendingTargets.Remove((actor, (int)slot));
            _targets[(actor, (int)slot)] = TimelineState.Of(root).Begin + seconds * 30f;
        }
    }

    // Scrub targets for clamped one-shots (mount idles), in control seconds.
    private readonly Dictionary<(ActorId Actor, int Slot), float> _controlTargets = new();
    private void RunControlTargets()
    {
        KeyValuePair<(ActorId Actor, int Slot), float>[] targets;
        lock (_targetLock)
        {
            if (_controlTargets.Count == 0)
                return;
            targets = _controlTargets.ToArray();
            _controlTargets.Clear();
        }
        foreach (var (key, seconds) in targets)
        {
            var character = _native.Resolve(key.Actor, out _);
            if (character == null)
                continue;
            nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, key.Slot);
            if (root != 0 && TimelineState.Of(root).ClampedAtEnd)
                _engine.SeekClampedControls(root, seconds);
        }
    }

    /// <summary>A scrub target for a slot timeline that is still loading: it runs
    /// once the timeline can be seeked (dropped after ~5 s). False when the slot
    /// has no timeline or it is already loaded.</summary>
    internal bool RequestTimelineWhenLoaded(ActorId actor, Character* character, AnimationSlot slot, float seconds)
    {
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, (int)slot);
        if (!Available || root == 0 || TimelineState.Of(root).Loaded)
            return false;
        lock (_targetLock)
            _pendingTargets[(actor, (int)slot)] = (seconds, _tick);
        return true;
    }

    private void PromotePendingTargets()
    {
        KeyValuePair<(ActorId Actor, int Slot), (float Seconds, int Tick)>[] pending;
        lock (_targetLock)
        {
            if (_pendingTargets.Count == 0)
                return;
            pending = _pendingTargets.ToArray();
        }
        foreach (var (key, target) in pending)
        {
            var character = _native.Resolve(key.Actor, out _);
            bool ready = character != null && Supports(character, key.Slot);
            if (!ready && character != null && _tick - target.Tick < 300)
                continue;
            lock (_targetLock)
            {
                if (!_pendingTargets.TryGetValue(key, out var current) || current != target)
                    continue;
                _pendingTargets.Remove(key);
                if (ready)
                {
                    nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, key.Slot);
                    _targets[key] = TimelineState.Of(root).Begin + target.Seconds * 30f;
                }
            }
        }
    }

    /// <summary>Records the latest scrub target in Havok control seconds.</summary>
    internal void Request(ActorId actor, Character* character, AnimationSlot slot, float seconds, float controlLocalTime)
    {
        int index = (int)slot;
        var key = (actor, index);
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, index);
        if (root == 0)
            return;
        // Clamped at its end: the control's own seconds are the target.
        if (TimelineState.Of(root).ClampedAtEnd)
        {
            lock (_targetLock)
                _controlTargets[key] = seconds;
            return;
        }
        // Measured once per slot timeline, before this service has synced it: the
        // clip sync leaves LocalTime a fraction (its ε) short of natural, so a
        // re-measure after a seek would carry that fraction into the next target.
        if (!_offsets.TryGetValue(key, out var mapping) || mapping.Root != root)
            mapping = (root, TimelineState.Of(root).Clock - controlLocalTime * 30f);
        _offsets[key] = mapping;
        lock (_targetLock)
            _targets[key] = seconds * 30f + mapping.Offset;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!_engine.Ready)
            return;
        _tick++;
        _engine.BeginFrameTrees();
        try
        {
            _managed.Clear();
            foreach (var actor in _actors.Actors)
                if (actor.Address != 0 && ((CSGameObject*)actor.Address)->IsCharacter())
                    _managed.Add(actor.Address);

            PumpRestores();
            CorrectPropBirths();
            UpdateLayerProps();
            PromotePendingTargets();
            RunControlTargets();
            ReleaseActorSpeeds();

            KeyValuePair<(ActorId, int), float>[] targets;
            lock (_targetLock)
            {
                targets = new KeyValuePair<(ActorId, int), float>[_targets.Count];
                ((ICollection<KeyValuePair<(ActorId, int), float>>)_targets).CopyTo(targets, 0);
                _targets.Clear();
            }
            foreach (var (key, target) in targets)
                RunSeek(key, target);
            PumpLoops(targets);

            List<(nint, int)>? done = null;
            foreach (var (key, work) in _work)
            {
                nint current = _managed.Contains(key.Character)
                    ? AnimationNativeState.SlotTimeline(&((Character*)key.Character)->Timeline.TimelineSequencer, key.Slot)
                    : 0;
                if (work.Tick(_engine, current))
                    continue;
                if (work.Log != null)
                    _log.Information($"[TimelineSeek] slot {key.Slot}: {work.Log}");
                (done ??= new()).Add(key);
            }
            if (done != null)
                foreach (var key in done)
                    _work.Remove(key);
        }
        catch (Exception ex)
        {
            _log.Error($"[TimelineSeek] tick faulted: {ex}");
            lock (_targetLock)
                _targets.Clear();
            _work.Clear();
            _restores.Clear();
        }
        finally
        {
            _engine.EndFrameTrees();
        }
    }

    // ── Redraw restore ────────────────────────────────────────────────

    /// <summary>
    /// Entry of Character::DisableDraw (every redraw path: Penumbra, Glamourer,
    /// the game's own hide). At entry the slot ids, handles and clocks are intact;
    /// the function then destroys the props and stops every slot. Only drawn
    /// (+0x9A bit 7) Poser actors; never throws into the game.
    /// </summary>
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
                    if ((*(byte*)(clip + 0x88) & 0x10) == 0 || (*(byte*)(clip + 0x10) & 0x10) == 0)
                        continue;
                    nint data = *(nint*)(clip + 0x48);
                    int group = *(ushort*)(data + 0x14);
                    ulong model = *(ulong*)(data + 0x18);
                    // Slots ascend, so a later (higher) layer overrides.
                    if (group == 0) { group0 = model; owner0 = slot; }
                    else if (group == 1) { group1 = model; owner1 = slot; }
                    requests.Add((slot, clip, group));
                }
                // A C043 Summon Weapon of type 8 asks for a group-0 prop the same way.
                foreach (var clip in tree.SummonWeapons)
                {
                    if ((*(byte*)(clip + 0x88) & 0x10) == 0 || (*(byte*)(clip + 0x10) & 0x10) == 0)
                        continue;
                    nint data = *(nint*)(clip + 0x48);
                    if (*(int*)(data + 0xC) != 8)
                        continue;
                    group0 = *(ulong*)(data + 0x10);
                    owner0 = slot;
                }
                foreach (var clip in tree.Invisibilities)
                    fades.Add((slot, clip));
                foreach (var (clip, _) in tree.Summons)
                    if ((*(byte*)(clip + 0x10) & 0x10) != 0)
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
                    *(byte*)(clip + 0xA4) = 0;
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
                *(byte*)(clip + 0xD3) |= _maskedFades[clip];
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
            if ((*(byte*)(clip + 0xD3) & PropSlotBit) == 0)
                continue;
            *(byte*)(clip + 0xD3) &= unchecked((byte)~PropSlotBit);
            _maskedFades[clip] = PropSlotBit;
            // FUN_1408B0B00(ddc = chara+0x6F8, slot, alpha): slot 2's alpha at
            // ddc + 2·0x70 + 0x68. Stored, not multiplied: a higher layer's own
            // active C094 writes over it the same tick.
            *(float*)(character + 0x6F8 + 2 * 0x70 + 0x68) = 1f;
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
            nint root = clip != 0 ? *(nint*)(clip + 0x38) : 0;
            if (root != 0 && (*(byte*)(clip + 0xD3) & (1 << 3)) != 0)
                foreach (var (roots, owner0) in _fadeOwners.Values)
                {
                    int slot = Array.IndexOf(roots, root);
                    if (slot < 0)
                        continue;
                    if (owner0 > slot)
                    {
                        *(byte*)(clip + 0xD3) &= unchecked((byte)~(1 << 3));
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
        if (*(uint*)(control + 0x718 + (8 + group * 9) * 4) == 0)
            return; // nobody requests this group any more
        ulong* model = (ulong*)(control + 0xF8 + group * 8);
        if (*model != wanted)
            *model = wanted;
    }

    private void DisableDrawDetour(nint character)
    {
        try
        {
            // A character with a pending restore counts even if Poser's list dropped
            // it for a moment: the game is calling this on a live object.
            if (character != 0 && (_managed.Contains(character) || _restores.ContainsKey(character))
                && (*(byte*)(character + 0x9A) & 0x80) != 0)
                Capture(character);
        }
        catch
        {
            // A capture failure only loses the restore.
        }
        _disableDrawHook!.Original(character);
    }

    private void Capture(nint character)
    {
        if ((*(byte*)(character + 0x1CE8) & 0x10) != 0)
            return; // scheduler-driven actor: its scheduler replays it
        // A redraw during a pending restore (redraws mashed) would capture the
        // transient base timeline: keep the first capture, but start its replay
        // over, since this redraw throws away whatever it already replayed.
        if (_restores.TryGetValue(character, out var pending))
        {
            pending.Restart();
            _work.Remove((character, 0));
            _work.Remove((character, 1));
            _work.Remove((character, 2));
            return;
        }
        var sequencer = &((Character*)character)->Timeline.TimelineSequencer;
        var slots = new List<(int, ushort, float)>(2);
        // Base, Upper Body and a held facial expression: DisableDraw stops all.
        foreach (int slot in new[] { 0, 1, 2 })
        {
            nint timeline = AnimationNativeState.SlotTimeline(sequencer, slot);
            ushort id = AnimationNativeState.RequestedId(sequencer, slot);
            if (timeline == 0 || id == 0 || !TimelineState.Of(timeline).Loaded
                || TimelineState.Of(timeline).IntegerFrame || TimelineState.Of(timeline).Finished)
                continue;
            slots.Add((slot, id, TimelineState.Of(timeline).Clock));
        }
        var go = (CSGameObject*)character;
        if (slots.Count > 0)
            _restores[character] = new Restore(slots.ToArray(), go->ObjectIndex, go->EntityId);
    }

    // TimelineContainer +0x2C3 bit 0 (patchcheck TimelineContainer.DrawnFlags): cleared by
    // DisableDraw, set by OnEnableDraw before the draw-ready check runs.
    private const int ContainerDrawnFlagsOffset = 0x2C3;

    /// <summary>
    /// After the redraw (RE notes/poser-animation-model.md §2): Mode, BaseOverride and
    /// the emote state survive it; the slots don't. Replay each recorded id as soon as
    /// the container is drawn again, not after the draw is ready: an actor in EmoteLoop
    /// with no EmoteMode starts no Base timeline itself, and the draw waits for one.
    /// Then wait until each is loaded and seek it to the recorded clock. The seek's
    /// deferred work re-fires a C031 whose start gave up while the new weapon object
    /// loaded, and seeks it.
    /// </summary>
    private void PumpRestores()
    {
        if (_restores.Count == 0)
            return;
        List<nint>? finished = null;
        foreach (var (character, restore) in _restores)
        {
            if (++restore.Frame > RestoreFrames)
            {
                LogOnce($"restore-timeout:{restore.Slots[0].Id}", "[TimelineSeek] redraw restore timed out; the actor keeps its default timeline.");
                (finished ??= new()).Add(character);
                continue;
            }
            // Poser's actor list can drop an actor for a few frames while its redraw
            // settles; wait for it (never touch an address that isn't listed).
            if (!_managed.Contains(character))
                continue;
            var go = (CSGameObject*)character;
            if (go->ObjectIndex != restore.ObjectIndex || go->EntityId != restore.EntityId)
            {
                (finished ??= new()).Add(character); // a different object now lives here
                continue;
            }
            var chara = (Character*)character;
            if (go->DrawObject == null || (*((byte*)&chara->Timeline + ContainerDrawnFlagsOffset) & 1) == 0)
                continue;
            var sequencer = &chara->Timeline.TimelineSequencer;
            if (!restore.Replayed)
            {
                nint noBlend = 0;
                foreach (var (slot, id, _) in restore.Slots)
                {
                    var played = _native.ReplayTimeline(chara, id);
                    if (!played.Success)
                        LogOnce($"restore-play:{id}", $"[TimelineSeek] redraw restore could not replay {id}: {played.Detail}");
                    else if (noBlend == 0)
                        noBlend = AnimationNativeState.SlotTimeline(sequencer, slot);
                }
                // The replayed clips start once loaded, without a blend in from whatever
                // the game started first (the actor control's +0x6B8, one start).
                if (noBlend != 0)
                    _engine.SetNoBlendStart(noBlend);
                restore.Replayed = true;
                continue;
            }
            bool all = true;
            for (int i = 0; i < restore.Slots.Length; i++)
            {
                if (restore.Done[i])
                    continue;
                var (slot, id, clock) = restore.Slots[i];
                nint timeline = AnimationNativeState.SlotTimeline(sequencer, slot);
                ushort live = AnimationNativeState.RequestedId(sequencer, slot);
                if (timeline == 0 || live != id || !TimelineState.Of(timeline).Loaded)
                {
                    all = false;
                    continue;
                }
                if (!restore.SpeedLogged)
                {
                    restore.SpeedLogged = true;
                    LogOnce($"restore-speed:{TimelineState.Of(timeline).Speed}",
                        $"[TimelineSeek] redraw restore: replayed timeline born at speed {TimelineState.Of(timeline).Speed}");
                }
                var work = new TimelineSeekWork { AllowFire = true };
                var refused = _engine.Seek(character, timeline, clock, work, wrapForward: false);
                if (refused != null)
                    LogOnce($"restore-seek:{id}:{refused}", $"[TimelineSeek] redraw restore of {id}: {refused}");
                else
                    _work[(character, slot)] = work;
                restore.Done[i] = true;
            }
            if (all)
                (finished ??= new()).Add(character);
        }
        if (finished != null)
            foreach (var character in finished)
                _restores.Remove(character);
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
                nint current = group == 0 ? 0 : *(nint*)(group + 0xA48) is var h and not 0 ? *(nint*)h : 0;
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

    // ── Scrub ─────────────────────────────────────────────────────────

    private void RunSeek((ActorId Actor, int Slot) key, float target)
    {
        var character = _native.Resolve(key.Actor, out var detail);
        if (character == null)
        {
            LogOnce($"resolve:{key}", $"[TimelineSeek] {key.Actor}: {detail}");
            return;
        }
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, key.Slot);
        if (root == 0)
            return;
        var work = new TimelineSeekWork();
        var refused = _engine.Seek((nint)character, root, target, work);
        if (refused != null)
        {
            LogOnce($"refused:{key}:{refused}", $"[TimelineSeek] {key.Actor} slot {key.Slot}: {refused}");
            return;
        }
        _work[((nint)character, key.Slot)] = work;
    }

    // ── Loop ──────────────────────────────────────────────────────────

    /// <summary>Arms a loop for one layer: just before the slot timeline would
    /// run out, it is wrapped back to its begin (the seek's wrap: clips stop and
    /// re-fire, effects re-spawn), so it never ends and is never replayed.</summary>
    internal void SetLoop(ActorId actor, AnimationSlot slot, ushort timeline)
    {
        lock (_targetLock)
            _loops[(actor, (int)slot)] = timeline;
    }

    internal void ClearLoop(ActorId actor, AnimationSlot slot)
    {
        lock (_targetLock)
            if (_loops.Remove((actor, (int)slot)))
                _leadRestores.Add((actor, (int)slot));
    }

    internal void ClearLoops(ActorId actor)
    {
        lock (_targetLock)
        {
            List<(ActorId, int)>? gone = null;
            foreach (var key in _loops.Keys)
                if (key.Actor.Equals(actor))
                    (gone ??= new()).Add(key);
            if (gone != null)
                foreach (var key in gone)
                {
                    _loops.Remove(key);
                    _leadRestores.Add(key);
                }
        }
    }

    /// <summary>Loops are held while the session's commands are suspended.</summary>
    internal bool LoopsSuspended { get; set; }

    // The next frame's step is predicted from this frame's; a longer frame could
    // cross the end before the next check, so wrap one step early.
    private const float LoopLeadSteps = 2f;

    /// <summary>Layers Poser applied. A looped one wraps; any other one-shot is
    /// ended by the game at its end as it would be, but never while it is held or
    /// scrubbed (LayerEnded once it is gone). Owning again (a replay) or letting go
    /// clears an ended slot.</summary>
    internal void SetOwned(ActorId actor, AnimationSlot slot, bool owned)
    {
        var character = _native.Resolve(actor, out _);
        lock (_targetLock)
        {
            if (owned)
                _owned[(actor, (int)slot)] = 0;
            else if (_owned.Remove((actor, (int)slot)))
                _leadRestores.Add((actor, (int)slot));
            if (character != null)
                _endedSlots.Remove(((nint)character, (int)slot));
        }
    }

    /// <summary>An owned layer's animation is gone from its slot: the game ended
    /// it (or something outside Poser replaced it). Raised once, on the framework
    /// thread, with the animation's length in seconds.</summary>
    internal event Action<ActorId, AnimationSlot, float>? LayerEnded;

    private bool SlotEnded(Character* character, int slot)
    {
        lock (_targetLock)
            return _endedSlots.Count != 0 && _endedSlots.Contains(((nint)character, slot));
    }

    /// <summary>
    /// An owned layer's identity: the first live timeline after the play (an
    /// emote's intro excluded) is its animation; once the slot runs anything else,
    /// it ended. False while it isn't live (not yet, or no more).
    /// </summary>
    private bool TrackOwned(
        (ActorId Actor, int Slot) key, Character* character,
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, ushort live)
    {
        ushort id;
        lock (_targetLock)
            if (!_owned.TryGetValue(key, out id))
                return false;
        if (id == 0)
        {
            if (live == 0 || (key.Slot == 0 && IntroQueued(sequencer, live)))
                return false;
            lock (_targetLock)
                if (_owned.ContainsKey(key))
                    _owned[key] = live;
            id = live;
        }
        if (live == id)
        {
            // Read while it plays: until every clip has loaded, the reading is +0x40's
            // padded length, not the animation's.
            if (TimelineReading(character, (AnimationSlot)key.Slot) is { Duration: > 0f } reading)
                _ownedLengths[key] = reading.Duration;
            return true;
        }
        lock (_targetLock)
        {
            _owned.Remove(key);
            _endedSlots.Add(((nint)character, key.Slot));
        }
        _leads.Remove(key);
        _remaining.Remove(key);
        float seconds = _ownedLengths.Remove(key, out var last) ? last : 0f;
        LayerEnded?.Invoke(key.Actor, (AnimationSlot)key.Slot, seconds);
        return false;
    }

    /// <summary>The countdown's per-tick decrease, from a plain tick only: a scrub or
    /// wrap moves the clock by far more (or backward), and its jump is no rate.</summary>
    private float MeasureStep((ActorId Actor, int Slot) key, nint root, float remaining, float expected)
    {
        float clock = TimelineState.Of(root).Clock;
        float step = _remaining.TryGetValue(key, out var last) && last.Root == root
            && Math.Abs(clock - last.Clock - expected) <= Math.Max(1f, expected)
            ? last.Value - remaining
            : 0f;
        _remaining[key] = (root, remaining, clock);
        return step;
    }

    /// <summary>
    /// Every armed loop and owned layer, each tick. The sequencer ends a slot
    /// timeline (and moves on to the next row) by the first of: finalization (all
    /// clips done, checked at the start of Advance), its C009s having no more than
    /// +0x26E frames left (the pre-end notify, FUN_141BFF3A0, armed while the lead
    /// is >= 0 and the play info's +0x30 isn't -1; it runs every frame, paused or
    /// not, so a paused layer scrubbed into its last frames ended too), or
    /// completion. For these layers the notify is switched off (+0x26E = -1, the
    /// original kept and restored when Poser lets go) and Poser decides: a loop
    /// wraps just before the seam and blends back over the lead; any other
    /// one-shot gets its lead back only while it plays into it, so the game ends
    /// it there as it would, and a held or scrubbed one never ends. Remaining time
    /// counts in the clip's own units, so its decrease is measured per tick for
    /// the margin.
    /// </summary>
    private void PumpLoops(KeyValuePair<(ActorId, int), float>[] scrubbed)
    {
        RestoreLeads();
        if (LoopsSuspended)
            return;
        List<((ActorId Actor, int Slot) Key, ushort Loop)> layers;
        lock (_targetLock)
        {
            if (_loops.Count == 0 && _owned.Count == 0)
                return;
            layers = new List<((ActorId, int), ushort)>(_loops.Count + _owned.Count);
            foreach (var (key, timeline) in _loops)
                layers.Add((key, timeline));
            foreach (var key in _owned.Keys)
                if (!_loops.ContainsKey(key))
                    layers.Add((key, (ushort)0));
        }
        nint schedule = _engine.ScheduleManagement;
        float frames = schedule != 0 ? *(float*)(schedule + 0x50) : 1f;
        if (!(frames > 0f))
            return;
        foreach (var (key, loop) in layers)
        {
            if (Array.Exists(scrubbed, t => t.Key.Equals(key)))
                continue;
            var character = _native.Resolve(key.Actor, out _);
            if (character == null || _restores.ContainsKey((nint)character))
                continue;
            var sequencer = &character->Timeline.TimelineSequencer;
            ushort live = AnimationNativeState.RequestedId(sequencer, key.Slot);
            bool owned;
            lock (_targetLock)
                owned = _owned.ContainsKey(key);
            if (owned && !TrackOwned(key, character, sequencer, live))
                continue;
            if (live == 0 || (loop != 0 && live != loop) || !Supports(character, key.Slot))
                continue;
            nint root = AnimationNativeState.SlotTimeline(sequencer, key.Slot);
            short lead = HoldLead(key, root);
            float speed = TimelineState.Of(root).Speed;
            if (loop == 0)
            {
                // A one-shot (+0x58 == 1; a looping row never ends), not an emote's
                // intro with its loop queued behind it, and only where the game
                // has a pre-end notify (otherwise it ends at finalization anyway).
                if (!TimelineState.Of(root).OneShot || (key.Slot == 0 && IntroQueued(sequencer, live)))
                    continue;
                nint info = TimelineSeekEngine.PlayInfo(root);
                if (lead < 0 || info == 0 || *(int*)(info + 0x30) == -1)
                    continue;
                bool release = false;
                if (speed > 0f)
                {
                    float left = _engine.Remaining(root);
                    float step = left >= 0f ? MeasureStep(key, root, left, frames * speed) : 0f;
                    release = left >= 0f && left - LoopLeadSteps * Math.Max(step, 0f) <= lead;
                }
                *(short*)(root + 0x26E) = release ? lead : (short)-1;
                continue;
            }
            if (!(speed > 0f))
                continue;
            float begin = TimelineState.Of(root).Begin, end = TimelineState.Of(root).End;
            if (!(end > begin))
                end = begin + TimelineState.Of(root).Length;
            nint playInfo = TimelineSeekEngine.PlayInfo(root);
            bool armed = lead >= 0 && playInfo != 0 && *(int*)(playInfo + 0x30) != -1;
            float remaining = armed ? _engine.Remaining(root) : -1f;
            // Nothing runs past its own end, and a timeline whose every clip is done
            // ends on its next Advance (finalization).
            bool atEnd = TimelineState.Of(root).Clock + frames * speed * LoopLeadSteps >= end - 1f
                || _engine.Finalized(root);
            if (!atEnd && remaining >= 0f)
            {
                float step = MeasureStep(key, root, remaining, frames * speed);
                // A loop wraps at the seam the game would blend out at. No measured
                // step yet: only once it is reached.
                atEnd = remaining - LoopLeadSteps * Math.Max(step, 0f) <= lead;
            }
            if (!atEnd)
                continue;
            _remaining.Remove(key);
            var work = new TimelineSeekWork();
            var refused = _engine.Seek((nint)character, root, begin, work, blendFrames: Math.Max((int)lead, 0));
            if (refused != null)
            {
                LogOnce($"loop:{key}:{refused}", $"[TimelineSeek] loop of slot {key.Slot}: {refused}");
                continue;
            }
            _work[((nint)character, key.Slot)] = work;
            if (lead > 0)
                LogOnce($"loopblend:{loop}:{_engine.LastBlendPath}",
                    $"[TimelineSeek] loop of {loop}: {lead}-frame crossfade via {_engine.LastBlendPath}");
        }
    }

    /// <summary>The layer's own lead, with the game's pre-end notify switched off
    /// on its timeline (once per slot timeline: a replay is a new one).</summary>
    private short HoldLead((ActorId Actor, int Slot) key, nint root)
    {
        if (_leads.TryGetValue(key, out var held) && held.Root == root)
            return held.Original;
        short original = *(short*)(root + 0x26E);
        _leads[key] = (root, TimelineState.Of(root).Identity, original);
        if (original >= 0)
            *(short*)(root + 0x26E) = -1;
        return original;
    }

    /// <summary>Released layers get their timeline's own lead back, so it ends as
    /// the game would. Skipped once the slot runs another timeline.</summary>
    private void RestoreLeads()
    {
        (ActorId Actor, int Slot)[] keys;
        lock (_targetLock)
        {
            if (_leadRestores.Count == 0)
                return;
            keys = _leadRestores.ToArray();
            _leadRestores.Clear();
        }
        foreach (var key in keys)
        {
            lock (_targetLock)
                if (_loops.ContainsKey(key) || _owned.ContainsKey(key))
                    continue;
            if (!_leads.Remove(key, out var held))
                continue;
            var character = _native.Resolve(key.Actor, out _);
            if (character == null)
                continue;
            if (AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, key.Slot) == held.Root
                && TimelineState.Of(held.Root).Identity == held.Identity)
                *(short*)(held.Root + 0x26E) = held.Original;
        }
    }

    /// <summary>Slot 0 is still in an emote's intro: PlayActionTimeline queued its
    /// loop (seq+0x152) and the live row isn't it yet.</summary>
    private static bool IntroQueued(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, ushort live)
    {
        ushort queued = AnimationNativeState.QueuedId(sequencer);
        return queued != 0 && queued != live;
    }

    // ── Speed edges ───────────────────────────────────────────────────

    /// <summary>Before a new timeline replaces a PAUSED slot (speed 0): the old
    /// one stops with fade 0 and the new one starts without a blend-in.</summary>
    internal void PrepareInstantSwap(Character* character, int slot)
    {
        if (!Available || character == null)
            return;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, slot);
        if (root == 0 || TimelineState.Of(root).Speed != 0f)
            return;
        *(uint*)(root + 0x220) = 0;
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

    // The effective speed last given to a held layer's slot timeline.
    private readonly Dictionary<nint, float> _heldSpeeds = new();

    /// <summary>
    /// Holds layer speeds natively, every tick: the slot timeline at layer ×
    /// overall (its C009s carry it to their controls; change-guarded, so a steady
    /// hold costs nothing), the controls of the props the layer summons, and its
    /// effects at 0 when it is paused (the C012 speed handler drops 0). A replayed
    /// timeline is a new one and takes the speed on its first tick.
    /// </summary>
    internal void HoldSlotSpeeds(Character* character, IReadOnlyDictionary<int, float> speeds, float overall)
    {
        if (!Available || character == null || speeds.Count == 0)
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
                    if (_weaponSpeedHook == null)
                        // No hook: the push resets the prop timeline every frame; hold
                        // what is rendered (its controls) at least.
                        TimelineSeekEngine.SetWeaponControlSpeeds(weapon, effective);
                    // Directly as well: a speed edge can land after this frame's push,
                    // which would leave the prop a frame on the old speed (half a frame
                    // off at x0.5). The summoned prop gets no push at all. Change-
                    // guarded, so the steady per-frame write costs nothing.
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
                if (!_heldSpeeds.TryGetValue(root, out var last) || last != effective)
                {
                    if (effective == 0f)
                        _engine.SetVfxSpeeds(root, 0f);
                    _heldSpeeds[root] = effective;
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
        if (!Available || character == null)
            return;
        try
        {
            nint address = (nint)character;
            if (overall != 1f)
                _engine.SetStatusVfxSpeed(address, overall);
            bool mounted = *(ushort*)(address + 0x688) != 0 && !slotSpeeds.ContainsKey(0);
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
            *((byte*)&((Character*)address)->Timeline + 0x34E) |= 4;
        }
    }

    /// <summary>Holds layer weights (see TimelineSeekEngine.SetSlotWeight).</summary>
    internal void ApplySlotWeights(Character* character, IReadOnlyDictionary<int, float> weights)
    {
        if (character == null || weights.Count == 0)
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
        if (character == null)
            return;
        try { _engine.SetActorVfxSpeeds(&character->Timeline.TimelineSequencer, (nint)character, overall); }
        catch (Exception ex) { LogOnce("vfxspeed", $"[TimelineSeek] VFX speed edge faulted: {ex.Message}"); }
    }

    /// <summary>Same-tick speed edge for the actor's prop timelines.</summary>
    internal void ApplyPropSpeed(Character* character, float overall)
    {
        if (character == null)
            return;
        try { _engine.SetPropSpeeds((nint)character, overall); }
        catch (Exception ex) { LogOnce("propspeed", $"[TimelineSeek] prop speed edge faulted: {ex.Message}"); }
    }

    private void LogOnce(string key, string message)
    {
        if (_logged.Add(key))
            _log.Information(message);
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        // Unload is not guaranteed to run on the framework thread: let an
        // in-flight DisableDraw leave the detour before freeing it.
        _weaponSpeedHook?.Disable();
        _applyRequestsHook?.Disable();
        _fadeMaskHook?.Disable();
        if (_disableDrawHook != null)
            _disableDrawHook.Disable();
        if (_disableDrawHook != null || _weaponSpeedHook != null || _applyRequestsHook != null || _fadeMaskHook != null)
            System.Threading.Thread.Sleep(100);
        _disableDrawHook?.Dispose();
        _weaponSpeedHook?.Dispose();
        _applyRequestsHook?.Dispose();
        _fadeMaskHook?.Dispose();
        lock (_targetLock)
        {
            _targets.Clear();
            foreach (var key in _loops.Keys)
                _leadRestores.Add(key);
            foreach (var key in _owned.Keys)
                _leadRestores.Add(key);
            _loops.Clear();
            _owned.Clear();
            _endedSlots.Clear();
        }
        // Best effort: unload may not run on the framework thread.
        try { RestoreLeads(); } catch { }
        _work.Clear();
        _restores.Clear();
    }
}
