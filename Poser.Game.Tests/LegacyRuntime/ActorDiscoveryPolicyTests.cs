using Dalamud.Game.ClientState.Objects.Enums;
using Poser.Game;

namespace Poser.Game.Tests.LegacyRuntime;

public sealed class ActorDiscoveryPolicyTests
{
    [Theory]
    [InlineData(ObjectKind.Companion)]
    [InlineData(ObjectKind.Mount)]
    [InlineData(ObjectKind.Ornament)]
    public void Character_backed_attachments_are_discoverable(ObjectKind kind)
    {
        Assert.True(ActorManager.IsDiscoverableActor(kind, characterBacked: true));
        Assert.False(ActorManager.IsDiscoverableActor(kind, characterBacked: false));
    }

    [Fact]
    public void Unrelated_object_kinds_are_not_discoverable()
    {
        Assert.False(ActorManager.IsDiscoverableActor(
            (ObjectKind)byte.MaxValue,
            characterBacked: true));
    }
}
