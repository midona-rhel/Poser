using Poser.Domain.Scene;

namespace Poser.Domain.Tests;

public sealed class EntityNamesTests
{
    [Theory]
    [InlineData("Key 1", "Key 1|Key 3", "Key 4")]
    [InlineData("Key", "Keyboard 9|Other Key 7", "Key 1")]
    public void Advances_only_the_matching_series(string seed, string names, string expected)
        => Assert.Equal(expected, EntityNames.Next(seed, names.Split('|')));
}
