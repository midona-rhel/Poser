using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.World;
using Xunit;

namespace Poser.Game.Tests.World;

public sealed class WorldClaimBookTests
{
    private static SelectionId Entity(WorldKinds kind, Guid id, uint generation) => kind switch
    {
        WorldKinds.Actor => SelectionId.ForActor(new(id, generation)),
        WorldKinds.Light => SelectionId.ForLight(new(id, generation)),
        _ => SelectionId.ForWorldObject(new(id, generation)),
    };

    [Theory]
    [InlineData(WorldKinds.Actor)]
    [InlineData(WorldKinds.Light)]
    [InlineData(WorldKinds.Object)]
    [InlineData(WorldKinds.Effect)]
    public void Release_uses_the_exact_scene_identity_and_drops_all_its_receipts(WorldKinds kind)
    {
        var book = new WorldClaimBook();
        var id = Entity(kind, Guid.NewGuid(), 1);
        var first = book.Add(id);
        var second = book.Add(id);
        int calls = 0;
        WorldRelease Release(SelectionId requested)
        {
            Assert.Equal(id, requested);
            calls++;
            return new(WorldCommandStatus.Applied);
        }
        Assert.True(book.Release(id, Release).Success);
        Assert.Equal(WorldCommandStatus.AlreadyReleased, book.Release(first, Release).Status);
        Assert.Equal(WorldCommandStatus.AlreadyReleased, book.Release(second, Release).Status);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(WorldKinds.Actor)]
    [InlineData(WorldKinds.Light)]
    [InlineData(WorldKinds.Object)]
    [InlineData(WorldKinds.Effect)]
    public void Refusal_keeps_receipt_and_a_replacement_cannot_be_released_through_it(WorldKinds kind)
    {
        var book = new WorldClaimBook();
        var lineage = Guid.NewGuid();
        var original = Entity(kind, lineage, 1);
        var replacement = Entity(kind, lineage, 2);
        var claim = book.Add(original);
        Assert.Equal(WorldCommandStatus.Refused,
            book.Release(claim, _ => new(WorldCommandStatus.Refused)).Status);
        int calls = 0;
        Assert.True(book.Release(claim, requested =>
        {
            calls++;
            Assert.Equal(original, requested);
            Assert.NotEqual(replacement, requested);
            return new(WorldCommandStatus.AlreadyReleased);
        }).Success);
        Assert.Equal(1, calls);
        Assert.Equal(WorldCommandStatus.AlreadyReleased,
            book.Release(claim, _ => throw new Exception("Released twice")).Status);
        var next = book.Add(replacement);
        book.Clear();
        Assert.Equal(WorldCommandStatus.AlreadyReleased,
            book.Release(next, _ => throw new Exception("Old session")).Status);
    }
}
