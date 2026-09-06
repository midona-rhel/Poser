using Poser.Game.Scene;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Scene;

public class CoalescedRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FollowUpSeesReplacementAndIdenticalCandidatesDoNotChurnRevision(bool auxiliary)
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
                [new ActorDescriptor(new ActorId(lineage, auxiliary ? 1u : enumerated), "Actor", [])], [], [], []);
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
        Assert.Equal(auxiliary ? 1u : 2u, scene.Snapshot.Actors[0].Id.Generation);
    }

    [Fact]
    public void ReplacementAfterEnumerationIsObservedWithoutAnotherEvent()
    {
        var queue = new CoalescedRefresh();
        int generation = 1, bound = 0, passes = 0, depth = 0;
        void Refresh()
        {
            Assert.Equal(1, ++depth);
            bound = generation;
            if (++passes == 1)
            {
                generation = 2;
                queue.Request(Refresh);
                queue.Request(Refresh);
            }
            depth--;
        }
        queue.Request(Refresh);
        Assert.Equal(2, bound);
        Assert.Equal(2, passes);
        queue.Drain(Refresh);
        Assert.Equal(2, passes);
    }

    [Fact]
    public void NotificationsDuringFollowUpWaitForNextTick()
    {
        var queue = new CoalescedRefresh();
        int passes = 0;
        void Refresh()
        {
            if (++passes < 4) queue.Request(Refresh);
        }
        queue.Request(Refresh);
        Assert.Equal(2, passes);
        queue.Drain(Refresh);
        Assert.Equal(4, passes);
    }

    [Fact]
    public void ExitCancelsPendingWorkAndDisposeRejectsQueuedCallbacks()
    {
        var queue = new CoalescedRefresh();
        int passes = 0;
        void Refresh() { passes++; queue.Request(Refresh); }
        queue.Request(Refresh);
        queue.Cancel();
        queue.Drain(Refresh);
        Assert.Equal(2, passes);
        queue.Request(Refresh);
        Assert.Equal(4, passes);
        queue.Stop();
        queue.Drain(Refresh);
        queue.Request(Refresh);
        Assert.Equal(4, passes);
    }
}
