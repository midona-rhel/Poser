using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Scene;

public sealed class AttachmentRelationshipValidationTests
{
    [Fact]
    public void Attached_actor_requires_exact_owner_and_kind()
    {
        var owner = new ActorId(Guid.NewGuid(), 3);
        var child = new ActorId(Guid.NewGuid(), 6);
        var scene = new SceneSession(new SelectionSession());
        var root = new ActorDescriptor(owner, "Owner", []);
        var missingKind = new ActorDescriptor(
            child, "Child", [], IsCompanion: true, OwnerActor: owner);

        var refused = scene.TryRefresh(new SceneSnapshot(
            1, [root, missingKind], [], [], []));
        var accepted = scene.TryRefresh(new SceneSnapshot(
            1,
            [root, missingKind with { AttachmentKind = CompanionKind.Mount }],
            [], [], []));

        Assert.Equal(SceneRefreshOutcome.RejectedInvalidCandidate, refused.Outcome);
        Assert.Contains("attachment kind", refused.Detail!,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(accepted.Accepted);
    }
}
