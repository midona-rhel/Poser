using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class PoseFilePersistenceTests
{
    [Fact]
    public void Current_wire_shape_round_trips_without_a_file_version()
    {
        const string json = """
        {
          "TypeName": "Brio Pose",
          "Description": "<current & compatible>",
          "Tags": [
            { "DisplayName": "sitting", "Name": "sitting", "Aliases": [], "IsToolGenerated": false }
          ],
          "Bones": {
            "j_kao": {
              "Position": "1.25, 2.5, -3.75",
              "Rotation": "0, 0.25, 0, 0.9682458",
              "Scale": "1, 1, 1"
            }
          },
          "FutureBrioMember": { "Ignored": true },
        }
        """;

        var parsed = AtomicPoseFileStore.Default.Parse(json);

        Assert.True(parsed.Succeeded, parsed.Failure?.Detail);
        Assert.NotNull(parsed.Pose);
        Assert.Equal(new[] { "sitting" }, parsed.Pose!.Tags);
        Assert.Equal(new Vector3(1.25f, 2.5f, -3.75f), parsed.Pose.Bones["j_kao"].Position);

        using var fixture = new StoreFixture();
        var written = AtomicPoseFileStore.Default.Write(parsed.Pose, fixture.Destination);

        Assert.True(written.Succeeded, written.Failure?.Detail);
        var output = File.ReadAllText(fixture.Destination);
        Assert.Contains("\n  \"TypeName\": \"Brio Pose\"", output);
        Assert.Contains("\"Position\": \"1.25, 2.5, -3.75\"", output);
        Assert.Contains("\"Description\": \"<current & compatible>\"", output);
        Assert.Contains("\"Tags\": [\n    \"sitting\"", output);
        Assert.DoesNotContain("FileVersion", output);
        Assert.DoesNotContain("FutureBrioMember", output);
    }

    [Fact]
    public void Lossy_compatibility_wrappers_keep_nullable_and_bool_contracts()
    {
        Assert.Null(PoseFile.FromJson("{ not json"));

        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var invalid = ValidPose();
        invalid.Bones["j_kao"].Position = new Vector3(float.NaN, 0, 0);

        Assert.False(invalid.Save(fixture.Destination));
        Assert.Equal("old", File.ReadAllText(fixture.Destination));
    }

    [Fact]
    public void Read_rejects_a_file_larger_than_32_mib_before_parsing()
    {
        using var fixture = new StoreFixture();
        using (var stream = new FileStream(fixture.Destination, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);

        var result = AtomicPoseFileStore.Default.Read(fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.SizeLimit, result.Failure?.Kind);
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void Read_enforces_json_depth_64(int nestedContainers, bool accepted)
    {
        var json = NestedUnknownJson(nestedContainers);

        var result = AtomicPoseFileStore.Default.Parse(json);

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
            Assert.Equal(PoseFileStoreFailureKind.Json, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_enforces_each_collection_bound()
    {
        var pose = ValidPose();
        Fill(pose.MainHand, PoseFileLimits.MaxEntriesPerCollection + 1, "mh");

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.CollectionSize, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_enforces_total_collection_bound_without_truncation()
    {
        var pose = new PoseFile();
        Fill(pose.Bones, PoseFileLimits.MaxEntriesPerCollection, "b");
        Fill(pose.MainHand, PoseFileLimits.MaxEntriesPerCollection, "m");
        Fill(pose.OffHand, PoseFileLimits.MaxEntriesPerCollection, "o");
        Fill(pose.Prop, PoseFileLimits.MaxEntriesPerCollection, "p");
        Fill(pose.Ornament, 1, "r");

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.TotalEntries, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_enforces_bone_name_bound()
    {
        var pose = new PoseFile();
        pose.Bones[new string('x', PoseFileLimits.MaxBoneNameCharacters + 1)] = ValidBone();

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.BoneName, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_enforces_tag_count_and_length_bounds()
    {
        var tooMany = ValidPose();
        tooMany.Tags = Enumerable.Range(0, PoseFileLimits.MaxTags + 1)
            .Select(i => $"tag-{i}")
            .ToList();
        var tooLong = ValidPose();
        tooLong.Tags = new List<string>
        {
            new('x', PoseFileLimits.MaxTagCharacters + 1),
        };

        var countResult = PoseFileValidation.Validate(tooMany);
        var lengthResult = PoseFileValidation.Validate(tooLong);

        Assert.Equal(PoseFileValidationFailureKind.TagCount, countResult.Failure?.Kind);
        Assert.Equal(PoseFileValidationFailureKind.TagLength, lengthResult.Failure?.Kind);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Validation_rejects_non_finite_used_numerics(float value)
    {
        var pose = ValidPose();
        pose.Bones["j_kao"].Scale = new Vector3(1, value, 1);

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.NonFiniteNumeric, result.Failure?.Kind);
    }

    [Fact]
    public void Numeric_converter_rejects_non_finite_wire_values()
    {
        const string json = """
        {
          "Bones": {
            "j_kao": {
              "Position": "NaN, 0, 0",
              "Rotation": "0, 0, 0, 1",
              "Scale": "1, 1, 1"
            }
          }
        }
        """;

        var result = AtomicPoseFileStore.Default.Parse(json);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Json, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_rejects_a_degenerate_used_quaternion()
    {
        var pose = ValidPose();
        pose.Bones["j_kao"].Rotation = new Quaternion(0, 0, 0, 0.0001f);

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.DegenerateQuaternion, result.Failure?.Kind);
    }

    [Fact]
    public void Validation_does_not_normalize_wire_values_and_accepts_additive_zero_scale()
    {
        var pose = ValidPose();
        pose.Bones["j_kao"].Rotation = new Quaternion(0, 0, 0, 2);
        pose.ModelDifference.Scale = Vector3.Zero;

        var result = PoseFileValidation.Validate(pose);

        Assert.True(result.Succeeded, result.Failure?.Detail);
        Assert.Equal(2, pose.Bones["j_kao"].Rotation.W);
        Assert.Equal(Vector3.Zero, pose.ModelDifference.Scale);
    }

    [Fact]
    public void Validation_rejects_anamnesis_alias_collisions_deterministically()
    {
        var pose = new PoseFile();
        pose.Bones["Head"] = ValidBone();
        pose.Bones["j_kao"] = ValidBone();

        var result = PoseFileValidation.Validate(pose);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileValidationFailureKind.AliasCollision, result.Failure?.Kind);
        Assert.Equal(
            "Bones 'Head' and 'j_kao' both map to 'j_kao'.",
            result.Failure?.Detail);
    }

    private static string NestedUnknownJson(int containers) =>
        "{\"Unknown\":" + new string('[', containers) + "0" + new string(']', containers) + "}";

    private static void Fill(
        Dictionary<string, PoseFile.BoneData> collection,
        int count,
        string prefix)
    {
        for (var i = 0; i < count; i++)
            collection[$"{prefix}{i}"] = ValidBone();
    }

    internal static PoseFile ValidPose()
    {
        var pose = new PoseFile();
        pose.Bones["j_kao"] = ValidBone();
        return pose;
    }

    private static PoseFile.BoneData ValidBone() => new()
    {
        Position = new Vector3(1, 2, 3),
        Rotation = Quaternion.Identity,
        Scale = Vector3.One,
    };
}

public sealed class AtomicPoseFileStoreTests
{
    public static IEnumerable<object[]> ExistingDestinationFailures()
    {
        yield return new object[] { PoseFileStorePhase.Serialize, PoseFileStoreFailureKind.Serialization };
        yield return new object[] { PoseFileStorePhase.CreateTemporary, PoseFileStoreFailureKind.TemporaryCreate };
        yield return new object[] { PoseFileStorePhase.WriteTemporary, PoseFileStoreFailureKind.TemporaryWrite };
        yield return new object[] { PoseFileStorePhase.FlushTemporary, PoseFileStoreFailureKind.TemporaryFlush };
        yield return new object[] { PoseFileStorePhase.ReopenTemporary, PoseFileStoreFailureKind.TemporaryReopen };
        yield return new object[] { PoseFileStorePhase.ReplaceDestination, PoseFileStoreFailureKind.Replace };
    }

    [Theory]
    [MemberData(nameof(ExistingDestinationFailures))]
    public void Every_precommit_failure_preserves_the_existing_destination(
        PoseFileStorePhase injected,
        PoseFileStoreFailureKind expected)
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 0x13, 0x37, 0x42, 0x7f };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var store = new AtomicPoseFileStore(phase =>
        {
            if (phase == injected)
                throw new IOException($"injected {phase}");
        });

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure?.Kind);
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Null(result.RecoveryEvidencePath);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Validation_failure_happens_before_the_destination_or_a_temp_is_touched()
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var pose = PoseFilePersistenceTests.ValidPose();
        pose.Bones["j_kao"].Position = new Vector3(float.NaN, 0, 0);

        var result = AtomicPoseFileStore.Default.Write(pose, fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure?.Kind);
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Move_failure_leaves_an_absent_destination_absent()
    {
        using var fixture = new StoreFixture();
        var store = new AtomicPoseFileStore(phase =>
        {
            if (phase == PoseFileStorePhase.MoveDestination)
                throw new IOException("injected move");
        });

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Move, result.Failure?.Kind);
        Assert.False(File.Exists(fixture.Destination));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Undeletable_temp_is_returned_as_recovery_evidence()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var store = new AtomicPoseFileStore(phase =>
        {
            if (phase is PoseFileStorePhase.ReplaceDestination or PoseFileStorePhase.CleanupTemporary)
                throw new IOException($"injected {phase}");
        });

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Replace, result.Failure?.Kind);
        Assert.NotNull(result.RecoveryEvidencePath);
        Assert.True(File.Exists(result.RecoveryEvidencePath));
        Assert.Equal(
            Path.GetFullPath(fixture.Root),
            Path.GetDirectoryName(Path.GetFullPath(result.RecoveryEvidencePath!)));
        Assert.NotEqual(Path.GetFullPath(fixture.Destination), Path.GetFullPath(result.RecoveryEvidencePath!));
    }

    [Fact]
    public void Successful_write_reopens_and_validates_before_replacing()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var phases = new List<PoseFileStorePhase>();
        var store = new AtomicPoseFileStore(phases.Add);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.True(result.Succeeded, result.Failure?.Detail);
        Assert.True(phases.IndexOf(PoseFileStorePhase.ReopenTemporary) <
                    phases.IndexOf(PoseFileStorePhase.ReplaceDestination));
        Assert.NotNull(PoseFile.Load(fixture.Destination));
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }
}

internal sealed class StoreFixture : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "poser-pose-store-tests", Guid.NewGuid().ToString("N"));

    public string Destination => Path.Combine(Root, "pose.pose");

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
            // A failed fixture cleanup must not hide the persistence assertion.
        }
    }
}
