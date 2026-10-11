using System.Numerics;
using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.WorldObjects;
using Xunit;

namespace Poser.Game.Tests.WorldObjects;

public sealed class WorldCandidateBookTests
{
    private static WorldCandidateEntry Entry(WorldKinds kind, object identity, Func<bool>? valid = null,
        Func<WorldAcquisitionBinding?>? acquire = null) => new(identity, kind, "Candidate", Vector3.Zero,
            valid ?? (() => true), acquire ?? (() => new(() => SelectionId.ForEnvironment(), () => { }, () => true)));

    [Fact]
    public void Acquire_revalidates_before_touching_the_driver()
    {
        var book = new WorldCandidateBook();
        bool valid = true;
        int acquisitions = 0;
        book.Refresh(WorldKinds.Light, [Entry(WorldKinds.Light, (123, 1), () => valid, () =>
        {
            acquisitions++;
            return new(() => SelectionId.ForEnvironment(), () => { }, () => true);
        })]);
        var id = Assert.Single(book.Snapshot.Candidates).Id;
        valid = false;
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(id, out var binding));
        Assert.Null(binding);
        Assert.Equal(0, acquisitions);
        valid = true;
        Assert.Equal(WorldCommandStatus.Applied, book.Acquire(id, out binding));
        Assert.NotNull(binding!.Resolve());
        Assert.Equal(1, acquisitions);
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(id, out _));
        Assert.Equal(1, acquisitions);
    }

    [Fact]
    public void Refresh_keeps_identity_but_reused_native_lifetimes_get_new_ids()
    {
        var book = new WorldCandidateBook();
        book.Refresh(WorldKinds.All, [Entry(WorldKinds.Light, (123, 1)), Entry(WorldKinds.Actor, 1)]);
        var before = book.Snapshot;
        var old = Assert.Single(before.Candidates, c => c.Kind == WorldKinds.Light).Id;
        book.Refresh(WorldKinds.Light, [Entry(WorldKinds.Light, (123, 1)) with { Name = "Moved", Position = Vector3.One }]);
        Assert.Equal(old, Assert.Single(book.Snapshot.Candidates, c => c.Kind == WorldKinds.Light).Id);
        Assert.Equal(Vector3.Zero, Assert.Single(before.Candidates, c => c.Kind == WorldKinds.Light).Position);
        book.Refresh(WorldKinds.Light, [Entry(WorldKinds.Light, (123, 2))]);
        Assert.NotEqual(old, Assert.Single(book.Snapshot.Candidates, c => c.Kind == WorldKinds.Light).Id);
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(old, out _));
        // Refreshing one kind leaves the other listings alone.
        book.Refresh(WorldKinds.Light, []);
        Assert.Equal(WorldKinds.Actor, Assert.Single(book.Snapshot.Candidates).Kind);
    }
}
