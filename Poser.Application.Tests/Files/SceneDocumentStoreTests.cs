using System.IO;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class SceneDocumentStoreTests
{
    [Theory]
    [InlineData(".xivs", true)]
    [InlineData(".JSON", false)]
    public void Format_routing_preserves_supported_content_and_reports_conversion_loss(string extension, bool keepsActors)
    {
        using var fixture = new SceneFixture();
        var path = Path.ChangeExtension(fixture.Path, extension);
        var scene = SceneFileStoreTests.ValidScene();
        scene.Lights[0].Attachment = null;
        scene.Lights[0].Light!.IsOn = true;
        var documents = new SceneDocumentStore();
        var written = documents.Write(scene, path);
        Assert.True(written.Outcome.Succeeded, written.Outcome.Failure?.Detail);
        if (keepsActors)
            Assert.Empty(written.Notes);
        else
            Assert.Contains(written.Notes, note => note.Contains("actor"));

        var read = documents.Read(path);
        Assert.True(read.Outcome.Succeeded, read.Outcome.Failure?.Detail);
        Assert.Equal(scene.Lights[0].Light!.Name, Assert.Single(read.Outcome.Scene!.Lights).Light!.Name);
        if (keepsActors)
            Assert.Equal(scene.Actors[0].Name, Assert.Single(read.Outcome.Scene.Actors).Name);
        else
            Assert.Empty(read.Outcome.Scene.Actors);
    }
}
