using System.Numerics;
using Poser.Config;
using Poser.Game.Posing;

namespace Poser.Game.Tests.Posing;

public sealed class ActorOrbitPositionTests
{
    private static ActorOrbitPosition NewState() => new(new(1, 2, 3), new(4, 5, 6));

    [Fact]
    public void Camera_orbit_update_is_enabled_for_a_new_configuration() =>
        Assert.True(new CameraConfiguration().UpdateOrbitWithActorPosition);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Dragging_or_camera_lock_defers_the_latest_position(bool mouseHeld, bool locked)
    {
        var state = NewState();
        state.Schedule(new(10, 20, 30), true);
        Assert.False(state.TryTake(Vector3.Zero, true, mouseHeld, locked, out _));
        Assert.False(state.Applied);
        state.Schedule(new(11, 21, 31), true);
        Assert.True(state.TryTake(Vector3.Zero, true, false, false, out var next));
        Assert.Equal(new Vector3(11, 21, 31), next);
        Assert.False(state.TryTake(Vector3.Zero, true, false, false, out _));
    }

    [Fact]
    public void Subtracts_the_current_draw_offset_exactly_once()
    {
        var state = NewState();
        state.Schedule(new(10, 20, 30), true);
        var offset = new Vector3(.3f, 1.5f, -.7f);
        Assert.True(state.TryTake(offset, true, false, false, out var next));
        Assert.Equal(new Vector3(10, 20, 30) - offset, next);
        Assert.Equal(new Vector3(10, 20, 30), next + offset);
    }

    [Fact]
    public void Disabling_discards_pending_work_until_another_edit()
    {
        var state = NewState();
        state.Schedule(new(10, 20, 30), true);
        Assert.False(state.TryTake(Vector3.Zero, false, false, false, out _));
        Assert.False(state.TryTake(Vector3.Zero, true, false, false, out _));
        state.Schedule(new(11, 22, 33), false);
        Assert.False(state.TryTake(Vector3.Zero, true, false, false, out _));
        state.Schedule(new(11, 22, 33), true);
        Assert.True(state.TryTake(Vector3.Zero, true, false, false, out _));
    }

    [Fact]
    public void Subsequent_moves_and_undo_do_not_replace_native_restore_values()
    {
        var state = NewState();
        foreach (var position in new[] { new Vector3(10, 20, 30), new Vector3(9, 8, 7), new Vector3(10, 20, 30) })
        {
            state.Schedule(position, true);
            Assert.True(state.TryTake(Vector3.Zero, true, false, false, out var next));
            Assert.Equal(position, next);
        }
        Assert.True(state.Applied);
        Assert.Equal(new Vector3(1, 2, 3), state.OriginalPosition);
        Assert.Equal(new Vector3(4, 5, 6), state.OriginalDefaultPosition);
    }
}
