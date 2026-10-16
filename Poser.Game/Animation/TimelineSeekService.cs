using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

/// <summary>
/// Runs native timeline seeks (#533) on the framework tick. The scrub UI calls
/// from the ImGui draw callback; it only records the latest target per
/// actor/slot, and the seek runs once on the next tick (coalesced), followed by
/// that seek's deferred frames.
/// </summary>
public sealed unsafe class TimelineSeekService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private readonly TimelineSeekEngine _engine;
    // Written by the UI (draw callback), drained by the framework tick.
    private readonly object _targetLock = new();
    private readonly Dictionary<(ActorId Actor, int Slot), float> _targets = new();
    private readonly Dictionary<(ActorId Actor, int Slot), TimelineSeekWork> _work = new();
    // Havok control seconds → timeline frames, captured while the actor is
    // settled (no deferred work): a mid-drag LocalTime can lag one sync.
    private readonly Dictionary<(ActorId Actor, int Slot), (nint Root, float Offset)> _offsets = new();
    private readonly HashSet<string> _logged = new();
    private readonly Poser.Game.Services.IActorManager _actors;
    // Last seen current prop timeline per (character, weapon slot); a change is a birth.
    private readonly Dictionary<(nint Character, int Weapon), nint> _propTimelines = new();

    public TimelineSeekService(
        IFramework framework, ISigScanner scanner, IPluginLog log, AnimationNativeState native,
        Poser.Game.Services.IActorManager actors)
    {
        _actors = actors;
        _framework = framework;
        _log = log;
        _native = native;
        var natives = new TimelineSeekNatives(scanner, log);
        var layout = new TimelineSeekLayout(scanner, log);
        _engine = new TimelineSeekEngine(natives, layout);
        _framework.Update += OnUpdate;
    }

    public bool Available => _engine.Ready;

    /// <summary>
    /// Whether the native seek can move this actor's slot right now (a live,
    /// loaded, float-frame timeline). False keeps the caller on its fallback.
    /// </summary>
    internal bool Supports(Character* character, int slot)
    {
        if (!Available || character == null)
            return false;
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, slot);
        return root != 0 && TimelineSeekEngine.IsLoaded(root) && !TimelineSeekEngine.IsIntegerFrame(root)
            && (*(byte*)(root + 0x7C) & 0x40) == 0;
    }

    /// <summary>Records the latest scrub target in Havok control seconds.</summary>
    internal void Request(ActorId actor, Character* character, AnimationSlot slot, float seconds, float controlLocalTime)
    {
        int index = (int)slot;
        var key = (actor, index);
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, index);
        if (root == 0)
            return;
        if (!_offsets.TryGetValue(key, out var mapping) || mapping.Root != root || !_work.ContainsKey(key))
            mapping = (root, TimelineSeekEngine.Clock(root) - controlLocalTime * 30f);
        _offsets[key] = mapping;
        lock (_targetLock)
            _targets[key] = seconds * 30f + mapping.Offset;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!_engine.Ready)
            return;
        try
        {
            CorrectPropBirths();
            KeyValuePair<(ActorId, int), float>[] targets;
            lock (_targetLock)
            {
                targets = new KeyValuePair<(ActorId, int), float>[_targets.Count];
                ((ICollection<KeyValuePair<(ActorId, int), float>>)_targets).CopyTo(targets, 0);
                _targets.Clear();
            }
            foreach (var (key, target) in targets)
                RunSeek(key, target);

            List<(ActorId, int)>? done = null;
            foreach (var (key, work) in _work)
            {
                var character = _native.Resolve(key.Actor, out _);
                nint current = character == null
                    ? 0
                    : AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, key.Slot);
                if (work.Tick(_engine, current))
                    continue;
                if (work.Log != null)
                    _log.Information($"[TimelineSeek] {key.Actor} slot {key.Slot}: {work.Log}");
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
        }
    }

    /// <summary>One forward correction for each newly current prop timeline of
    /// Poser's actors; skipped for an actor whose seek is still in flight (the
    /// seek steps its props itself).</summary>
    private void CorrectPropBirths()
    {
        HashSet<nint>? seeking = null;
        foreach (var work in _work.Values)
            (seeking ??= new()).Add(work.Character);
        foreach (var actor in _actors.Actors)
        {
            nint character = actor.Address;
            if (character == 0 || !((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)character)->IsCharacter())
                continue;
            for (int w = 0; w < 4; w++)
            {
                var key = (character, w);
                nint weapon = TimelineSeekEngine.WeaponAt(character, w);
                nint group = _engine.PropGroup(weapon);
                nint current = group == 0 ? 0 : *(nint*)(group + 0xA48) is var h and not 0 ? *(nint*)h : 0;
                _propTimelines.TryGetValue(key, out var last);
                if (current == last)
                    continue;
                if (current == 0 || seeking?.Contains(character) == true)
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
        _work[key] = work;
    }

    /// <summary>Same-tick speed edge for one slot timeline (see
    /// TimelineSeekEngine.SetSlotTimelineSpeed).</summary>
    internal void ApplySlotSpeed(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, int slot, float speed)
    {
        try { _engine.SetSlotTimelineSpeed(sequencer, slot, speed); }
        catch (Exception ex) { LogOnce("slotspeed", $"[TimelineSeek] slot speed edge faulted: {ex.Message}"); }
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
        lock (_targetLock)
            _targets.Clear();
        _work.Clear();
    }
}
