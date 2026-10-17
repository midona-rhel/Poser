using System;
using Poser.Game.Natives;

namespace Poser.Game.Animation;

/// <summary>
/// The game's own state of a slot's root SchedulerTimeline (0x280 bytes), every
/// test read the way the game reads it (RE notes/poser-animation-model.md §3). For
/// a C010 child timeline, read its root's state.
/// </summary>
internal readonly unsafe struct TimelineState
{
    internal const int Flags10 = NativeFields.SchedulerTimeline_State;          // bit 5: started
    internal const int Flags11 = NativeFields.SchedulerTimeline_Latch;          // bit 1: finished
    internal const int ClockOffset = NativeFields.TimelineController_CurrentTimestamp;
    internal const int BeginOffset = NativeFields.SchedulerTimeline_Begin;
    internal const int EndOffset = NativeFields.SchedulerTimeline_End;
    internal const int LengthOffset = NativeFields.SchedulerTimeline_Length;
    internal const int PauseMaskOffset = NativeFields.SchedulerState_PauseMask;
    internal const int PlayModeOffset = NativeFields.SchedulerTimeline_LoopMode;   // 1: one-shot
    internal const int FrameOffset = NativeFields.SchedulerTimeline_IntegerFrame;      // the integer-frame clock
    internal const int RunStateOffset = NativeFields.SchedulerTimeline_PlayState;   // 0 before begin, 1 running, 2/3 ended
    internal const int LoadStateOffset = NativeFields.SchedulerTimeline_LoadState;  // 5: every clip ready
    internal const int Flags7C = NativeFields.SchedulerTimeline_ClockFlags;          // bit 5 integer frame, bit 6 finished (clock frozen at +0x48)
    internal const int Flags7D = NativeFields.SchedulerTimeline_WrapControl;          // bit 1 holds at its end, bit 2 releases the hold
    internal const int SpeedOffset = NativeFields.SchedulerTimeline_Speed;     // the effective speed
    internal const int Flags272 = NativeFields.SchedulerTimeline_Flags272;        // bit 2 set by TimelineGroup.Teardown
    internal const int Flags273 = NativeFields.SchedulerTimeline_Flags273;        // bit 0 teardown marked, bit 5 ENDED (Notify(0))
    internal const int IdOffset = NativeFields.SchedulerTimeline_ActionKey;        // the action key (row id + 0x20000)
    internal const int SelfHandleOffset = NativeFields.SchedulerTimeline_SelfHandle;

    private readonly byte* _tl;

    private TimelineState(byte* timeline) => _tl = timeline;

    internal static TimelineState Of(nint timeline) => new((byte*)timeline);

    internal bool Exists => _tl != null;
    internal bool Dead => Exists && (_tl[Flags273] & 0x01) != 0;

    /// <summary>Torn down by its group (TimelineGroup.Teardown: +0x10 bit 7, +0x272
    /// bit 2): its memory is still valid but it is no longer a live timeline.</summary>
    internal bool TornDown => Exists && ((_tl[Flags10] & 0x80) != 0 || (_tl[Flags272] & 0x04) != 0);

    /// <summary>What identifies this timeline beyond its address, which a new
    /// timeline can reuse: its self handle node and its action key.</summary>
    internal (nint Node, uint Id) Identity => Exists
        ? (*(nint*)(_tl + SelfHandleOffset), *(uint*)(_tl + IdOffset))
        : default;
    internal bool Loaded => Exists && *(int*)(_tl + LoadStateOffset) == 5;
    internal bool Started => Exists && (_tl[Flags10] & 0x20) != 0;
    internal bool Ended => Exists && (_tl[Flags273] & 0x20) != 0;
    internal bool Finished => Exists && ((_tl[Flags7C] & 0x40) != 0 || (_tl[Flags11] & 0x02) != 0);
    internal bool IntegerFrame => Exists && (_tl[Flags7C] & 0x20) != 0;
    internal bool Paused => Exists && PauseMask != 0;

    /// <summary>Who holds the clock: bit 2 is UpdateBanner's own and the GPose motion
    /// freeze; a tool takes a high bit.</summary>
    internal uint PauseMask => *(uint*)(_tl + PauseMaskOffset);

    internal bool OneShot => Exists && *(int*)(_tl + PlayModeOffset) == 1;

    internal bool Playing => Started && !Ended && (_tl[Flags11] & 0x02) == 0
        && *(int*)(_tl + RunStateOffset) == 1;

    /// <summary>A one-shot stopped at its end: never finished, and its Havok controls
    /// free-run, so its clock means nothing to a seek (mount idles).</summary>
    internal bool OneShotClamped => OneShot
        && *(int*)(_tl + RunStateOffset) == 3 && (_tl[Flags7C] & 0x40) == 0
        && Math.Abs(*(float*)(_tl + ClockOffset) - End) < 0.01f;

    /// <summary>A looping timeline holding at its end (+0x74 stays 1). +0x7D bit 1
    /// alone isn't it: Toast carries it from its start (frame 61).</summary>
    internal bool HeldAtEnd => Exists && (_tl[Flags7D] & 0x02) != 0 && (_tl[Flags7D] & 0x04) == 0
        && (_tl[Flags7C] & 0x40) == 0 && Math.Abs(*(float*)(_tl + ClockOffset) - End) < 0.01f;

    /// <summary>Stopped at its end while its controls go on: the layer's time is theirs.</summary>
    internal bool ClampedAtEnd => OneShotClamped || HeldAtEnd;

    /// <summary>The clock's position can be moved (an integer-frame clock aside).</summary>
    internal bool Seekable => Loaded && !Dead && !Finished;

    /// <summary>The clock as the game reads it: integer frame +0x64, finished +0x48,
    /// otherwise +0x34.</summary>
    internal float Clock
    {
        get
        {
            byte flags = _tl[Flags7C];
            if ((flags & 0x20) != 0)
                return *(uint*)(_tl + FrameOffset);
            if ((flags & 0x40) != 0)
                return *(float*)(_tl + LengthOffset);
            return *(float*)(_tl + ClockOffset);
        }
    }

    internal float Begin => *(float*)(_tl + BeginOffset);
    internal float End => *(float*)(_tl + EndOffset);
    internal float Length => *(float*)(_tl + LengthOffset);
    internal float Speed => *(float*)(_tl + SpeedOffset);
}
