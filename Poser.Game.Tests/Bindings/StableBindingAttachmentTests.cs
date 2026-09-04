using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;

namespace Poser.Game.Tests.Bindings;

public sealed class StableBindingAttachmentTests
{
    [Fact]
    public void Link_records_exact_owner_generation_and_attachment_kind()
    {
        var owner = new ActorId(Guid.NewGuid(), 4);
        var child = new ActorId(Guid.NewGuid(), 7);
        var unrelated = new ActorId(Guid.NewGuid(), 2);
        var descriptors = new List<ActorDescriptor>
        {
            new(owner, "Owner", []),
            new(child, "Mount", [], IsCompanion: true),
            new(unrelated, "Other", [], IsCompanion: true),
        };
        var addresses = new List<nint> { 0x100, 0x200, 0x300 };
        var owners = new Dictionary<nint, ActorAttachment>
        {
            [0x200] = new(owner, CompanionKind.Mount),
        };

        StableBindingRegistry.LinkCompanionOwners(
            descriptors, addresses, owners);

        Assert.Null(descriptors[0].OwnerActor);
        Assert.Equal(owner, descriptors[1].OwnerActor);
        Assert.Equal(CompanionKind.Mount, descriptors[1].AttachmentKind);
        Assert.Null(descriptors[2].OwnerActor);
        Assert.Null(descriptors[2].AttachmentKind);
    }

    [Fact]
    public void Link_refuses_self_owner_relation()
    {
        var child = new ActorId(Guid.NewGuid(), 3);
        var descriptor = new ActorDescriptor(
            child, "Child", [], IsCompanion: true);
        var descriptors = new List<ActorDescriptor> { descriptor };

        StableBindingRegistry.LinkCompanionOwners(
            descriptors,
            [0x200],
            new Dictionary<nint, ActorAttachment>
            {
                [0x200] = new(child, CompanionKind.Companion),
            });

        Assert.Equal(descriptor, descriptors[0]);
    }
}
