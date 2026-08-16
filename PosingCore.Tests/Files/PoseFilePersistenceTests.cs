using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class PoseFilePersistenceTests
{
    [Fact]
    public void A_valid_pose_round_trips_through_the_atomic_store()
    {
        using var fixture = new StoreFixture();
        var original = ValidPose();

        var write = AtomicPoseFileStore.Default.Write(original, fixture.Path);
        var read = AtomicPoseFileStore.Default.Read(fixture.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(original.Bones["j_kao"].Position, read.Pose!.Bones["j_kao"].Position);
        Assert.Equal(original.Bones["j_kao"].Rotation, read.Pose.Bones["j_kao"].Rotation);
    }

    [Fact]
    public void Malformed_and_invalid_numeric_documents_are_rejected_before_materialization()
    {
        Assert.False(AtomicPoseFileStore.Default.Parse("{ not json").Succeeded);

        var invalid = "{\"Bones\":{\"j_kao\":{\"Position\":\"NaN, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}}}";
        var result = AtomicPoseFileStore.Default.Parse(invalid);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure!.Kind);
        Assert.Equal(PoseFileValidationFailureKind.NonFiniteNumeric, result.Failure.ValidationFailure!.Kind);
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
    public void An_invalid_export_preserves_the_existing_destination()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Path, "old destination");
        var invalid = ValidPose();
        invalid.Bones["j_kao"].Position = new Vector3(float.NaN, 0, 0);

        var result = AtomicPoseFileStore.Default.Write(invalid, fixture.Path);

        Assert.False(result.Succeeded);
        Assert.Equal("old destination", File.ReadAllText(fixture.Path));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public void An_atomic_write_failure_keeps_the_old_bytes_and_reports_the_phase()
    {
        using var fixture = new StoreFixture();
        var old = new byte[] { 0x13, 0x37, 0x42 };
        File.WriteAllBytes(fixture.Path, old);
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
    public void Parser_boundaries_reject_oversize_depth_and_collection_inputs()
    {
        var deep = "{}";
        for (var i = 0; i < PoseFileLimits.MaxJsonDepth + 1; i++)
            deep = $"{{\"x\":{deep}}}";

        var depth = AtomicPoseFileStore.Default.Parse(deep);
        var entries = string.Join(",", Enumerable.Repeat("\"same\":{}", PoseFileLimits.MaxEntriesPerCollection + 1));
        var collection = AtomicPoseFileStore.Default.Parse($"{{\"Bones\":{{{entries}}}}}");

        Assert.False(depth.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Json, depth.Failure!.Kind);
        Assert.False(collection.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.CollectionSize,
            collection.Failure!.ValidationFailure!.Kind);
    }

    [Fact]
    public void File_security_rejects_oversize_input_before_parsing()
    {
        using var fixture = new StoreFixture();
        using (var stream = new FileStream(fixture.Path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);

        var result = AtomicPoseFileStore.Default.Read(fixture.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.SizeLimit, result.Failure!.Kind);
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
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "poser-pose-store-tests", Guid.NewGuid().ToString("N"));

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
