using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Poser.Files;
using Poser.Library;
using Poser.Tests.Files;

namespace Poser.Tests.Library;

public sealed class PoseLibraryFileActionsTests
{
    [Fact]
    public void Rename_and_move_enforce_bare_names_collisions_and_existing_destinations()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("original", PoseFilePersistenceTests.ValidPose());
        fixture.WritePose("taken", PoseFilePersistenceTests.ValidPose());
        var sub = Path.Combine(fixture.Root, "sub");
        Directory.CreateDirectory(sub);

        var bad = PoseLibraryFileActions.Default.Rename(path, "taken");
        var renamed = PoseLibraryFileActions.Default.Rename(path, " renamed ");
        var moved = PoseLibraryFileActions.Default.Move(renamed.ResultPath!, sub);

        Assert.False(bad.Succeeded);
        Assert.True(renamed.Succeeded, renamed.Detail);
        Assert.True(moved.Succeeded, moved.Detail);
        Assert.Equal(Path.Combine(sub, "renamed.pose"), moved.ResultPath);
        Assert.True(File.Exists(moved.ResultPath!));
    }

    [Fact]
    public void Quarantine_and_restore_preserve_evidence_and_suffix_collisions()
    {
        using var fixture = new ActionsFixture();
        var first = fixture.WriteRaw("broken", "{ one");
        var firstQuarantine = PoseLibraryFileActions.Default.Quarantine(first);
        var second = fixture.WriteRaw("broken", "{ two");
        var secondQuarantine = PoseLibraryFileActions.Default.Quarantine(second);

        Assert.True(firstQuarantine.Succeeded);
        Assert.True(secondQuarantine.Succeeded);
        Assert.True(secondQuarantine.ResultPath!.EndsWith(
            "broken (2).pose", StringComparison.Ordinal));
        var restored = PoseLibraryFileActions.Default.Restore(firstQuarantine.ResultPath!);

        Assert.True(restored.Succeeded, restored.Detail);
        Assert.True(File.Exists(restored.ResultPath!));
        Assert.False(File.Exists(firstQuarantine.ResultPath!));
        Assert.False(PoseLibraryFileActions.Default.Restore(restored.ResultPath!).Succeeded);
    }

    [Fact]
    public void Probe_retries_each_document_through_its_own_codec_and_reports_typed_status()
    {
        using var fixture = new ActionsFixture();
        var valid = fixture.WritePose("valid", PoseFilePersistenceTests.ValidPose());
        var corrupt = fixture.WriteRaw("corrupt", "{ nope");
        var futurePose = PoseFilePersistenceTests.ValidPose();
        futurePose.Version = "future-2";
        var future = fixture.WritePose("future", futurePose);
        var scene = fixture.WriteScene("scene", SceneFileStoreTests.ValidScene());

        Assert.Equal(PoseLibraryMetadataStatus.Valid, Probe(valid));
        Assert.Equal(PoseLibraryMetadataStatus.Corrupt, Probe(corrupt));
        Assert.Equal(PoseLibraryMetadataStatus.Future, Probe(future));
        Assert.Equal(PoseLibraryMetadataStatus.Valid, Probe(scene));
    }

    [Fact]
    public void Metadata_import_normalizes_fields_preserves_unknown_members_and_refuses_unsafe_reads()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WriteRaw("foreign", """
        {
          "Author": "Brio",
          "FutureBrioMember": { "Nested": [1, 2, 3] },
          "Bones": { "j_kao": { "Position": "0, 0, 0", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1" } }
        }
        """);
        var beforeCorrupt = fixture.WriteRaw("corrupt", "{ nope");
        var before = File.ReadAllBytes(beforeCorrupt);

        var edited = PoseLibraryFileActions.Default.EditMetadata(
            path, "  Midona  ", new[] { " one", "two ", "ONE", "" });
        var refused = PoseLibraryFileActions.Default.EditMetadata(
            beforeCorrupt, "Midona", Array.Empty<string>());

        Assert.True(edited.Succeeded, edited.Detail);
        Assert.False(refused.Succeeded);
        Assert.Equal(before, File.ReadAllBytes(beforeCorrupt));
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("Midona", json.RootElement.GetProperty("Author").GetString());
        Assert.Equal(new[] { 1, 2, 3 }, json.RootElement
            .GetProperty("FutureBrioMember").GetProperty("Nested")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.True(AtomicPoseFileStore.Default.Read(path).Succeeded);
    }

    private static PoseLibraryMetadataStatus Probe(string path)
    {
        var result = PoseLibraryFileActions.Default.Probe(path);
        Assert.True(result.Succeeded, result.Detail);
        return result.ProbeStatus!.Value;
    }

    private sealed class ActionsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "poser-library-actions-tests",
            Guid.NewGuid().ToString("N"));

        public ActionsFixture() => Directory.CreateDirectory(Root);

        public string WritePose(string name, PoseFile pose)
        {
            var path = Path.Combine(Root, name + ".pose");
            Assert.True(AtomicPoseFileStore.Default.Write(pose, path).Succeeded);
            return path;
        }

        public string WriteScene(string name, SceneFile scene)
        {
            var path = Path.Combine(Root, name + SceneFile.Extension);
            Assert.True(SceneFileStore.Default.Write(scene, path).Succeeded);
            return path;
        }

        public string WriteRaw(string name, string json)
        {
            var path = Path.Combine(Root, name + ".pose");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
