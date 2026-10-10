using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Scene;

public sealed class SceneSessionFloorTests
{
    [Fact]
    public void Generation_floors_hold_within_a_session_and_reset_with_the_next()
    {
        var sessions = new Sessions { ActiveSessionGeneration = SessionGeneration.New() };
        var scene = new SceneSession(new SelectionSession(), sessions);
        var prop = Guid.NewGuid();

        Assert.True(scene.TryRefresh(Snapshot(1, prop, 2)).Accepted);
        Assert.Equal(
            SceneRefreshOutcome.RejectedInvalidCandidate,
            scene.TryRefresh(Snapshot(2, prop, 1)).Outcome);

        sessions.ActiveSessionGeneration = SessionGeneration.New();
        Assert.True(scene.TryRefresh(Snapshot(2, prop, 1)).StateChanged);
    }

    private static SceneSnapshot Snapshot(ulong revision, Guid prop, uint generation) =>
        new(revision, [], [], [], [new PropDescriptor(new PropId(prop, generation), "Prop")]);

    private sealed class Sessions : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; }
    }
}
