using System;
using System.Collections.Generic;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>The scrub fallback's clock and prop writes, for a timeline the seek can't move.</summary>
internal sealed unsafe partial class TimelineSeekEngine
{
    // ── Clock writes (the scrub fallback) ─────────────────────────────

    /// <summary>
    /// The scrub fallback for a timeline the seek can't move (an integer-frame clock, a
    /// scheduler-driven actor), the way the game sets a clock (RE: SchedulerTimeline
    /// StartChild): TimelineController::SetCurrentTime on the timeline, then on each
    /// active C010's child at ((parent − start) · scale + offset), recursively. Nothing
    /// else: the TMB actor, track and C010 offsets the old version wrote are not
    /// fields (they landed in whatever the timeline's arena packed next).
    /// </summary>
    public void WriteClocks(nint root, float frames)
    {
        void Write(nint timeline, float frame, int depth)
        {
            if (timeline == 0 || depth > 4)
                return;
            _n.SetCurrentTime(timeline, frame);
            var clips = new List<nint>();
            ClipsOf(timeline, clips);
            foreach (var clip in clips)
            {
                if (_l.Classify(clip) != SeekClipKind.ChildTimeline || (*(byte*)(clip + NativeFields.SchedulerClip_State) & 0x10) == 0
                    || ChildOf(clip) is not (var child and not 0))
                    continue;
                float scale = *(float*)(clip + NativeFields.TimelineClip_Scale);
                if (!(scale > 0f) || !float.IsFinite(scale))
                    scale = 1f;
                nint data = *(nint*)(clip + NativeFields.SchedulerClip_Data);
                float offset = data != 0 && (*(uint*)(data + NativeFields.C010Data_Flags) & 1) != 0 ? *(float*)(data + NativeFields.C010Data_Offset) : 0f;
                Write(child, (frame - *(float*)(clip + NativeFields.SchedulerClip_StartFrame)) * scale + offset, depth + 1);
            }
        }
        Write(root, frames, 0);
    }

    /// <summary>The Havok controls a slot timeline's C009s drive (its tree, C010
    /// children included): the slot's own controls, whatever their array order.</summary>
    public List<nint> SlotControls(nint root)
    {
        var controls = new List<nint>();
        foreach (var clip in Collect(root).Havok.Keys)
            controls.AddRange(ClipControls(clip));
        return controls;
    }

    /// <summary>One cycle of a control, in seconds: the animation minus its crops.</summary>
    internal static float ControlLength(nint control) => CycleLength(control);

    /// <summary>The scrub fallback for props: each prop the slot's C031s summon, found
    /// in the game's prop group (never matched by length), has its Havok controls
    /// moved by the same delta, clamped short of their cycle's end; a control Havok
    /// parked at its last cycle is left.</summary>
    public void ShiftPropControls(nint character, nint root, float delta)
    {
        foreach (var (c031, _) in Collect(root).Summons)
        {
            nint prop = CurrentPropTimeline(PropGroup(PropWeapon(character, c031)), PropTimelineId(c031));
            if (prop == 0)
                continue;
            foreach (var clip in Collect(prop).Havok.Keys)
                foreach (var control in ClipControls(clip))
                {
                    // Parked by Havok at its last cycle: a time alone is parked again.
                    if (HeldByHavok(control))
                        continue;
                    float length = CycleLength(control);
                    float* time = (float*)(control + NativeFields.hkaDefaultAnimationControl_LocalTime);   // hkaDefaultAnimationControl.LocalTime
                    *time = Math.Clamp(*time + delta, 0f, MathF.Max(0f, length - 1f / 30f));
                }
        }
    }
}
