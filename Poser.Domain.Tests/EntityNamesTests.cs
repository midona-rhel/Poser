using Poser.Domain.Scene;

namespace Poser.Domain.Tests;

public sealed class EntityNamesTests
{
    [Theory]
    [InlineData("Key 1", "Key 1|Key 3", "Key 4")]
    [InlineData("Key 2", "Key 1|Key 2", "Key 3")]
    [InlineData("Key", "Key", "Key 2")]
    [InlineData("Key", "Key 1|Key 3", "Key 4")]
    [InlineData("Camera", "Actor 1", "Camera 1")]
    [InlineData("Actor", "Camera 1", "Actor 1")]
    [InlineData("Key 9", "Key 1", "Key 10")]
    [InlineData("Key", "Keyboard 9|Other Key 7", "Key 1")]
    [InlineData("Key 1", "key 003", "Key 4")]
    [InlineData("Key 2147483647", "", "Key 2147483648")]
    public void Advances_only_the_matching_series(string seed, string names, string expected)
        => Assert.Equal(expected, EntityNames.Next(seed, names.Split('|')));
}
