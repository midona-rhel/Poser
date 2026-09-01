using System;
using System.Globalization;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Poser.Domain.Identity;

namespace Poser.Game.Animation;

/// <summary>
/// THE RESET WATCH — a safe, read-only diagnostic for the "plays to the old
/// end and resets" bug. Armed on an actor, it logs every clock in the
/// animation cycle each frame for a bounded window: the havok control
/// LocalTimes, the scheduler CurrentTimestamp (frames), and the completion
/// cursor mirrored at the track-controller / track / clip levels (seconds).
/// The frame a clock jumps BACKWARD names the schedule that still counts on
/// the old position despite the scrub. No hooks, no breakpoints.
/// </summary>
public sealed unsafe partial class AnimationRuntimePort
{
    private ActorId? _resetWatchActor;
    private int _resetWatchTicks;
    private readonly float[] _resetWatchPrev = new float[16];

    /// <summary>Arms the per-frame clock log on this actor for ~6 seconds.</summary>
    public void ProbeWatchReset(ActorId actor)
    {
        _resetWatchActor = actor;
        _resetWatchTicks = 360;
        for (int i = 0; i < _resetWatchPrev.Length; i++)
            _resetWatchPrev[i] = float.NaN;
        _log.Information($"[AnimReset] watch armed on {actor} for 6s.");
    }

    /// <summary>The clip/track/track-controller cursor addresses for a slot,
    /// plus the scheduler timestamp — the same chain the scrub writes, read
    /// only. Returns how many it filled into <paramref name="cursors"/>.</summary>
    private int ResolveResetCursors(
        Character* character, int slot, float** cursors, int max)
    {
        int count = 0;
        var stamp = SchedulerTimestamp(&character->Timeline.TimelineSequencer, slot);
        if (stamp == null)
            return 0;
        if (count < max) cursors[count++] = stamp; // scheduler frames
        nint scheduler = (nint)stamp - SchedulerTimestampOffset;
        nint trackController = *(nint*)(scheduler + 0x18);
        if (trackController == 0)
            return count;
        if (count < max) cursors[count++] = (float*)(trackController + 0x11C);
        nint trackPointers = *(nint*)(trackController + 0x28);
        int trackCount = *(ushort*)(trackController + 0x28 + 0xA);
        for (int t = 0; t < trackCount && t < 4 && trackPointers != 0 && count < max; t++)
        {
            nint track = *(nint*)(trackPointers + t * 8);
            if (track == 0)
                continue;
            if (count < max) cursors[count++] = (float*)(track + 0xAC);
            nint clipPointers = *(nint*)(track + 0x18);
            int clipCount = *(ushort*)(track + 0x18 + 0xA);
            for (int c = 0; c < clipCount && c < 4 && clipPointers != 0 && count < max; c++)
            {
                nint clip = *(nint*)(clipPointers + c * 8);
                if (clip != 0 && count < max)
                    cursors[count++] = (float*)(clip + 0x5C);
            }
        }
        return count;
    }

    private void ProbeResetWatchTick()
    {
        if (_resetWatchActor is not { } actor)
            return;
        if (--_resetWatchTicks <= 0)
        {
            _resetWatchActor = null;
            _log.Information("[AnimReset] watch disarmed.");
            return;
        }
        var character = Resolve(actor, out _);
        if (character == null)
            return;

        // Slot 1 (upper body) is the repro slot; fall back to slot 0.
        int slot = SchedulerTimestamp(&character->Timeline.TimelineSequencer, 1) != null
            ? 1 : 0;
        float** cursorsBuffer = stackalloc float*[12];
        int n = ResolveResetCursors(character, slot, cursorsBuffer, 12);

        // The slot's own havok control time (first partial that has it).
        float controlTime = float.NaN;
        var draw = character->GameObject.DrawObject;
        if (draw != null && draw->Object.GetObjectType() == ObjectType.CharacterBase
            && ((CharacterBase*)draw)->Skeleton != null)
        {
            var skele = ((CharacterBase*)draw)->Skeleton;
            for (int p = 0; p < skele->PartialSkeletonCount && float.IsNaN(controlTime); p++)
            {
                var animated = skele->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
                if (animated == null || slot >= animated->AnimationControls.Length)
                    continue;
                var ctl = animated->AnimationControls[slot].Value;
                if (ctl != null)
                    controlTime = ctl->hkaAnimationControl.LocalTime;
            }
        }

        var line = new StringBuilder(160);
        line.Append(CultureInfo.InvariantCulture,
            $"[AnimReset] slot{slot} ctl={controlTime:0.00}");
        bool dropped = false;
        for (int i = 0; i < n && i < _resetWatchPrev.Length; i++)
        {
            float value = *cursorsBuffer[i];
            float prev = _resetWatchPrev[i];
            // A BACKWARD jump (not a per-frame advance) is the reset.
            bool back = !float.IsNaN(prev) && value < prev - 1.0f;
            line.Append(CultureInfo.InvariantCulture, $" c{i}={value:0.0}");
            if (back)
            {
                line.Append("<RESET");
                dropped = true;
            }
            _resetWatchPrev[i] = value;
        }
        // One line per frame is a lot; log only movement frames and resets.
        if (dropped || _resetWatchTicks % 6 == 0)
            _log.Information(line.ToString());
    }
}
