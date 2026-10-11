using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Application.Animation;
using Poser.Domain;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

/// <summary>Havok control enumeration and the scrub's clock writes.</summary>
public sealed unsafe class AnimationScrubRuntimePort : IAnimationScrubPort
{
    private readonly IPluginLog _log;
    private readonly AnimationNativeState _native;
    private int _scrubLogCounter;

    public AnimationScrubRuntimePort(IPluginLog log, AnimationNativeState native)
    {
        _log = log;
        _native = native;
    }

    private static void PropagateScrubToProps(
        Character* character, float before, float duration, float delta)
    {
        AnimationNativeState.ForEachPropControl(character, control =>
        {
            if (!AnimationNativeState.TracksControl(control, before, duration, out var propDuration))
                return;
            float target = control->hkaAnimationControl.LocalTime + delta;
            control->hkaAnimationControl.LocalTime =
                Math.Clamp(target, 0f, Math.Max(0f, propDuration - 1f / 30f));
        });
    }

    /// <summary>Walks a track controller's tracks and clips; every
    /// ChildTimelineClip gets its frame cursor written and its child
    /// controller seeked (timestamp + previous), recursively.</summary>
    private static void SeekChildTimelines(nint trackController, float frames, int depth)
    {
        if (trackController == 0 || depth > 3)
            return;
        nint trackPointers = *(nint*)(trackController + 0x28);
        int trackCount = *(ushort*)(trackController + 0x28 + 0xA);
        for (int t = 0; t < trackCount && t < 8 && trackPointers != 0; t++)
        {
            nint track = *(nint*)(trackPointers + t * 8);
            if (track == 0)
                continue;
            nint clipPointers = *(nint*)(track + 0x18);
            int clipCount = *(ushort*)(track + 0x18 + 0xA);
            for (int c = 0; c < clipCount && c < 8 && clipPointers != 0; c++)
            {
                nint clip = *(nint*)(clipPointers + c * 8);
                if (clip == 0 || !AnimationNativeState.RegionReadable(clip, 0x160) || *(int*)(clip + 0x84) != 7)
                    continue;
                *(float*)(clip + 0xCC) = frames;   // ChildFrame
                *(float*)(clip + 0xD0) = frames;   // PrevChildFrame
                // The child controller: +0x138 in this client (Ktisis says
                // +0x130; the addressed hunt proved +0x138 -> +0x34/38).
                nint child = *(nint*)(clip + 0x138);
                if (child == 0 || !AnimationNativeState.RegionReadable(child, 0x80))
                    child = *(nint*)(clip + 0x130);
                if (child == 0 || !AnimationNativeState.RegionReadable(child, 0x80))
                    continue;
                *(float*)(child + AnimationNativeState.SchedulerTimestampOffset) = frames;
                *(float*)(child + AnimationNativeState.SchedulerTimestampOffset + 4) = frames;
                SeekChildTimelines(*(nint*)(child + 0x18), frames, depth + 1);
            }
        }
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        return character == null
            ? Array.Empty<ScrubControlReading>()
            : AnimationNativeState.CollectControls(character, out token);
    }

    /// <summary>Resolves the verified Base and Upper Body slot controls.</summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        if (character == null || slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
            return null;
        int index = (int)slot;
        if (character->Timeline.TimelineSequencer.TimelineIds[index] == 0)
            return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return null;
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return null;
        var skeleton = charaBase->Skeleton;
        token = CurrentToken(skeleton);
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null || index >= animated->AnimationControls.Length)
                continue;
            var control = animated->AnimationControls[index].Value;
            if (control == null)
                continue;
            var binding = control->hkaAnimationControl.Binding;
            if (binding.ptr == null || binding.ptr->Animation.ptr == null ||
                binding.ptr->Animation.ptr->Duration <= 0f)
                continue;
            return new ScrubControlReading(
                new ScrubControlId(p, index),
                control->hkaAnimationControl.LocalTime,
                binding.ptr->Animation.ptr->Duration,
                control->PlaybackSpeed);
        }
        return null;
    }

    public Outcome SetControlTime(
        ActorId actor, ScrubControlId control, float time, ulong token)
    {
        var character = _native.Resolve(actor, out var detail);
        if (character == null)
            return Outcome.Fail(detail!);
        if (!float.IsFinite(time))
            return Outcome.Fail("Scrub time must be a finite number.");

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return Outcome.Fail("Actor has no character skeleton.");
        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return Outcome.Fail("Actor has no character skeleton.");
        var skeleton = charaBase->Skeleton;

        if (control.Partial < 0 || control.Partial >= skeleton->PartialSkeletonCount)
            return Outcome.Fail("Scrub target no longer exists.");
        var partial = &skeleton->PartialSkeletons[control.Partial];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if (animated == null ||
            control.Control < 0 || control.Control >= animated->AnimationControls.Length)
            return Outcome.Fail("Scrub target no longer exists.");
        var target = animated->AnimationControls[control.Control].Value;
        if (target == null)
            return Outcome.Fail("Scrub target no longer exists.");
        var binding = target->hkaAnimationControl.Binding;
        if (binding.ptr == null || binding.ptr->Animation.ptr == null)
            return Outcome.Fail("Scrub target no longer exists.");

        // Re-derive the token from the live skeleton: a replacement moves
        // the skeleton or changes the control count, and the write is
        // refused rather than landing on whatever now occupies the slot.
        if (token != 0 && token != CurrentToken(skeleton))
            return Outcome.Fail("Skeleton changed; scrub cancelled.");

        float duration = binding.ptr->Animation.ptr->Duration;
        // Never PLACE a cursor on the last frame: a child timeline set
        // exactly at its end fires its end events during the drag (prop
        // released before Play) but never completes — completion needs the
        // child to CROSS the end during an update — so the parent runs its
        // own tail and freezes (c0 stuck at 665 with the child pinned at
        // 585, 2026-09-01 22:03). One frame short, Play crosses it properly.
        float lastFrame = Math.Max(0f, duration - 1f / 30f);
        time = Math.Clamp(time, 0f, lastFrame);
        float before = target->hkaAnimationControl.LocalTime;
        target->hkaAnimationControl.LocalTime = time;
        // Props: an attached weapon/prop skeleton control that runs on the
        // same clock as this control (same clip length within a second, and
        // its time within a second of ours — the bubble wand reads
        // 19.33/19.50 with a fixed 0.18s lead-in) moves by the same delta,
        // keeping its offset. Measured 2026-09-01 22:27 through the bridge.
        PropagateScrubToProps(character, before, duration, time - before);
        // EVERY partial runs its own control for the same slot (body,
        // face, hair). Scrubbing only one left the others on the old
        // schedule — one of them reaches its clip's end at the old time
        // and the timeline layer resets the whole animation (the
        // pause→scrub→play reset, 2026-09-01). Same index, same time,
        // all partials, each clamped to its own clip.
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            if (p == control.Partial)
                continue;
            var sibling = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (sibling == null || control.Control >= sibling->AnimationControls.Length)
                continue;
            var siblingControl = sibling->AnimationControls[control.Control].Value;
            if (siblingControl == null)
                continue;
            var siblingBinding = siblingControl->hkaAnimationControl.Binding;
            float siblingClip =
                siblingBinding.ptr != null && siblingBinding.ptr->Animation.ptr != null
                    ? siblingBinding.ptr->Animation.ptr->Duration
                    : duration;
            siblingControl->hkaAnimationControl.LocalTime =
                Math.Clamp(time, 0f, siblingClip);
        }
        // The SCHEDULER's clock moves with the scrub — without this the
        // timeline layer keeps counting from the old position and resets
        // the animation on the old schedule. It counts in 30fps FRAMES
        // (dump-proven: control 1.42s ↔ clock 42.74), not seconds.
        var timestamp = AnimationNativeState.SchedulerTimestamp(
            &character->Timeline.TimelineSequencer, control.Control);
        if (timestamp != null)
        {
            *timestamp = Math.Clamp(time, 0f, duration) * 30f;
            // The previous-timestamp twin too: leaving it at the old value
            // made the next tick's delta enormous — the one-frame "way
            // faster" blip after a scrub.
            *(timestamp + 1) = *timestamp;
            nint schedulerForLog = (nint)timestamp - AnimationNativeState.SchedulerTimestampOffset;
            if (++_scrubLogCounter % 45 == 1)
                _log.Information(
                    $"[AnimState] scrub {time:0.00}s -> {*timestamp:0.0}f; "
                    + $"end candidates: sched+68={*(int*)(schedulerForLog + 0x68)} "
                    + $"clipDuration={duration * 30f:0.0}f");
            // THE CLOCK FAMILY (clock hunt with addresses, 22:19): during
            // natural play six timestamp pairs mirror each other in FRAMES —
            // scheduler +0x34/38, track controller +0x18C/190, track
            // +0x11C/120, ChildTimelineClip +0xCC/D0, and the child
            // controller (clip+0x138 ->) +0x34/38. A scrub that leaves ANY
            // of them behind lets that one gate completion on the old
            // schedule (the forward-scrub stall). tctl+0x11C / track+0xAC /
            // clip+0x5C are per-tick DELTA fields, not clocks — untouched.
            float frames = Math.Clamp(time, 0f, duration) * 30f;
            nint schedulerObject = (nint)timestamp - AnimationNativeState.SchedulerTimestampOffset;
            nint trackController = *(nint*)(schedulerObject + 0x18);
            if (trackController != 0 && AnimationNativeState.RegionReadable(trackController, 0x1A0))
            {
                *(float*)(trackController + 0x18C) = frames;
                *(float*)(trackController + 0x190) = frames;
                nint trackPointers = *(nint*)(trackController + 0x28);
                int trackCount = *(ushort*)(trackController + 0x28 + 0xA);
                for (int trackIndex = 0;
                    trackIndex < trackCount && trackIndex < 8 && trackPointers != 0;
                    trackIndex++)
                {
                    nint track = *(nint*)(trackPointers + trackIndex * 8);
                    if (track == 0 || !AnimationNativeState.RegionReadable(track, 0x130))
                        continue;
                    *(float*)(track + 0x11C) = frames;
                    *(float*)(track + 0x120) = frames;
                }
            }
            // THE CHILD TIMELINE: the slot's timeline is a PARENT whose clip
            // (ClipType 7, Ktisis ChildTimelineClip) runs a child
            // TimelineController with its own frame clock. Scrubbing only
            // the parent left the child on the old position — the end is
            // judged there, hence "dies on the old schedule" (2026-09-01).
            SeekChildTimelines(trackController, frames, 0);
        }
        return Outcome.Ok();
    }

    private static ulong CurrentToken(
        FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* skeleton)
    {
        int count = 0;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var animated = partial->GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                var binding = control->hkaAnimationControl.Binding;
                if (binding.ptr == null || binding.ptr->Animation.ptr == null)
                    continue;
                count++;
            }
        }
        return unchecked(((ulong)(nint)skeleton * 397) ^ (ulong)count);
    }
}
