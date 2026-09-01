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
        _resetConstantsDumped = false;
        _containerPrev = null;
        _lastStableFrames = float.NaN;
        _oldPositionFrames = float.NaN;
        _staleScanDone = false;
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
        if (n >= 1)
        {
            float c0 = *cursorsBuffer[0];
            if (!float.IsNaN(_lastStableFrames) && c0 < _lastStableFrames - 5f && float.IsNaN(_oldPositionFrames))
                _oldPositionFrames = _lastStableFrames;
            _lastStableFrames = c0;
            ScanForStalePosition(character);
        }

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

        // The per-actor clock hunt: any float in the TimelineContainer that
        // moves each tick (no delta windows — the old hunts had a hole at
        // 0.3/tick, exactly a 30fps clock at ~100 ticks/s). Once found, the
        // scheduler's CONSTANT fields near that value are the start
        // reference the old schedule is measured from.
        WatchContainerClock(character, n >= 1 ? (nint)cursorsBuffer[0] - SchedulerTimestampOffset : 0);

        // ARM-TIME CONSTANTS: everything on the scheduler chain that looks
        // like a duration, position or deadline. Armed after a scrub to zero
        // but before Play, a field still holding the OLD position is the
        // stale reference the end is judged from.
        if (!_resetConstantsDumped && n >= 1)
        {
            _resetConstantsDumped = true;
            nint schedulerObject = (nint)cursorsBuffer[0] - SchedulerTimestampOffset;
            DumpConstants("sched", schedulerObject, 0x274);
            if (n >= 2)
            {
                nint trackController = (nint)cursorsBuffer[1] - 0x11C;
                DumpConstants("tctl", trackController, 0x130);
                nint trackPointers = *(nint*)(trackController + 0x28);
                nint track = trackPointers != 0 ? *(nint*)trackPointers : 0;
                if (track != 0)
                {
                    DumpConstants("track", track, 0xC0);
                    nint clipPointers = *(nint*)(track + 0x18);
                    nint clip = clipPointers != 0 ? *(nint*)clipPointers : 0;
                    if (clip != 0)
                        DumpConstants("clip", clip, 0x98);
                }
            }
            // The sequencer's own per-slot arrays, as floats.
            DumpConstants("seq", (nint)(&character->Timeline.TimelineSequencer), 0x2E0);
        }

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

    private float[]? _containerPrev;
    private int _containerReports;

    private void WatchContainerClock(Character* character, nint schedulerObject)
    {
        int size = TimelineContainerSize / 4;
        var basePtr = (float*)&character->Timeline;
        if (_containerPrev == null || _containerPrev.Length != size)
        {
            _containerPrev = new float[size];
            for (int i = 0; i < size; i++)
                _containerPrev[i] = basePtr[i];
            _containerReports = 0;
            return;
        }
        StringBuilder? line = null;
        for (int i = 0; i < size; i++)
        {
            float now = basePtr[i];
            float delta = now - _containerPrev[i];
            _containerPrev[i] = now;
            if (delta == 0f)
                continue;
            // Integer frame counters: a +1..+40 step reads as a denormal
            // float delta, so test the int view too.
            int nowInt = BitConverter.SingleToInt32Bits(now);
            int wasInt = BitConverter.SingleToInt32Bits(_containerPrev[i] + delta - delta) ;
            int intDelta = nowInt - BitConverter.SingleToInt32Bits(now - delta);
            bool intLike = Math.Abs(intDelta) >= 1 && Math.Abs(intDelta) <= 40
                && Math.Abs(nowInt) < 10_000_000;
            float size2 = Math.Abs(delta);
            bool floatLike = float.IsFinite(now) && size2 >= 0.002f && size2 <= 40f;
            if (!floatLike && !intLike)
                continue;
            if (intLike && !floatLike)
            {
                line ??= new StringBuilder(200).Append("[AnimReset] cclock");
                line.Append(CultureInfo.InvariantCulture, $" +{i * 4:x3}=i{nowInt}(d{intDelta})");
                continue;
            }
            line ??= new StringBuilder(200).Append("[AnimReset] cclock");
            line.Append(CultureInfo.InvariantCulture, $" +{i * 4:x3}={now:0.##}(d{delta:0.###})");
            // Scheduler constants within reach of this clock: start refs.
            if (schedulerObject != 0 && _containerReports < 6)
            {
                for (int off = 0; off + 4 <= 0x274; off += 4)
                {
                    float value = *(float*)(schedulerObject + off);
                    if (!float.IsFinite(value) || value == 0f)
                        continue;
                    if (Math.Abs(value - now) <= 200f)
                        line.Append(CultureInfo.InvariantCulture, $" ref+{off:x3}={value:0.##}");
                }
            }
        }
        if (line != null && _containerReports++ < 40)
            _log.Information(line.ToString());
    }

    private bool _resetConstantsDumped;
    private float _lastStableFrames = float.NaN;
    private float _oldPositionFrames = float.NaN;
    private bool _staleScanDone;

    /// <summary>After a scrub (a backward jump of the scheduler clock) the
    /// pre-scrub position is the value to hunt: any field on the Character or
    /// its timeline container still holding it — as frames, seconds, or an
    /// int — is the reference the old schedule is judged from.</summary>
    private void ScanForStalePosition(Character* character)
    {
        if (float.IsNaN(_oldPositionFrames) || _staleScanDone)
            return;
        _staleScanDone = true;
        float frames = _oldPositionFrames;
        float seconds = frames / 30f;
        var line = new StringBuilder(400).Append(CultureInfo.InvariantCulture,
            $"[AnimReset] stale hunt for {frames:0.0}f/{seconds:0.00}s:");
        int printed = 0;
        void Scan(string name, nint address, int size)
        {
            for (int off = 0; off + 4 <= size && printed < 40; off += 4)
            {
                int raw = *(int*)(address + off);
                if (raw == 0)
                    continue;
                float f = BitConverter.Int32BitsToSingle(raw);
                bool hit = float.IsFinite(f) && (Math.Abs(f - frames) <= 2f || Math.Abs(f - seconds) <= 0.07f);
                bool intHit = Math.Abs(raw - (int)Math.Round(frames)) <= 2;
                if (!hit && !intHit)
                    continue;
                line.Append(hit
                    ? string.Create(CultureInfo.InvariantCulture, $" {name}+{off:x3}={f:0.##}f")
                    : string.Create(CultureInfo.InvariantCulture, $" {name}+{off:x3}={raw}i"));
                printed++;
            }
        }
        Scan("chara", (nint)character, 0x2000);
        Scan("cont", (nint)(&character->Timeline), TimelineContainerSize);
        _log.Information(printed == 0 ? line.Append(" nothing").ToString() : line.ToString());
    }

    /// <summary>Prints the plausible durations/positions on one object:
    /// floats in [0.5, 5000] and ints in [1, 100000] that are not pointers.</summary>
    private void DumpConstants(string name, nint address, int size)
    {
        var line = new StringBuilder(400).Append("[AnimReset] const ").Append(name);
        int printed = 0;
        for (int offset = 0; offset + 4 <= size && printed < 48; offset += 4)
        {
            int raw = *(int*)(address + offset);
            if (raw == 0)
                continue;
            float f = BitConverter.Int32BitsToSingle(raw);
            if (float.IsFinite(f) && f >= 0.5f && f <= 5000f && Math.Abs(f - Math.Round(f)) < 0.01f || (float.IsFinite(f) && f >= 0.5f && f <= 5000f))
            {
                line.Append(CultureInfo.InvariantCulture, $" +{offset:x3}={f:0.##}f");
                printed++;
            }
            else if (raw >= 1 && raw <= 100000)
            {
                line.Append(CultureInfo.InvariantCulture, $" +{offset:x3}={raw}i");
                printed++;
            }
        }
        _log.Information(line.ToString());
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
