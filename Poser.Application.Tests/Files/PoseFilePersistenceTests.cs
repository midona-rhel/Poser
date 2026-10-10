using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Poser.Domain.Library;
using Poser.Documents.Files;
using Poser.Documents.Library;

namespace Poser.Tests.Files;

public sealed class PoseFilePersistenceTests
{
    [Fact]
    public void Legacy_aliases_and_empty_helpers_work_in_full_and_metadata_reads()
    {
        using var fixture = new StoreFixture();
        const string json = """
            {"Bones":{
              "RootHead":{"Position":"1, 2, 3","Rotation":"0, 0, 0, 1","Scale":"1, 1, 1"},
              "Head":{"Position":"4, 5, 6","Rotation":"0, 0, 0, 1","Scale":"1, 1, 1"},
              "Met_2":{"Position":"0, 0, 0","Rotation":"1E-45, -1E-45, 0, 0","Scale":"0, 0, 0"}
            }}
            """;
        File.WriteAllText(fixture.Path, json);
        var read = AtomicPoseFileStore.Default.Read(fixture.Path);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
        read.Pose!.SanitizeBoneNames();
        Assert.Equal(new Vector3(4, 5, 6), read.Pose.Bones["j_kao"].Position);
        Assert.True(AtomicPoseFileStore.Default.Write(read.Pose, fixture.Path).Succeeded);
        Assert.True(AtomicPoseFileStore.Default.Read(fixture.Path).Succeeded);
        Assert.False(AtomicPoseFileStore.Default.Parse("""{"ModelDifference":{"Rotation":"0, 0, 0, 0"}}""").Succeeded);
    }

    [Fact]
    public void Bom_decimal_comma_and_version_fields_follow_the_interop_rules()
    {
        using var fixture = new StoreFixture();
        const string json = """{"Version":"1.0","Bones":{"j_kao":{"Position":"0.5, 1.25, 0","Rotation":"0, 0, 0, 1","Scale":"1, 1, 1"}}}""";
        File.WriteAllBytes(fixture.Path,
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());

        var read = AtomicPoseFileStore.Default.Read(fixture.Path);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(new Vector3(0.5f, 1.25f, 0), read.Pose!.Bones["j_kao"].Position);
        // "Version" is the author's pose version, not the format's.
        Assert.Equal(PoseLibraryMetadataStatus.Valid, PoseLibraryFileActions.Classify(metadata).Status);

        var comma = AtomicPoseFileStore.Default.Parse(json.Replace("0.5, 1.25, 0", "0,5, 1,25, 0"));
        Assert.False(comma.Succeeded);
        Assert.Contains("decimal comma", comma.Failure!.Detail);

        File.WriteAllText(fixture.Path, json.Replace(
            "{\"Version\"", $"{{\"FileVersion\":{PoseFile.CurrentFileVersion + 1},\"Version\""));
        Assert.Equal(PoseLibraryMetadataStatus.Future,
            PoseLibraryFileActions.Classify(AtomicPoseFileStore.Default.ReadMetadata(fixture.Path)).Status);
    }

    [Fact]
    public void A_valid_pose_round_trips_through_the_atomic_store()
    {
        using var fixture = new StoreFixture();
        var original = ValidPose();
        original.Author = "Ada";
        original.Tags = ["sample", "workflow"];
        original.Base64Image = "thumbnail";

        var write = AtomicPoseFileStore.Default.Write(original, fixture.Path);
        var read = AtomicPoseFileStore.Default.Read(fixture.Path);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(original.Bones["j_kao"].Position, read.Pose!.Bones["j_kao"].Position);
        Assert.Equal(original.Bones["j_kao"].Rotation, read.Pose.Bones["j_kao"].Rotation);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
        Assert.Equal("Ada", metadata.Author);
        Assert.Equal(new[] { "sample", "workflow" }, metadata.Tags);
        Assert.True(metadata.HasThumbnail);
    }

    [Fact]
    public void Brio_metadata_is_compatible_and_new_exports_keep_the_zero_model_default()
    {
        const string json = "{\"ModelId\":878,\"RaceSexId\":\"0101\",\"FaceID\":1,\"Bones\":{\"j_kao\":{\"Position\":\"0, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}}}";
        var parsed = AtomicPoseFileStore.Default.Parse(json);

        Assert.True(parsed.Succeeded, parsed.Failure?.Detail);
        Assert.Equal(878, parsed.Pose!.ModelId);
        Assert.Equal("0101", parsed.Pose.RaceSexId);
        Assert.Equal(1, parsed.Pose.FaceID);
        Assert.Equal(0, new PoseFile().ModelId);
    }

    [Fact]
    public void A_failed_write_keeps_the_old_bytes_and_reports_the_phase()
    {
        using var fixture = new StoreFixture();
        var old = new byte[] { 0x13, 0x37, 0x42 };
        File.WriteAllBytes(fixture.Path, old);
        var invalid = ValidPose();
        invalid.Bones["j_kao"].Position = new Vector3(float.NaN, 0, 0);
        Assert.False(AtomicPoseFileStore.Default.Write(invalid, fixture.Path).Succeeded);
        Assert.Equal(old, File.ReadAllBytes(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));

        var store = new AtomicPoseFileStore((phase, _) =>
        {
            if (phase == PoseFileStorePhase.WriteTemporary)
                throw new IOException("injected write failure");
        });

        var result = store.Write(ValidPose(), fixture.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.TemporaryWrite, result.Failure!.Kind);
        Assert.Equal(old, File.ReadAllBytes(fixture.Path));
        Assert.Empty(result.RecoveryEvidencePaths);
    }

    [Fact]
    public void Atomic_commit_reports_phase_failures_and_preserves_recovery_evidence()
    {
        var phaseCases = new[]
        {
            (Phase: PoseFileStorePhase.ReplaceDestination,
                Existing: true, Kind: PoseFileStoreFailureKind.Replace),
            (Phase: PoseFileStorePhase.MoveDestination,
                Existing: false, Kind: PoseFileStoreFailureKind.Move),
        };

        foreach (var testCase in phaseCases)
        {
            using var fixture = new StoreFixture();
            if (testCase.Existing)
                File.WriteAllText(fixture.Path, "old destination");

            var store = new AtomicPoseFileStore((phase, _) =>
            {
                if (phase == testCase.Phase)
                    throw new IOException("injected commit failure");
            });

            var result = store.Write(ValidPose(), fixture.Path);

            Assert.False(result.Succeeded);
            Assert.Equal(testCase.Kind, result.Failure!.Kind);
            var temporary = Assert.Single(result.RecoveryEvidencePaths);
            Assert.EndsWith(".tmp", temporary, StringComparison.Ordinal);
            if (testCase.Existing)
                Assert.Equal("old destination", File.ReadAllText(fixture.Path));
            else
                Assert.False(File.Exists(fixture.Path));
        }
    }

    public static PoseFile ValidPose() => new()
    {
        Bones =
        {
            ["j_kao"] = new PoseFile.BoneData
            {
                Position = new Vector3(1, 2, 3),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            },
        },
    };
}

internal sealed class StoreFixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "poser-pose-store-tests", Guid.NewGuid().ToString("N"));

    public string Path => System.IO.Path.Combine(Root, "pose.pose");

    public StoreFixture() => Directory.CreateDirectory(Root);

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
