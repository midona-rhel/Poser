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

    [Fact]
    public void Lineage_lookup_follows_the_committed_generation_and_a_refused_candidate_changes_nothing()
    {
        var sessions = new Sessions { ActiveSessionGeneration = SessionGeneration.New() };
        var scene = new SceneSession(new SelectionSession(), sessions);
        var prop = Guid.NewGuid();
        var current = SelectionId.ForProp(new PropId(prop, 3));

        Assert.True(scene.TryRefresh(Snapshot(1, prop, 1)).Accepted);
        Assert.True(scene.TryRefresh(Snapshot(2, prop, 3)).Accepted);
        Assert.Equal<SelectionId?>(current, scene.Resolve(SelectionId.ForProp(new PropId(prop, 1))));

        // A regressed candidate is refused whole: lookup and floor stay at 3.
        Assert.Equal(
            SceneRefreshOutcome.RejectedInvalidCandidate,
            scene.TryRefresh(Snapshot(3, prop, 2)).Outcome);
        Assert.Equal<SelectionId?>(current, scene.Resolve(SelectionId.ForProp(new PropId(prop, 2))));
        Assert.Equal(
            SceneRefreshOutcome.RejectedInvalidCandidate,
            scene.TryRefresh(Snapshot(3, prop, 2)).Outcome);
    }

    private static SceneSnapshot Snapshot(ulong revision, Guid prop, uint generation) =>
        new(revision, [], [], [], [new PropDescriptor(new PropId(prop, generation), "Prop")]);

    private sealed class Sessions : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; }
    }
}
