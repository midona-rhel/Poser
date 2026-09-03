using Poser.Game.WorldObjects;

namespace Poser.Game.Tests.WorldObjects;

public sealed class VfxPathClaimOwnerTests
{
    [Fact]
    public void Failed_create_rolls_back_pending_claim()
    {
        var owner = new VfxPathClaimOwner();
        using (owner.Acquire("VFX/Fire.AVFX")) { }
        Assert.False(owner.Contains("vfx/fire.avfx"));
    }

    [Fact]
    public void Same_path_remains_handled_until_last_instance_releases()
    {
        var owner = new VfxPathClaimOwner();
        using var first = owner.Acquire("vfx/fire.avfx");
        using var second = owner.Acquire("VFX/FIRE.AVFX");

        Assert.Equal(2, owner.Count("vfx/fire.avfx"));
        first.Dispose();
        Assert.True(owner.Contains("vfx/fire.avfx"));
        second.Dispose();
        second.Dispose();
        Assert.False(owner.Contains("vfx/fire.avfx"));
    }
}
