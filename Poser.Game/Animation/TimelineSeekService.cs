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

    private delegate void DisableDrawFn(nint character);
    private readonly Hook<DisableDrawFn>? _disableDrawHook;

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
        }
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
        // Measured once per slot timeline, before this service has synced it: the
        // clip sync leaves LocalTime a fraction (its ε) short of natural, so a
        // re-measure after a seek would carry that fraction into the next target.
        if (!_offsets.TryGetValue(key, out var mapping) || mapping.Root != root)
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
            _managed.Clear();
            foreach (var actor in _actors.Actors)
                if (actor.Address != 0 && ((CSGameObject*)actor.Address)->IsCharacter())
                    _managed.Add(actor.Address);

            PumpRestores();
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
    }

    // ── Redraw restore ────────────────────────────────────────────────

    /// <summary>
    /// Entry of Character::DisableDraw (every redraw path: Penumbra, Glamourer,
    /// the game's own hide). At entry the slot ids, handles and clocks are intact;
    /// the function then destroys the props and stops every slot. Only drawn
    /// (+0x9A bit 7) Poser actors; never throws into the game.
    /// </summary>
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
            ushort id = *(ushort*)((byte*)sequencer + 0xE0 + 2 * slot);
            if (timeline == 0 || id == 0 || !TimelineSeekEngine.IsLoaded(timeline)
                || TimelineSeekEngine.IsIntegerFrame(timeline) || (*(byte*)(timeline + 0x7C) & 0x40) != 0)
                continue;
            slots.Add((slot, id, TimelineSeekEngine.Clock(timeline)));
        }
        var go = (CSGameObject*)character;
        if (slots.Count > 0)
            _restores[character] = new Restore(slots.ToArray(), go->ObjectIndex, go->EntityId);
    }

    /// <summary>
    /// After the redraw: wait for the new draw object and the game's base
    /// timeline, replay each recorded id (no blend), wait until it is loaded,
    /// then seek it to the recorded clock. The seek's deferred work re-fires a
    /// C031 whose start gave up while the new weapon object loaded, and seeks it.
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
            // The replay's C009s start only once the new CharacterBase has its
            // skeleton (cb+0xA0) and partials.
            if (go->RenderFlags != 0 || go->DrawObject == null
                || ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)go->DrawObject)->Skeleton == null)
                continue;
            var chara = (Character*)character;
            var sequencer = &chara->Timeline.TimelineSequencer;
            if (!restore.Replayed)
            {
                nint baseTimeline = AnimationNativeState.SlotTimeline(sequencer, 0);
                if (baseTimeline == 0 || !TimelineSeekEngine.IsLoaded(baseTimeline))
                    continue;
                _engine.SetNoBlendStart(baseTimeline);
                foreach (var (_, id, _) in restore.Slots)
                {
                    var played = _native.PlayTimeline(chara, id);
                    if (!played.Success)
                        LogOnce($"restore-play:{id}", $"[TimelineSeek] redraw restore could not replay {id}: {played.Detail}");
                }
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
                ushort live = *(ushort*)((byte*)sequencer + 0xE0 + 2 * slot);
                if (timeline == 0 || live != id || !TimelineSeekEngine.IsLoaded(timeline))
                {
                    all = false;
                    continue;
                }
                if (!restore.SpeedLogged)
                {
                    restore.SpeedLogged = true;
                    LogOnce($"restore-speed:{*(float*)(timeline + 0x240)}",
                        $"[TimelineSeek] redraw restore: replayed timeline born at speed {*(float*)(timeline + 0x240)}");
                }
                var work = new TimelineSeekWork { AllowFire = true };
                var refused = _engine.Seek(character, timeline, clock, work);
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

    // ── Speed edges ───────────────────────────────────────────────────

    /// <summary>Same-tick speed edge for one slot timeline (see
    /// TimelineSeekEngine.SetSlotTimelineSpeed).</summary>
    internal void ApplySlotSpeed(
        FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer* sequencer, int slot, float speed)
    {
        try { _engine.SetSlotTimelineSpeed(sequencer, slot, speed); }
        catch (Exception ex) { LogOnce("slotspeed", $"[TimelineSeek] slot speed edge faulted: {ex.Message}"); }
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
        if (_disableDrawHook != null)
        {
            _disableDrawHook.Disable();
            System.Threading.Thread.Sleep(100);
            _disableDrawHook.Dispose();
        }
        lock (_targetLock)
            _targets.Clear();
        _work.Clear();
        _restores.Clear();
    }
}
