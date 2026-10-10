using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Poser.Files;
using Poser.Library;
using Poser.Tests.Files;
using Poser.Documents.Files;
using Poser.Documents.Library;

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
