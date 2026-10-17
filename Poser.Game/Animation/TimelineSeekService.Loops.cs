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

/// <summary>Loops and owned layers: wraps with a crossfade, one-shots ended as the game would.</summary>
public sealed unsafe partial class TimelineSeekService
{
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

    // A loop whose row has no pre-end lead, or a lead of 0 (a one-shot never made to loop), wraps this
    // many frames before its end with a crossfade of the same length, the game's seam
    // between emote rows: at the end itself its controls are already parked on the
    // last frame, and a cut snaps to the first. (The game's own length for a row pair
    // is its blend table, MotionTimelineBlendTable.)
    private const int NoLeadLoopBlendFrames = 12;

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
            // +0x40 counts the TMB's padding: the timeline finishes when its last
            // animation does, so the seam is that animation's end (as the scrub reads it).
            var poseTree = _engine.Collect(root);
            if (poseTree.PoseEnd > begin + 1f && !poseTree.PoseEndPending)
                end = Math.Min(end, poseTree.PoseEnd);
            nint playInfo = TimelineSeekEngine.PlayInfo(root);
            // A lead of 0 (the switch at the very end) is no seam either: the lead only
            // says when the game switches, not how long it blends.
            bool hasLead = lead > 0;
            bool armed = hasLead && playInfo != 0 && *(int*)(playInfo + 0x30) != -1;
            float remaining = armed ? _engine.Remaining(root) : -1f;
            int blend = hasLead ? lead : NoLeadLoopBlendFrames;
            // Nothing runs past its own end, and a timeline whose every clip is done
            // ends on its next Advance (finalization). Without a lead the seam is the
            // blend's length before the end.
            bool atEnd = TimelineState.Of(root).Clock + frames * speed * LoopLeadSteps >= end - (hasLead ? 1f : blend)
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
            var refused = _engine.Seek((nint)character, root, begin, work, blendFrames: blend);
            if (refused != null)
            {
                LogOnce($"loop:{key}:{refused}", $"[TimelineSeek] loop of slot {key.Slot}: {refused}");
                continue;
            }
            _work[((nint)character, key.Slot)] = work;
            if (blend > 0)
                LogOnce($"loopblend:{loop}:{_engine.LastBlendPath}",
                    $"[TimelineSeek] loop of {loop}: {blend}-frame crossfade via {_engine.LastBlendPath}");
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
}
