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

/// <summary>
/// Runs native timeline seeks on the framework tick. The scrub UI calls
/// from the ImGui draw callback; it only records the latest target per
/// actor/slot, and the seek runs once on the next tick (coalesced), followed by
/// that seek's deferred frames. It also corrects newly born prop timelines and
/// attempts to restore selected animation clocks after an actor redraw.
/// </summary>
public sealed unsafe partial class TimelineSeekService : IDisposable
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
    private readonly Dictionary<(ActorId Actor, int Slot), (nint Root, (nint Node, uint Id) Identity, float Offset)> _offsets = new();
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
    // drawn character, main thread; also its dirty and init paths).
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
    // +0xF8), run after the timeline step for every control with +0x6CC set.
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
        if ((*(byte*)((nint)character + NativeFields.Character_TimelineFlags1CE8) & 0x10) != 0)
            return false;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, slot);
        var state = TimelineState.Of(root);
        // Any pause bit besides the game's own bit 2 freezes Advance; the seek
        // refuses it, so the caller takes its fallback instead.
        return state.Seekable && !state.IntegerFrame && (state.PauseMask & ~4u) == 0;
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
    /// once the timeline can be seeked (dropped after 300 updates). False when the slot
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
        if (!_offsets.TryGetValue(key, out var mapping) || mapping.Root != root
            || mapping.Identity != TimelineState.Of(root).Identity)
            mapping = (root, TimelineState.Of(root).Identity, TimelineState.Of(root).Clock - controlLocalTime * 30f);
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

    private void LogOnce(string key, string message)
    {
        if (_logged.Add(key))
            _log.Information(message);
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        // Disable hooks before disposing them. The delay gives in-flight calls time
        // to return but does not establish that all detours have completed.
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
