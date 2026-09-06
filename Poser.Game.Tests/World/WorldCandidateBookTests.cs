using System.Numerics;
using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.World;
using Xunit;

namespace Poser.Game.Tests.World;

public sealed class WorldCandidateBookTests
{
    private static WorldCandidateEntry Entry(WorldKinds kind, object identity, Func<bool>? valid = null,
        Func<Func<SelectionId?>?>? acquire = null) => new(identity, kind, "Candidate", Vector3.Zero,
            valid ?? (() => true), acquire ?? (() => () => SelectionId.ForEnvironment()));

    [Theory]
    [InlineData(WorldKinds.Actor)]
    [InlineData(WorldKinds.Light)]
    [InlineData(WorldKinds.Object)]
    [InlineData(WorldKinds.Effect)]
    public void Acquire_revalidates_before_touching_the_driver(WorldKinds kind)
    {
        var book = new WorldCandidateBook();
        bool valid = true;
        int acquisitions = 0;
        book.Refresh(kind, [Entry(kind, (123, 1), () => valid, () =>
        {
            acquisitions++;
            return () => SelectionId.ForEnvironment();
        })]);
        var id = Assert.Single(book.Snapshot.Candidates).Id;
        valid = false;
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(id, out var binding));
        Assert.Null(binding);
        Assert.Equal(0, acquisitions);
        valid = true;
        Assert.Equal(WorldCommandStatus.Applied, book.Acquire(id, out binding));
        Assert.NotNull(binding!());
        Assert.Equal(1, acquisitions);
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(id, out _));
        Assert.Equal(1, acquisitions);
    }

    [Theory]
    [InlineData(WorldKinds.Actor)]
    [InlineData(WorldKinds.Light)]
    [InlineData(WorldKinds.Object)]
    [InlineData(WorldKinds.Effect)]
    public void Refresh_keeps_identity_but_reused_native_lifetimes_get_new_ids(WorldKinds kind)
    {
        var book = new WorldCandidateBook();
        book.Refresh(kind, [Entry(kind, (123, 1))]);
        var before = book.Snapshot;
        var old = Assert.Single(before.Candidates).Id;
        book.Refresh(kind, [Entry(kind, (123, 1)) with { Name = "Moved", Position = Vector3.One }]);
        Assert.Equal(old, Assert.Single(book.Snapshot.Candidates).Id);
        Assert.Equal(Vector3.Zero, Assert.Single(before.Candidates).Position);
        book.Refresh(kind, [Entry(kind, (123, 2))]);
        Assert.NotEqual(old, Assert.Single(book.Snapshot.Candidates).Id);
        Assert.Equal(WorldCommandStatus.StaleCandidate, book.Acquire(old, out _));
        book.Refresh(kind, []);
        Assert.Empty(book.Snapshot.Candidates);
    }

    [Fact]
    public void Refresh_of_one_kind_preserves_the_other_listings()
    {
        var book = new WorldCandidateBook();
        book.Refresh(WorldKinds.All, [Entry(WorldKinds.Actor, 1), Entry(WorldKinds.Light, 1)]);
        book.Refresh(WorldKinds.Light, []);
        Assert.Equal(WorldKinds.Actor, Assert.Single(book.Snapshot.Candidates).Kind);
    }
}
