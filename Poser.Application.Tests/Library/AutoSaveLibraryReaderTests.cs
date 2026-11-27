using System;
using System.IO;
using Poser.Documents.Files;
using Poser.Documents.Library;

namespace Poser.Application.Tests.Library;

/// <summary>The Auto-saves tab's listing of scene and old pose autosave folders.</summary>
public sealed class AutoSaveLibraryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poser-autosave-reader-{Guid.NewGuid():N}");

    public AutoSaveLibraryReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not a failed test.
        }
    }

    [Fact]
    public void Scene_backups_are_listed_even_without_a_legacy_pose_directory()
    {
        var scenes = Path.Combine(_root, "scenes");
        var store = new Poser.Documents.AutoSave.SceneAutoSaveStore(scenes, _ => { });
        var scene = new SceneFile { SceneId = Guid.NewGuid(), TerritoryId = 10, PlaceName = "Test map" };
        var (path, result) = store.Write(scene, DateTime.Now);
        Assert.True(result.Succeeded);
        var entry = Assert.Single(Assert.Single(new AutoSaveLibraryReader().Read(
            Path.Combine(_root, "missing-legacy"), scenes)).Entries);
        Assert.Equal(path, entry.FilePath);
    }
}
