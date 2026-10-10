using Poser.Application.World;
using Poser.Domain.Identity;
using Poser.Game.World;
using Xunit;

namespace Poser.Game.Tests.World;

public sealed class WorldClaimBookTests
{
    [Fact]
    public void Receipts_release_the_exact_identity_once_and_never_redirect_to_a_replacement()
    {
        var book = new WorldClaimBook();
        var lineage = Guid.NewGuid();
        var original = SelectionId.ForLight(new(lineage, 1));
        var replacement = SelectionId.ForLight(new(lineage, 2));
        var first = book.Add(original);
        var second = book.Add(original);
        Assert.Equal(WorldCommandStatus.Refused,
            book.Release(first, _ => new(WorldCommandStatus.Refused)).Status);
        int calls = 0;
        Assert.True(book.Release(first, requested =>
        {
            calls++;
            Assert.Equal(original, requested);
            Assert.NotEqual(replacement, requested);
            return new(WorldCommandStatus.Applied);
        }).Success);
        Assert.Equal(1, calls);
        Assert.Equal(WorldCommandStatus.AlreadyReleased,
            book.Release(first, _ => throw new Exception("Released twice")).Status);
        Assert.Equal(WorldCommandStatus.AlreadyReleased,
            book.Release(second, _ => throw new Exception("Sibling receipt")).Status);
        var next = book.Add(replacement);
        book.Clear();
        Assert.Equal(WorldCommandStatus.AlreadyReleased,
            book.Release(next, _ => throw new Exception("Old session")).Status);
    }
}
