using System.Numerics;
using Poser.Game.Posing;

namespace Poser.Game.Tests.Posing;

public sealed class ActorOrbitPositionTests
{
    [Fact]
    public void Moves_subtract_the_draw_offset_once_and_never_replace_native_restore_values()
    {
        var state = new ActorOrbitPosition(new(1, 2, 3), new(4, 5, 6));
        var offset = new Vector3(.3f, 1.5f, -.7f);
        foreach (var position in new[] { new Vector3(10, 20, 30), new Vector3(9, 8, 7), new Vector3(10, 20, 30) })
        {
            state.Schedule(position, true);
            Assert.True(state.TryTake(offset, true, false, false, out var next));
            Assert.Equal(position - offset, next);
        }
        Assert.True(state.Applied);
        Assert.Equal(new Vector3(1, 2, 3), state.OriginalPosition);
        Assert.Equal(new Vector3(4, 5, 6), state.OriginalDefaultPosition);
    }
}
