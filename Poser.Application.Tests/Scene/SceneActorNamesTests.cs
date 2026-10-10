using System.Text.Json;
using Poser.Application.Scene;
using Poser.Documents.Files;

namespace Poser.Application.Tests.Scene;

public sealed class SceneActorNamesTests
{
    [Theory]
    [InlineData("Actor (201)", false, "Actor")]
    [InlineData("Actor (201)", true, "Actor (201)")]
    public void Legacy_cleanup_does_not_strip_authored_suffixes(string name, bool authored, string expected)
    {
        var actor = new SceneActor { Name = name, NameIsDisplayName = authored };
        var copy = JsonSerializer.Deserialize<SceneActor>(JsonSerializer.Serialize(actor))!;
        Assert.Equal(expected, SceneActorNames.Resolve(copy));
    }
}
