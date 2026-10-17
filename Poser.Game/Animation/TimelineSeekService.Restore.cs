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

/// <summary>The redraw restore: a redrawn actor comes back on the frame it was on.</summary>
public sealed unsafe partial class TimelineSeekService
{
    // ── Redraw restore ────────────────────────────────────────────────

    /// <summary>
    /// Entry of Character::DisableDraw (every redraw path: Penumbra, Glamourer,
    /// the game's own hide). At entry the slot ids, handles and clocks are intact;
    /// the function then destroys the props and stops every slot. Only drawn
    /// (+0x9A bit 7) Poser actors; never throws into the game.
    /// </summary>
    private sealed class Restore
    {
        public readonly (int Slot, ushort Id, float Clock)[] Slots;
        // Slot 0's loop queued behind the intro it was caught on, or 0.
        public ushort QueuedLoop;
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


    private void DisableDrawDetour(nint character)
    {
        try
        {
            // A character with a pending restore counts even if Poser's list dropped
            // it for a moment: the game is calling this on a live object.
            if (character != 0 && (_managed.Contains(character) || _restores.ContainsKey(character))
                && (*(byte*)(character + NativeFields.Character_DrawFlags9A) & 0x80) != 0)
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
        if ((*(byte*)(character + NativeFields.Character_TimelineFlags1CE8) & 0x10) != 0)
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
            if (id == 0)
                continue;
            var state = TimelineState.Of(timeline);
            if (state.IntegerFrame || state.Finished)
                continue;
            // A row still loading (a redraw right after a play) is replayed from its
            // start: an actor in EmoteLoop with no EmoteMode has nothing else that
            // starts its Base again, and its draw never finishes without one.
            slots.Add((slot, id, state.Loaded ? state.Clock : TimelineState.Of(timeline).Exists ? state.Begin : 0f));
        }
        ushort queuedLoop = AnimationNativeState.QueuedLoopBehindIntro(sequencer);
        var go = (CSGameObject*)character;
        _log.Debug($"[TimelineSeek] redraw capture mode {((Character*)character)->Mode}/{((Character*)character)->ModeParam} queued {AnimationNativeState.QueuedLoopBehindIntro(sequencer)}: "
            + string.Join(", ", slots.Select(x => $"slot {x.Item1} row {x.Item2} at {x.Item3:0.#}")));
        if (slots.Count > 0)
            _restores[character] = new Restore(slots.ToArray(), go->ObjectIndex, go->EntityId)
            {
                QueuedLoop = queuedLoop,
            };
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
                    var played = slot == 0 && restore.QueuedLoop != 0
                        ? _native.ReplayIntro(chara, id, restore.QueuedLoop)
                        : _native.ReplayTimeline(chara, id);
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
}
