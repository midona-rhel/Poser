using Poser.Domain.Transforms;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class ActorStateRestoreTests
{
    private static ActorState Read(bool paused) =>
        new(Transform.Identity, true, null)
        {
            Runtime = new ActorRuntimeState(null, null!, null, null, []) { Paused = paused },
        };

    [Fact]
    public void A_restored_actor_keeps_the_pause_it_was_read_with()
    {
        // #483: Destroy then Undo brought a paused actor back animating.
        Assert.True(Read(paused: true).RestoresPaused);
        Assert.False(Read(paused: false).RestoresPaused);
        // A posed duplicate's frozen basis still holds without a runtime read.
        Assert.True(new ActorState(Transform.Identity, true, null) { FreezePoseOnRestore = true }.RestoresPaused);
    }
}
