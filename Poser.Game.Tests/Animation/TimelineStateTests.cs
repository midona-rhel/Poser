using Poser.Game.Animation;
using Xunit;

namespace Poser.Game.Tests.Animation;

public sealed unsafe class TimelineStateTests
{
    private sealed class Timeline
    {
        public readonly byte[] Bytes = new byte[0x280];

        public Timeline Set<T>(int offset, T value) where T : unmanaged
        {
            fixed (byte* p = Bytes)
                *(T*)(p + offset) = value;
            return this;
        }

        public Timeline Bit(int offset, byte bit) { Bytes[offset] |= bit; return this; }

        public R Read<R>(System.Func<TimelineState, R> read)
        {
            fixed (byte* p = Bytes)
                return read(TimelineState.Of((nint)p));
        }
    }

    private static Timeline Running() => new Timeline()
        .Bit(TimelineState.Flags10, 0x20)
        .Set(TimelineState.LoadStateOffset, 5)
        .Set(TimelineState.RunStateOffset, 1)
        .Set(TimelineState.BeginOffset, 0f)
        .Set(TimelineState.EndOffset, 90f)
        .Set(TimelineState.LengthOffset, 90f)
        .Set(TimelineState.ClockOffset, 30f);

    [Fact]
    public void No_timeline_is_in_no_state()
    {
        var none = TimelineState.Of(0);
        Assert.False(none.Exists);
        Assert.False(none.Loaded);
        Assert.False(none.Seekable);
        Assert.False(none.ClampedAtEnd);
    }

    [Fact]
    public void A_running_timeline_plays_and_seeks_on_its_float_clock()
    {
        var tl = Running();
        Assert.True(tl.Read(s => s.Playing));
        Assert.True(tl.Read(s => s.Seekable));
        Assert.False(tl.Read(s => s.ClampedAtEnd));
        Assert.Equal(30f, tl.Read(s => s.Clock));
    }

    [Fact]
    public void Loading_dead_and_finished_timelines_cannot_be_seeked()
    {
        Assert.False(Running().Set(TimelineState.LoadStateOffset, 4).Read(s => s.Seekable));
        Assert.False(Running().Bit(TimelineState.Flags273, 0x01).Read(s => s.Seekable));
        Assert.False(Running().Bit(TimelineState.Flags7C, 0x40).Read(s => s.Seekable));
        Assert.False(Running().Bit(TimelineState.Flags11, 0x02).Read(s => s.Seekable));
    }

    [Fact]
    public void Ended_is_the_games_end_bit_not_the_teardown_bit()
    {
        Assert.True(Running().Bit(TimelineState.Flags273, 0x20).Read(s => s.Ended));
        Assert.False(Running().Bit(TimelineState.Flags273, 0x20).Read(s => s.Playing));
        Assert.False(Running().Bit(TimelineState.Flags273, 0x01).Read(s => s.Ended));
    }

    [Fact]
    public void The_clock_reads_the_integer_frame_or_the_frozen_length()
    {
        Assert.Equal(12f, Running().Bit(TimelineState.Flags7C, 0x20).Set(TimelineState.FrameOffset, 12u).Read(s => s.Clock));
        Assert.Equal(90f, Running().Bit(TimelineState.Flags7C, 0x40).Read(s => s.Clock));
    }

    [Fact]
    public void A_one_shot_at_its_end_is_clamped()
    {
        var tl = Running().Set(TimelineState.PlayModeOffset, 1).Set(TimelineState.RunStateOffset, 3)
            .Set(TimelineState.ClockOffset, 90f);
        Assert.True(tl.Read(s => s.OneShotClamped));
        Assert.True(tl.Read(s => s.ClampedAtEnd));
        Assert.False(tl.Bit(TimelineState.Flags7C, 0x40).Read(s => s.ClampedAtEnd));
    }

    [Fact]
    public void A_hold_flag_counts_only_at_the_end_and_until_released()
    {
        // This bit may be set before the timeline reaches its end.
        Assert.False(Running().Bit(TimelineState.Flags7D, 0x02).Read(s => s.HeldAtEnd));
        var held = Running().Bit(TimelineState.Flags7D, 0x02).Set(TimelineState.ClockOffset, 90f);
        Assert.True(held.Read(s => s.HeldAtEnd));
        Assert.False(held.Bit(TimelineState.Flags7D, 0x04).Read(s => s.HeldAtEnd));
    }
}
