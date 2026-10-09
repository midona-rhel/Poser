using Poser.Library;

namespace Poser.Domain.Tests.Library;

public sealed class LibraryBrowseOrderingTests
{
    private static readonly DateTime Earlier = new(2026, 1, 2, 3, 4, 5);
    private static readonly DateTime Later = Earlier.AddMinutes(1);

    [Theory]
    [InlineData(LibraryBrowseSort.ModifiedNewest, -1)]
    [InlineData(LibraryBrowseSort.ModifiedOldest, 1)]
    public void CompareModified_OrdersBothDateDirections(
        LibraryBrowseSort sort,
        int expectedSign)
    {
        int result = LibraryBrowseOrdering.CompareModified(Later, Earlier, sort);

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData(LibraryBrowseSort.Name)]
    [InlineData(LibraryBrowseSort.ModifiedNewest)]
    [InlineData(LibraryBrowseSort.ModifiedOldest)]
    public void CompareModified_LeavesEqualDatesStable(LibraryBrowseSort sort)
    {
        Assert.Equal(0, LibraryBrowseOrdering.CompareModified(Earlier, Earlier, sort));
    }
}
