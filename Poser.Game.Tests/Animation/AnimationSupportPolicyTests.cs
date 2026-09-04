using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

public sealed class AnimationSupportPolicyTests
{
    [Fact]
    public void Attached_companion_uses_backend_while_standalone_companion_stays_unsupported()
    {
        var id = new ActorId(Guid.NewGuid(), 2);
        var owner = new ActorId(Guid.NewGuid(), 5);
        var live = new ActorBase(
            new EntityId("companion"), "Companion", (nint)0x100,
            ActorKind.Companion);
        var standalone = new ActorDescriptor(
            id, "Companion", [], IsCompanion: true);
        var attached = standalone with
        {
            OwnerActor = owner,
            AttachmentKind = CompanionKind.Companion,
        };

        Assert.False(AnimationRuntimePort.IsSupportedActor(live, standalone));
        Assert.True(AnimationRuntimePort.IsSupportedActor(live, attached));
    }
}
