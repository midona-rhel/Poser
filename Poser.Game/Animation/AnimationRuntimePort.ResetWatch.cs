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
    private readonly float[] _resetWatchPrev = new float[32];

    /// <summary>Arms the per-frame clock log on this actor for ~20 seconds.</summary>
    public void ProbeWatchReset(ActorId actor)
    {
        _resetWatchActor = actor;
        _resetWatchTicks = 1200;
        for (int i = 0; i < _resetWatchPrev.Length; i++)
            _resetWatchPrev[i] = float.NaN;
        _log.Information($"[AnimReset] watch armed on {actor} for 20s.");
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

        var line = new StringBuilder(256);
        line.Append(CultureInfo.InvariantCulture,
            $"[AnimReset] slot{slot} tl={character->Timeline.TimelineSequencer.TimelineIds[slot]}");
        bool dropped = false;
        int watchIndex = 0;

        // EVERY partial's control for this slot: LocalTime and the control's
        // second time field (+0x190, seen advancing in the hunt).
        var draw = character->GameObject.DrawObject;
        if (draw != null && draw->Object.GetObjectType() == ObjectType.CharacterBase
            && ((CharacterBase*)draw)->Skeleton != null)
        {
            var skele = ((CharacterBase*)draw)->Skeleton;
            for (int p = 0; p < skele->PartialSkeletonCount; p++)
            {
                var animated = skele->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
                if (animated == null || slot >= animated->AnimationControls.Length)
                    continue;
                var ctl = animated->AnimationControls[slot].Value;
                if (ctl == null)
                    continue;
                float local = ctl->hkaAnimationControl.LocalTime;
                float second = *(float*)((byte*)ctl + 0x190);
                Track(line, ref dropped, ref watchIndex, $"p{p}", local);
                Track(line, ref dropped, ref watchIndex, $"p{p}b", second);
            }
        }

        for (int i = 0; i < n; i++)
            Track(line, ref dropped, ref watchIndex, $"c{i}", *cursorsBuffer[i]);

        // The limits the cursors are measured against (read-only, static).
        if (n >= 2)
        {
            nint trackController = (nint)cursorsBuffer[1] - 0x11C;
            nint trackPointers = *(nint*)(trackController + 0x28);
            if (trackPointers != 0)
            {
                nint track = *(nint*)trackPointers;
                nint clipPointers = track != 0 ? *(nint*)(track + 0x18) : 0;
                nint clip = clipPointers != 0 ? *(nint*)clipPointers : 0;
                if (clip != 0)
                    line.Append(CultureInfo.InvariantCulture,
                        $" trackTotal={*(float*)(clip + 0x54):0.0}"
                        + $" clipStart={*(float*)(clip + 0x64):0.0}"
                        + $" clipTotal={*(float*)(clip + 0x68):0.0}");
            }
        }

        if (dropped || _resetWatchTicks % 10 == 0)
            _log.Information(line.ToString());

        // The field diff: every dword that changed since last tick across the
        // scheduler object, its track controller, the first track and clip.
        // Units-agnostic — the counter that finishes on the OLD schedule is
        // the one that does not start at zero after a scrub to zero.
        if (n >= 1)
        {
            nint schedulerObject = (nint)cursorsBuffer[0] - SchedulerTimestampOffset;
            DiffObject(0, "sched", schedulerObject, 0x274);
            if (n >= 2)
            {
                nint trackController = (nint)cursorsBuffer[1] - 0x11C;
                DiffObject(1, "tctl", trackController, 0x130);
                nint trackPointers = *(nint*)(trackController + 0x28);
                nint track = trackPointers != 0 ? *(nint*)trackPointers : 0;
                if (track != 0)
                {
                    DiffObject(2, "track", track, 0xC0);
                    nint clipPointers = *(nint*)(track + 0x18);
                    nint clip = clipPointers != 0 ? *(nint*)clipPointers : 0;
                    if (clip != 0)
                        DiffObject(3, "clip", clip, 0x98);
                }
            }
        }
    }

    private readonly byte[][] _resetDiffPrev = new byte[4][];
    private readonly nint[] _resetDiffAddr = new nint[4];

    /// <summary>Prints the dwords of one object that changed since the last
    /// tick, as float and int. A new address resets the baseline silently.</summary>
    private void DiffObject(int index, string name, nint address, int size)
    {
        var prev = _resetDiffPrev[index];
        if (prev == null || prev.Length != size || _resetDiffAddr[index] != address)
        {
            prev = new byte[size];
            _resetDiffPrev[index] = prev;
            _resetDiffAddr[index] = address;
            fixed (byte* dst = prev)
                Buffer.MemoryCopy((void*)address, dst, size, size);
            return;
        }
        StringBuilder? line = null;
        int printed = 0;
        for (int offset = 0; offset + 4 <= size; offset += 4)
        {
            int now = *(int*)(address + offset);
            int was = BitConverter.ToInt32(prev, offset);
            if (now == was)
                continue;
            if (printed++ < 24)
            {
                line ??= new StringBuilder(200).Append("[AnimReset] ").Append(name);
                float asFloat = BitConverter.Int32BitsToSingle(now);
                line.Append(CultureInfo.InvariantCulture,
                    $" +{offset:x3}={asFloat:0.###}/i{now}");
            }
        }
        fixed (byte* dst = prev)
            Buffer.MemoryCopy((void*)address, dst, size, size);
        if (line != null)
            _log.Information(line.ToString());
    }

    /// <summary>Appends one clock and flags a BACKWARD jump as the reset.</summary>
    private void Track(StringBuilder line, ref bool dropped, ref int index, string name, float value)
    {
        if (index >= _resetWatchPrev.Length)
            return;
        float prev = _resetWatchPrev[index];
        bool back = !float.IsNaN(prev) && value < prev - 0.5f;
        line.Append(CultureInfo.InvariantCulture, $" {name}={value:0.00}");
        if (back)
        {
            line.Append("<RESET");
            dropped = true;
        }
        _resetWatchPrev[index] = value;
        index++;
    }
}
