using Poser.Game.Scene;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Scene;

public class CoalescedRefreshTests
{
    [Fact]
    public void FollowUpSeesReplacementAndIdenticalCandidatesDoNotChurnRevision()
    {
        var queue = new CoalescedRefresh();
        var scene = new SceneSession(new SelectionSession());
        Guid lineage = Guid.NewGuid();
        uint generation = 1, bound = 0;
        int passes = 0;
        void Refresh()
        {
            uint enumerated = generation;
            var candidate = new SceneSnapshot(0,
                [new ActorDescriptor(new ActorId(lineage, enumerated), "Actor", [])], [], [], []);
            if (++passes == 1)
            {
                generation = 2;
                queue.Request(Refresh);
            }
            Assert.True(scene.TryRefresh(CleanSceneLifecycle.CreateAdmissionCandidate(candidate, scene.Snapshot)).Accepted);
            bound = enumerated;
        }
        queue.Request(Refresh);
        Assert.Equal(2u, bound);
        ulong revision = scene.Snapshot.Revision;
        queue.Request(Refresh);
        Assert.Equal(revision, scene.Snapshot.Revision);
        Assert.Equal(2u, scene.Snapshot.Actors[0].Id.Generation);
    }
}
