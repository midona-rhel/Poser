using Poser.Game.Integration;

namespace Poser.Game.Tests.Integration;

public sealed class InheritedResourcePathsTests
{
    [Fact]
    public void Missing_loaded_paths_resolve_against_the_source_without_losing_valid_redirects()
    {
        var tree = new Dictionary<string, HashSet<string>>
        {
            [""] = ["base.sklb", "hair.mdl"],
            ["mods/body.mdl"] = ["body.mdl"],
        };
        var requested = new List<string>();
        var result = IntegrationRuntimePort.CaptureRedirects(tree, path =>
        {
            requested.Add(path);
            return path == "hair.mdl" ? "mods/hair.mdl" : path;
        });

        Assert.Equal(2, requested.Count);
        Assert.DoesNotContain("base.sklb", result.Keys);
        Assert.Equal("mods/hair.mdl", result["hair.mdl"]);
        Assert.Equal("mods/body.mdl", result["body.mdl"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Unresolved_skeleton_refuses_capture_instead_of_installing_an_empty_redirect()
    {
        var tree = new Dictionary<string, HashSet<string>> { [""] = ["base.sklb"] };
        Assert.Throws<InvalidOperationException>(() =>
            IntegrationRuntimePort.CaptureRedirects(tree, _ => ""));
    }
}
