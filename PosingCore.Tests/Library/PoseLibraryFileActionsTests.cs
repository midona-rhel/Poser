using System;
using System.Collections.Generic;
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
    // ── rename ───────────────────────────────────────────────────────────

    [Fact]
    public void Rename_moves_the_file_and_answers_the_new_path()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("original", PoseFilePersistenceTests.ValidPose());

        var result = PoseLibraryFileActions.Default.Rename(path, "renamed");

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Rename, result.Kind);
        Assert.Equal(Path.Combine(fixture.Root, "renamed.pose"), result.ResultPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(result.ResultPath!));
        Assert.True(AtomicPoseFileStore.Default.Read(result.ResultPath!).Succeeded);
    }

    [Fact]
    public void Rename_refuses_empty_invalid_and_taken_names_without_touching_the_source()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("original", PoseFilePersistenceTests.ValidPose());
        fixture.WritePose("taken", PoseFilePersistenceTests.ValidPose());

        foreach (var name in new[] { "", "   ", "bad/name", "bad:name", "taken" })
        {
            var result = PoseLibraryFileActions.Default.Rename(path, name);
            Assert.False(result.Succeeded);
            Assert.Equal(PoseLibraryFileActionKind.Rename, result.Kind);
            Assert.NotEmpty(result.Detail);
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void Rename_to_the_same_name_is_a_success_answering_the_standing_path()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("same", PoseFilePersistenceTests.ValidPose());

        var result = PoseLibraryFileActions.Default.Rename(path, "same");

        Assert.True(result.Succeeded);
        Assert.Equal(path, result.ResultPath);
        Assert.True(File.Exists(path));
    }

    // ── move ─────────────────────────────────────────────────────────────

    [Fact]
    public void Move_relocates_the_file_between_folders()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("mover", PoseFilePersistenceTests.ValidPose());
        var destination = Path.Combine(fixture.Root, "sub");
        Directory.CreateDirectory(destination);

        var result = PoseLibraryFileActions.Default.Move(path, destination);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Move, result.Kind);
        Assert.Equal(Path.Combine(destination, "mover.pose"), result.ResultPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(result.ResultPath!));
    }

    [Fact]
    public void Move_refuses_a_missing_destination_and_a_taken_name_without_touching_the_source()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("mover", PoseFilePersistenceTests.ValidPose());
        var taken = Path.Combine(fixture.Root, "sub");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "mover.pose"), "{}");

        var missing = PoseLibraryFileActions.Default.Move(
            path, Path.Combine(fixture.Root, "nowhere"));
        var collision = PoseLibraryFileActions.Default.Move(path, taken);

        Assert.False(missing.Succeeded);
        Assert.NotEmpty(missing.Detail);
        Assert.False(collision.Succeeded);
        Assert.NotEmpty(collision.Detail);
        Assert.True(File.Exists(path));
    }

    // ── delete ───────────────────────────────────────────────────────────

    [Fact]
    public void Delete_removes_the_file_and_a_second_delete_still_succeeds()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("victim", PoseFilePersistenceTests.ValidPose());

        var first = PoseLibraryFileActions.Default.Delete(path);
        var second = PoseLibraryFileActions.Default.Delete(path);

        Assert.True(first.Succeeded, first.Detail);
        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Delete, first.Kind);
        Assert.False(File.Exists(path));
    }

    // ── quarantine / restore ─────────────────────────────────────────────

    [Fact]
    public void Quarantine_moves_the_file_under_the_quarantine_folder_and_restore_brings_it_back()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WriteRaw("broken", "{ nope");

        var quarantined = PoseLibraryFileActions.Default.Quarantine(path);

        Assert.True(quarantined.Succeeded, quarantined.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Quarantine, quarantined.Kind);
        Assert.Equal(
            Path.Combine(
                fixture.Root,
                PoseLibraryFileActions.QuarantineFolderName,
                "broken.pose"),
            quarantined.ResultPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(quarantined.ResultPath!));

        var restored = PoseLibraryFileActions.Default.Restore(quarantined.ResultPath!);

        Assert.True(restored.Succeeded, restored.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Restore, restored.Kind);
        Assert.Equal(path, restored.ResultPath);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(quarantined.ResultPath!));
    }

    [Fact]
    public void Quarantine_suffixes_a_colliding_name_instead_of_overwriting_evidence()
    {
        using var fixture = new ActionsFixture();
        var first = fixture.WriteRaw("dupe", "{ one");
        Assert.True(PoseLibraryFileActions.Default.Quarantine(first).Succeeded);
        var second = fixture.WriteRaw("dupe", "{ two");

        var result = PoseLibraryFileActions.Default.Quarantine(second);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(
            Path.Combine(
                fixture.Root,
                PoseLibraryFileActions.QuarantineFolderName,
                "dupe (2).pose"),
            result.ResultPath);
        Assert.True(File.Exists(result.ResultPath!));
    }

    [Fact]
    public void Restore_refuses_a_file_that_is_not_quarantined()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("free", PoseFilePersistenceTests.ValidPose());

        var result = PoseLibraryFileActions.Default.Restore(path);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseLibraryFileActionKind.Restore, result.Kind);
        Assert.NotEmpty(result.Detail);
        Assert.True(File.Exists(path));
    }

    // ── probe (retry) ────────────────────────────────────────────────────

    [Fact]
    public void Probe_answers_the_typed_metadata_status_for_every_entry_shape()
    {
        using var fixture = new ActionsFixture();
        var valid = fixture.WritePose("valid", PoseFilePersistenceTests.ValidPose());
        var corrupt = fixture.WriteRaw("corrupt", "{ nope");
        var future = PoseFilePersistenceTests.ValidPose();
        future.Version = "future-2";
        var futurePath = fixture.WritePose("future", future);
        var oversized = fixture.WriteOversized("oversized");

        Assert.Equal(
            PoseLibraryMetadataStatus.Valid,
            ProbeStatus(valid));
        Assert.Equal(
            PoseLibraryMetadataStatus.Corrupt,
            ProbeStatus(corrupt));
        Assert.Equal(
            PoseLibraryMetadataStatus.Future,
            ProbeStatus(futurePath));
        Assert.Equal(
            PoseLibraryMetadataStatus.Oversized,
            ProbeStatus(oversized));

        var failed = PoseLibraryFileActions.Default.Probe(corrupt);
        Assert.True(failed.Succeeded);
        Assert.NotEmpty(failed.Detail);
    }

    // A shot is a different document with a different codec. Re-probing one
    // with the POSE codec answers Corrupt however healthy the shot is, so a
    // retry would permanently condemn a file that reads perfectly.
    [Fact]
    public void Probe_reads_a_shot_through_the_scene_codec_not_the_pose_codec()
    {
        using var fixture = new ActionsFixture();
        var valid = fixture.WriteScene("shot", SceneFileStoreTests.ValidScene());
        var corrupt = fixture.WriteRawScene("broken", "{ nope");

        Assert.Equal(PoseLibraryMetadataStatus.Valid, ProbeStatus(valid));
        Assert.Equal(PoseLibraryMetadataStatus.Corrupt, ProbeStatus(corrupt));

        var refused = PoseLibraryFileActions.Default.Probe(corrupt);
        Assert.True(refused.Succeeded);
        Assert.NotEmpty(refused.Detail);
    }

    private static PoseLibraryMetadataStatus ProbeStatus(string path)
    {
        var result = PoseLibraryFileActions.Default.Probe(path);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(PoseLibraryFileActionKind.Probe, result.Kind);
        Assert.NotNull(result.ProbeStatus);
        return result.ProbeStatus!.Value;
    }

    // ── metadata authoring ───────────────────────────────────────────────

    [Fact]
    public void EditMetadata_round_trips_author_and_normalized_tags_through_the_atomic_store()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("authored", PoseFilePersistenceTests.ValidPose());

        var result = PoseLibraryFileActions.Default.EditMetadata(
            path, "  Midona  ", new[] { " one", "two ", "ONE", "", "  " });

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(PoseLibraryFileActionKind.EditMetadata, result.Kind);

        var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
        Assert.Equal("Midona", metadata.Author);
        Assert.Equal(new[] { "one", "two" }, metadata.Tags);
        // The document must remain a fully valid pose after the edit.
        Assert.True(AtomicPoseFileStore.Default.Read(path).Succeeded);
    }

    [Fact]
    public void EditMetadata_clears_author_and_tags_when_asked_to()
    {
        using var fixture = new ActionsFixture();
        var pose = PoseFilePersistenceTests.ValidPose();
        pose.Author = "Someone";
        pose.Tags = ["kept"];
        var path = fixture.WritePose("cleared", pose);

        var result = PoseLibraryFileActions.Default.EditMetadata(
            path, "", Array.Empty<string>());

        Assert.True(result.Succeeded, result.Detail);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
        Assert.True(metadata.Succeeded);
        Assert.Null(metadata.Author);
        Assert.Empty(metadata.Tags);
    }

    [Fact]
    public void EditMetadata_refuses_corrupt_and_oversized_files_with_the_typed_read_failure()
    {
        using var fixture = new ActionsFixture();
        var corrupt = fixture.WriteRaw("corrupt", "{ nope");
        var corruptBytes = File.ReadAllBytes(corrupt);
        var oversized = fixture.WriteOversized("oversized");

        var corruptResult = PoseLibraryFileActions.Default.EditMetadata(
            corrupt, "A", Array.Empty<string>());
        var oversizedResult = PoseLibraryFileActions.Default.EditMetadata(
            oversized, "A", Array.Empty<string>());

        Assert.False(corruptResult.Succeeded);
        Assert.NotEmpty(corruptResult.Detail);
        Assert.False(oversizedResult.Succeeded);
        Assert.NotEmpty(oversizedResult.Detail);
        // The refused file is untouched — no partial author write.
        Assert.Equal(corruptBytes, File.ReadAllBytes(corrupt));
    }

    [Fact]
    public void EditMetadata_preserves_every_root_member_Poser_does_not_model()
    {
        // The members Brio writes and consumes at the document root, plus a
        // nested container standing in for whatever the format gains next.
        // None of them is named by Poser.Files.PoseFile.
        using var fixture = new ActionsFixture();
        var path = fixture.WriteRaw("foreign", """
        {
          "TypeName": "Brio Pose",
          "FileVersion": 3,
          "Author": "Brio",
          "GameVersion": "2026.07.15.0000.0000",
          "FutureBrioMember": { "Nested": [1, 2, 3], "Deeper": { "Flag": true } },
          "Tags": [{ "DisplayName": "sitting", "Name": "sitting" }],
          "Bones": {
            "j_kao": {
              "Position": "1.25, 2.5, -3.75",
              "Rotation": "0, 0.25, 0, 0.9682458",
              "Scale": "1, 1, 1"
            }
          }
        }
        """);

        var result = PoseLibraryFileActions.Default.EditMetadata(
            path, "Midona", new[] { "standing" });

        Assert.True(result.Succeeded, result.Detail);
        using var rewritten = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = rewritten.RootElement;

        // The edited members took the new values...
        Assert.Equal("Midona", root.GetProperty("Author").GetString());
        Assert.Equal(
            new[] { "standing" },
            root.GetProperty("Tags").EnumerateArray()
                .Select(tag => tag.GetString())
                .ToArray());

        // ...and every member Poser does not model survived verbatim.
        Assert.Equal(3, root.GetProperty("FileVersion").GetInt32());
        Assert.Equal(
            "2026.07.15.0000.0000", root.GetProperty("GameVersion").GetString());
        Assert.Equal(
            """{"Nested":[1,2,3],"Deeper":{"Flag":true}}""",
            JsonSerializer.Serialize(root.GetProperty("FutureBrioMember")));

        // The rewritten document is still one the codec fully accepts.
        Assert.True(AtomicPoseFileStore.Default.Read(path).Succeeded);
    }

    [Fact]
    public void EditMetadata_refuses_a_future_versioned_document_untouched()
    {
        using var fixture = new ActionsFixture();
        var future = PoseFilePersistenceTests.ValidPose();
        future.Version = "future-2";
        var path = fixture.WritePose("future", future);
        var before = File.ReadAllBytes(path);

        var result = PoseLibraryFileActions.Default.EditMetadata(
            path, "Midona", new[] { "standing" });

        Assert.False(result.Succeeded);
        Assert.Contains("future-2", result.Detail, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void EditMetadata_refuses_a_tag_set_beyond_the_codec_bound()
    {
        using var fixture = new ActionsFixture();
        var path = fixture.WritePose("bounded", PoseFilePersistenceTests.ValidPose());
        var tags = Enumerable.Range(0, PoseFileLimits.MaxTags + 1)
            .Select(i => $"tag-{i}")
            .ToArray();

        var result = PoseLibraryFileActions.Default.EditMetadata(path, null, tags);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Detail);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
        Assert.True(metadata.Succeeded);
        Assert.Empty(metadata.Tags);
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class ActionsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "poser-library-actions-tests",
            Guid.NewGuid().ToString("N"));

        public ActionsFixture() => Directory.CreateDirectory(Root);

        public string WritePose(string name, PoseFile pose)
        {
            var path = Path.Combine(Root, name + ".pose");
            var result = AtomicPoseFileStore.Default.Write(pose, path);
            Assert.True(result.Succeeded, result.Failure?.Detail);
            return path;
        }

        public string WriteScene(string name, SceneFile scene)
        {
            var path = Path.Combine(Root, name + SceneFile.Extension);
            var result = SceneFileStore.Default.Write(scene, path);
            Assert.True(result.Succeeded, result.Failure?.Detail);
            return path;
        }

        // Bytes, not File.WriteAllText(…, Encoding.UTF8): that helper emits a
        // BOM, and the codec's preflight reader would reject the fixture
        // before it ever reached what the test is about.
        public string WriteRaw(string name, string json)
        {
            var path = Path.Combine(Root, name + ".pose");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            return path;
        }

        public string WriteRawScene(string name, string json)
        {
            var path = Path.Combine(Root, name + SceneFile.Extension);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            return path;
        }

        public string WriteOversized(string name)
        {
            var path = Path.Combine(Root, name + ".pose");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
