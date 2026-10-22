using Poser.Documents.Files;

namespace Poser.Application.Tests.Files;

public class FileNamePolicyTests
{
    [Fact]
    public void Every_platform_invalid_character_has_consistent_modes()
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            string input = $"a{invalid}b";
            Assert.True(FileNamePolicy.HasInvalidCharacters(input));
            Assert.Equal("ab", FileNamePolicy.FilterInput(input));
            Assert.Equal("a_b", FileNamePolicy.GeneratedStem(input, "Entry"));
        }
    }

    [Theory]
    [InlineData("", "Entry")]
    [InlineData("   ", "Entry")]
    [InlineData("  Åsa 猫  ", "Åsa 猫")]
    public void Generated_names_trim_and_keep_explicit_fallback(string input, string expected)
    {
        Assert.Equal(input, FileNamePolicy.FilterInput(input));
        Assert.Equal(expected, FileNamePolicy.GeneratedStem(input, "Entry"));
        Assert.Equal("Actor", FileNamePolicy.GeneratedStem(" ", "Actor"));
    }
}
