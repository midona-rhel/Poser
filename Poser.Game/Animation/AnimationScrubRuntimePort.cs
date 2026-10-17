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
    private readonly TimelineSeekService? _seek;
#if DEBUG
    /// <summary>Debug bridge: scrub Base and Upper Body through the clock-writing
    /// fallback even where the native seek could move them.</summary>
    public static bool ForceFallback;
    private bool SeekAllowed => !ForceFallback;
#else
    private const bool SeekAllowed = true;
#endif

    public AnimationScrubRuntimePort(IPluginLog log, AnimationNativeState native, TimelineSeekService? seek = null)
    {
        _log = log;
        _native = native;
        _seek = seek;
    }

    public IReadOnlyList<ScrubControlReading> EnumerateControls(ActorId actor, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return Array.Empty<ScrubControlReading>();
        var controls = AnimationNativeState.CollectControls(character, out token);
        if (_seek == null)
            return controls;
        // Layers without a fixed Havok control are scrubbed by their timeline clock.
        var all = new List<ScrubControlReading>(controls);
        foreach (var slot in TimelineScrubSlots)
            if (_seek.TimelineReading(character, slot) is { } reading)
                all.Add(reading);
        return all;
    }

    /// <summary>The layers the native seek scrubs by their own timeline clock: all of
    /// them where it can. Base and Upper Body fall back to their Havok control only
    /// without it (that control is found by index, and a seek's re-created controls
    /// reorder the array: Upper Body then read Full body's time).</summary>
    private static readonly AnimationSlot[] TimelineScrubSlots =
        [AnimationSlot.Base, AnimationSlot.UpperBody, AnimationSlot.Facial, AnimationSlot.Additive, AnimationSlot.Lips,
         AnimationSlot.Parts1, AnimationSlot.Parts2, AnimationSlot.Parts3, AnimationSlot.Parts4, AnimationSlot.Overlay];

    /// <summary>Resolves the verified Base and Upper Body slot controls.</summary>
    public ScrubControlReading? FindSlotControl(
        ActorId actor, AnimationSlot slot, out ulong token)
    {
        token = 0;
        var character = _native.Resolve(actor, out _);
        if (character == null)
            return null;
        if (SeekAllowed && _seek?.TimelineReading(character, slot) is { } timeline)
            return timeline;
        if (slot is not (AnimationSlot.Base or AnimationSlot.UpperBody))
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
        if (control.IsTimeline)
        {
            var slot = (AnimationSlot)control.Control;
            if (_seek == null)
                return Outcome.Fail("This layer's animation can no longer be scrubbed.");
            if (!_seek.Supports(character, (int)slot))
                // A layer just played again loads first; its target waits for it.
                return _seek.RequestTimelineWhenLoaded(actor, character, slot, time)
                    ? Outcome.Ok()
                    : Outcome.Fail("This layer's animation can no longer be scrubbed.");
            _seek.RequestTimeline(actor, character, slot, time);
            return Outcome.Ok();
        }

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

        // #533: the game's own scheduler seek when available (same frame, both
        // directions, props and VFX enter as in playback); the clock writes below
        // remain the fallback for anything it refuses.
        if (SeekAllowed && _seek != null && control.Control is 0 or 1 && _seek.Supports(character, control.Control))
        {
            _seek.Request(actor, character, (AnimationSlot)control.Control, time, target->hkaAnimationControl.LocalTime);
            return Outcome.Ok();
        }

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
        // The slot's props move by the same delta: the props its C031s summon, found
        // in the game's prop group (TimelineSeekEngine.ShiftPropControls).
        nint root = AnimationNativeState.SlotTimeline(&character->Timeline.TimelineSequencer, control.Control);
        var engine = _seek is { Available: true } seek ? seek.Engine : null;
        if (engine != null && root != 0)
            engine.ShiftPropControls((nint)character, root, time - before);
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
        // The timeline clocks move with the scrub, in 30 fps frames: left behind, the
        // timeline keeps counting from the old position and resets the animation on
        // the old schedule. The engine writes the timeline, its TMB actors and tracks,
        // and every child timeline on its own clock; without the seek's layout only the
        // slot timeline's own clock is moved.
        float frames = Math.Clamp(time, 0f, duration) * 30f;
        if (root != 0 && engine != null)
            engine.WriteClocks(root, frames);
        else if (AnimationNativeState.SchedulerTimestamp(&character->Timeline.TimelineSequencer, control.Control)
                 is var timestamp && timestamp != null)
        {
            *timestamp = frames;
            *(timestamp + 1) = frames;
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
