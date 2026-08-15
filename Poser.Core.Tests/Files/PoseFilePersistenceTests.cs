using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class PoseFilePersistenceTests
{
    [Fact]
    public void Brio_authored_input_golden_is_accepted_without_a_file_version()
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

    }

    [Fact]
    public void Emitted_wire_golden_uses_the_accepted_Brio_conventions()
    {
        var expected = """
        {
          "TypeName": "Brio Pose",
          "FileVersion": 3,
          "GameVersion": "",
          "Author": null,
          "Description": "<current & compatible>",
          "Version": null,
          "Base64Image": null,
          "Tags": [
            "sitting"
          ],
          "ModelId": 0,
          "RaceSexId": null,
          "FaceID": null,
          "ModelDifference": {
            "Position": "0, 0, 0",
            "Rotation": "0, 0, 0, 1",
            "Scale": "0, 0, 0"
          },
          "ModelAbsoluteValues": {
            "Position": "0, 0, 0",
            "Rotation": "0, 0, 0, 1",
            "Scale": "0, 0, 0"
          },
          "Bones": {
            "j_kao": {
              "Position": "1.25, 2.5, -3.75",
              "Rotation": "0, 0.25, 0, 0.9682458",
              "Scale": "1, 1, 1"
            }
          },
          "MainHand": {},
          "OffHand": {},
          "Prop": {},
          "Ornament": {},
          "Position": "0, 0, 0",
          "Rotation": "0, 0, 0, 0",
          "Scale": "0, 0, 0"
        }
        """.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string input = """
        {
          "Description": "<current & compatible>",
          "Tags": [{ "DisplayName": "sitting", "Name": "sitting" }],
          "Bones": {
            "j_kao": {
              "Position": "1.25, 2.5, -3.75",
              "Rotation": "0, 0.25, 0, 0.9682458",
              "Scale": "1, 1, 1"
            }
          },
        }
        """;
        var parsed = AtomicPoseFileStore.Default.Parse(input);
        Assert.True(parsed.Succeeded, parsed.Failure?.Detail);
        using var fixture = new StoreFixture();

        var written = AtomicPoseFileStore.Default.Write(parsed.Pose!, fixture.Destination);

        Assert.True(written.Succeeded, written.Failure?.Detail);
        var output = Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Destination))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, output);
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

    [Fact]
    public void ReadMetadata_streams_large_image_payload_with_a_bounded_read_buffer()
    {
        var image = new string('A', 8 * 1024 * 1024);
        var bones = string.Join(
            ",",
            Enumerable.Range(0, PoseFileLimits.MaxEntriesPerCollection)
                .Select(i =>
                    $"\"bone-{i}\":{{\"Position\":\"1, 2, 3\"," +
                    "\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}"));
        var json = Encoding.UTF8.GetBytes(
            "{\"Author\":\"streamed\",\"Base64Image\":\"" + image +
            "\",\"Bones\":{" + bones + "}}");
        var fileSystem = new BoundedReadPoseFileSystem(json);
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.ReadMetadata("large.pose");

        Assert.True(result.Succeeded, result.Failure?.Detail);
        Assert.Equal("streamed", result.Author);
        Assert.True(result.HasThumbnail);
        Assert.True(fileSystem.ReadCount > 1);
        Assert.All(fileSystem.ReadSizes, size =>
            Assert.InRange(size, 1, AtomicPoseFileStore.MetadataBufferSize));
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("leading-zero")]
    public void Read_and_ReadMetadata_accept_long_numeric_strings_identically(
        string componentKind)
    {
        var component = componentKind == "whitespace"
            ? new string(' ', 2048) + "1" + new string(' ', 2048)
            : new string('0', 2048) + "1";
        var json = NumericPoseJson($"{component}, 2, 3");

        var ordinary = AtomicPoseFileStore.Default.Parse(json);
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, json);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Destination);

        Assert.True(ordinary.Succeeded, ordinary.Failure?.Detail);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
    }

    [Fact]
    public void Read_and_ReadMetadata_accept_the_converter_numeric_culture_identically()
    {
        var json = NumericPoseJson("1,234, 2, 3");

        var ordinary = AtomicPoseFileStore.Default.Parse(json);
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, json);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Destination);

        Assert.True(ordinary.Succeeded, ordinary.Failure?.Detail);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
    }

    [Fact]
    public void Read_and_ReadMetadata_accept_a_numeric_string_split_at_the_stream_buffer_boundary()
    {
        var component = new string('0', AtomicPoseFileStore.MetadataBufferSize + 17) + "1";
        var json = NumericPoseJson($"{component}, 2, 3");
        var bytes = Encoding.UTF8.GetBytes(json);

        var ordinary = AtomicPoseFileStore.Default.Parse(json);
        var fileSystem = new BoundedReadPoseFileSystem(bytes);
        var metadata = new AtomicPoseFileStore(fileSystem).ReadMetadata("boundary.pose");

        Assert.True(ordinary.Succeeded, ordinary.Failure?.Detail);
        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
        Assert.True(fileSystem.ReadCount > 1);
        Assert.All(fileSystem.ReadSizes, size =>
            Assert.InRange(size, 1, AtomicPoseFileStore.MetadataBufferSize));
    }

    [Theory]
    [InlineData("1x, 2, 3", PoseFileStoreFailureKind.Json, null)]
    [InlineData("1e999, 2, 3", PoseFileStoreFailureKind.Json, null)]
    [InlineData("NaN, 2, 3", PoseFileStoreFailureKind.Json, null)]
    [InlineData("0, 0, 0, 0", PoseFileStoreFailureKind.Validation,
        PoseFileValidationFailureKind.DegenerateQuaternion)]
    public void Read_and_ReadMetadata_reject_invalid_numeric_strings_identically(
        string numeric,
        PoseFileStoreFailureKind expectedFailureKind,
        PoseFileValidationFailureKind? expectedValidationKind)
    {
        var json = NumericPoseJson(
            position: "1, 2, 3",
            rotation: numeric);
        var ordinary = AtomicPoseFileStore.Default.Parse(json);
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, json);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Destination);

        Assert.False(ordinary.Succeeded);
        Assert.False(metadata.Succeeded);
        Assert.Equal(expectedFailureKind, ordinary.Failure?.Kind);
        Assert.Equal(expectedFailureKind, metadata.Failure?.Kind);
        Assert.Equal(expectedValidationKind, ordinary.Failure?.ValidationFailure?.Kind);
        Assert.Equal(expectedValidationKind, metadata.Failure?.ValidationFailure?.Kind);
    }

    private static string NumericPoseJson(
        string position,
        string? rotation = null) =>
        $$"""
        {
          "Bones": {
            "j_kao": {
              "Position": "{{position}}",
              "Rotation": "{{rotation ?? "0, 0, 0, 1"}}",
              "Scale": "1, 1, 1"
            }
          }
        }
        """;

    [Fact]
    public void Read_rejects_an_empty_file_before_decoding()
    {
        using var fixture = new StoreFixture();
        using (File.Create(fixture.Destination))
        {
        }

        var result = AtomicPoseFileStore.Default.Read(fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure?.Kind);
        Assert.Equal(
            PoseFileValidationFailureKind.Document,
            result.Failure?.ValidationFailure?.Kind);
    }

    [Fact]
    public void Read_accepts_a_valid_file_at_exactly_32_mib()
    {
        using var fixture = new StoreFixture();
        using (var stream = new FileStream(
                   fixture.Destination, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write("{}"u8);
            var spaces = new byte[8192];
            Array.Fill(spaces, (byte)' ');
            var remaining = PoseFileLimits.MaxFileBytes - stream.Position;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, spaces.Length);
                stream.Write(spaces, 0, count);
                remaining -= count;
            }
        }

        var result = AtomicPoseFileStore.Default.Read(fixture.Destination);

        Assert.True(result.Succeeded, result.Failure?.Detail);
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
    public void ReadMetadata_reuses_the_shared_json_depth_limit()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(
            fixture.Destination,
            NestedUnknownJson(PoseFileLimits.MaxJsonDepth));

        var result = AtomicPoseFileStore.Default.ReadMetadata(fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Json, result.Failure?.Kind);
    }

    [Fact]
    public void Structural_preflight_counts_duplicate_raw_collection_properties_before_deserialization()
    {
        var entries = string.Join(",", Enumerable.Repeat(
            "\"same\":{}", PoseFileLimits.MaxEntriesPerCollection + 1));
        var json = $"{{\"Bones\":{{{entries}}},\"MalformedTail\":";
        Assert.True(Encoding.UTF8.GetByteCount(json) < PoseFileLimits.MaxFileBytes);

        var result = AtomicPoseFileStore.Default.Parse(json);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure?.Kind);
        Assert.Equal(
            PoseFileValidationFailureKind.CollectionSize,
            result.Failure?.ValidationFailure?.Kind);
    }

    [Fact]
    public void Exact_32_mib_path_rejects_a_raw_limit_before_reaching_an_invalid_tail()
    {
        using var fixture = new StoreFixture();
        var entries = string.Join(",", Enumerable.Repeat(
            "\"same\":{}", PoseFileLimits.MaxEntriesPerCollection + 1));
        var prefix = Encoding.UTF8.GetBytes($"{{\"Bones\":{{{entries}");
        using (var stream = new FileStream(fixture.Destination, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(prefix);
            stream.SetLength(PoseFileLimits.MaxFileBytes);
        }

        var result = AtomicPoseFileStore.Default.Read(fixture.Destination);

        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure?.Kind);
        Assert.Equal(
            PoseFileValidationFailureKind.CollectionSize,
            result.Failure?.ValidationFailure?.Kind);
    }

    [Fact]
    public void Structural_preflight_counts_raw_total_entries_across_duplicate_keys()
    {
        var full = string.Join(",", Enumerable.Repeat(
            "\"same\":{}", PoseFileLimits.MaxEntriesPerCollection));
        var json = $"{{\"Bones\":{{{full}}},\"MainHand\":{{{full}}}," +
                   $"\"OffHand\":{{{full}}},\"Prop\":{{{full}}}," +
                   "\"Ornament\":{\"same\":{}}}";

        var result = AtomicPoseFileStore.Default.Parse(json);

        Assert.Equal(PoseFileStoreFailureKind.Validation, result.Failure?.Kind);
        Assert.Equal(
            PoseFileValidationFailureKind.TotalEntries,
            result.Failure?.ValidationFailure?.Kind);
    }

    [Fact]
    public void Structural_preflight_rejects_a_raw_bone_key_before_dictionary_materialization()
    {
        var key = new string('x', PoseFileLimits.MaxBoneNameCharacters + 1);

        var result = AtomicPoseFileStore.Default.Parse($"{{\"Bones\":{{\"{key}\":{{}}}}}}");

        Assert.Equal(PoseFileValidationFailureKind.BoneName, result.Failure?.ValidationFailure?.Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void Structural_preflight_counts_every_raw_tag_element(string element)
    {
        var tags = string.Join(",", Enumerable.Repeat(element, PoseFileLimits.MaxTags + 1));

        var result = AtomicPoseFileStore.Default.Parse($"{{\"Tags\":[{tags}]}}");

        Assert.Equal(PoseFileValidationFailureKind.TagCount, result.Failure?.ValidationFailure?.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structural_preflight_rejects_long_raw_tag_strings_in_both_shapes(bool objectShape)
    {
        var tag = new string('x', PoseFileLimits.MaxTagCharacters + 1);
        var value = objectShape ? $"{{\"Name\":\"{tag}\"}}" : $"\"{tag}\"";

        var result = AtomicPoseFileStore.Default.Parse($"{{\"Tags\":[{value}]}}");

        Assert.Equal(PoseFileValidationFailureKind.TagLength, result.Failure?.ValidationFailure?.Kind);
    }

    [Fact]
    public void Typed_outcomes_have_no_public_construction_path_for_impossible_states()
    {
        Assert.Empty(typeof(PoseFileStoreFailure).GetConstructors());
        Assert.Empty(typeof(PoseFileReadOutcome).GetConstructors());
        Assert.Empty(typeof(PoseFileWriteOutcome).GetConstructors());
        Assert.Empty(typeof(PoseFileValidationFailure).GetConstructors());
        Assert.Empty(typeof(PoseFileValidationOutcome).GetConstructors());
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
        yield return new object[] { (int)PoseFileStorePhase.Serialize, PoseFileStoreFailureKind.Serialization };
        yield return new object[] { (int)PoseFileStorePhase.CreateTemporary, PoseFileStoreFailureKind.TemporaryCreate };
        yield return new object[] { (int)PoseFileStorePhase.WriteTemporary, PoseFileStoreFailureKind.TemporaryWrite };
        yield return new object[] { (int)PoseFileStorePhase.FlushTemporary, PoseFileStoreFailureKind.TemporaryFlush };
        yield return new object[] { (int)PoseFileStorePhase.ReopenTemporary, PoseFileStoreFailureKind.TemporaryReopen };
    }

    [Theory]
    [MemberData(nameof(ExistingDestinationFailures))]
    public void Every_precommit_failure_preserves_the_existing_destination(
        int injectedValue,
        PoseFileStoreFailureKind expected)
    {
        var injected = (PoseFileStorePhase)injectedValue;
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 0x13, 0x37, 0x42, 0x7f };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var store = new AtomicPoseFileStore((phase, _) =>
        {
            if (phase == injected)
                throw new IOException($"injected {phase}");
        });

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure?.Kind);
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Empty(result.RecoveryEvidencePaths);
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
    public void Move_failure_preserves_the_validated_temp_as_recovery_evidence()
    {
        using var fixture = new StoreFixture();
        var fileSystem = new EmulatedPoseFileSystem
        {
            MoveBehavior = (_, _) => throw new IOException("injected move"),
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Move, result.Failure?.Kind);
        Assert.False(File.Exists(fixture.Destination));
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.True(File.Exists(result.RecoveryEvidencePaths[0]));
    }

    [Fact]
    public void Undeletable_temp_is_returned_as_recovery_evidence()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var fileSystem = new EmulatedPoseFileSystem
        {
            DeleteBehavior = _ => throw new IOException("injected delete"),
        };
        var store = new AtomicPoseFileStore(fileSystem, (phase, _) =>
        {
            if (phase == PoseFileStorePhase.WriteTemporary)
                throw new IOException("injected write");
        });

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.TemporaryWrite, result.Failure?.Kind);
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.True(File.Exists(result.RecoveryEvidencePaths[0]));
        Assert.Equal(
            Path.GetFullPath(fixture.Root),
            Path.GetDirectoryName(Path.GetFullPath(result.RecoveryEvidencePaths[0])));
        Assert.NotEqual(Path.GetFullPath(fixture.Destination), Path.GetFullPath(result.RecoveryEvidencePaths[0]));
    }

    [Fact]
    public void Successful_write_reopens_and_validates_before_replacing()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var phases = new List<PoseFileStorePhase>();
        var store = new AtomicPoseFileStore((phase, _) => phases.Add(phase));

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.True(result.Succeeded, result.Failure?.Detail);
        Assert.True(phases.IndexOf(PoseFileStorePhase.ReopenTemporary) <
                    phases.IndexOf(PoseFileStorePhase.ReplaceDestination));
        Assert.NotNull(PoseFile.Load(fixture.Destination));
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Reopened_temp_must_decode_and_validate_before_replacing()
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 4, 3, 2, 1 };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var store = new AtomicPoseFileStore((phase, path) =>
        {
            if (phase == PoseFileStorePhase.ReopenTemporary)
                File.WriteAllText(path!, "{ not valid");
        });

        var result = store.Write(
            PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.TemporaryReopen, result.Failure?.Kind);
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Empty(result.RecoveryEvidencePaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void ReplaceFile_1176_layout_preserves_old_destination_and_validated_temp()
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 0x11, 0x76 };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var fileSystem = new EmulatedPoseFileSystem
        {
            ReplaceBehavior = (_, _, _) => throw new IOException("ReplaceFile 1176", 1176),
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Replace, result.Failure?.Kind);
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.True(File.Exists(result.RecoveryEvidencePaths[0]));
        Assert.False(File.Exists(fileSystem.LastBackupPath));
    }

    [Fact]
    public void ReplaceFile_1177_layout_surfaces_both_old_backup_and_validated_temp()
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 0x11, 0x77 };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var fileSystem = new EmulatedPoseFileSystem
        {
            ReplaceBehavior = (source, destination, backup) =>
            {
                File.Move(destination, backup);
                throw new IOException("ReplaceFile 1177", 1177);
            },
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(fixture.Destination));
        Assert.Equal(2, result.RecoveryEvidencePaths.Count);
        Assert.Contains(fileSystem.LastBackupPath!, result.RecoveryEvidencePaths);
        Assert.Equal(oldBytes, File.ReadAllBytes(fileSystem.LastBackupPath!));
        Assert.All(result.RecoveryEvidencePaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Exception_after_replace_commit_is_confirmed_and_backup_is_then_cleaned()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var fileSystem = new EmulatedPoseFileSystem
        {
            ReplaceBehavior = (source, destination, backup) =>
            {
                File.Move(destination, backup);
                File.Move(source, destination);
                throw new IOException("exception after commit");
            },
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.True(result.Succeeded, result.Failure?.Detail);
        Assert.NotNull(PoseFile.Load(fixture.Destination));
        Assert.False(File.Exists(fileSystem.LastBackupPath));
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Destination_loss_between_commit_confirmation_and_backup_cleanup_preserves_backup()
    {
        using var fixture = new StoreFixture();
        var oldBytes = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(fixture.Destination, oldBytes);
        var deleteCalls = 0;
        var fileSystem = new EmulatedPoseFileSystem
        {
            DeleteBehavior = path =>
            {
                deleteCalls++;
                if (deleteCalls == 1)
                    File.Delete(fixture.Destination);
                File.Delete(path);
            },
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.Cleanup, result.Failure?.Kind);
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.Equal(fileSystem.LastBackupPath, result.RecoveryEvidencePaths[0]);
        Assert.Equal(oldBytes, File.ReadAllBytes(result.RecoveryEvidencePaths[0]));
    }

    [Fact]
    public void Destination_deleted_during_replace_keeps_the_sole_validated_new_copy()
    {
        using var fixture = new StoreFixture();
        File.WriteAllText(fixture.Destination, "old");
        var fileSystem = new EmulatedPoseFileSystem
        {
            ReplaceBehavior = (_, destination, _) =>
            {
                File.Delete(destination);
                throw new IOException("destination disappeared");
            },
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(fixture.Destination));
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.NotNull(PoseFile.Load(result.RecoveryEvidencePaths[0]));
    }

    [Fact]
    public void Destination_created_during_move_survives_and_temp_remains_recoverable()
    {
        using var fixture = new StoreFixture();
        var competingBytes = new byte[] { 7, 7, 7 };
        var fileSystem = new EmulatedPoseFileSystem
        {
            MoveBehavior = (_, destination) =>
            {
                File.WriteAllBytes(destination, competingBytes);
                throw new IOException("destination appeared");
            },
        };
        var store = new AtomicPoseFileStore(fileSystem);

        var result = store.Write(PoseFilePersistenceTests.ValidPose(), fixture.Destination);

        Assert.False(result.Succeeded);
        Assert.Equal(competingBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Single(result.RecoveryEvidencePaths);
        Assert.NotNull(PoseFile.Load(result.RecoveryEvidencePaths[0]));
    }
}

internal sealed class EmulatedPoseFileSystem : IPoseFileStoreFileSystem
{
    public Action<string, string, string>? ReplaceBehavior { get; init; }
    public Action<string, string>? MoveBehavior { get; init; }
    public Action<string>? DeleteBehavior { get; init; }
    public string? LastBackupPath { get; private set; }

    public Stream OpenRead(string path) => new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

    public Stream CreateNew(string path) => new FileStream(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);

    public bool Exists(string path) => File.Exists(path);

    public void Replace(string source, string destination, string backup)
    {
        LastBackupPath = backup;
        if (ReplaceBehavior is { } behavior)
            behavior(source, destination, backup);
        else
            File.Replace(source, destination, backup);
    }

    public void Move(string source, string destination)
    {
        if (MoveBehavior is { } behavior)
            behavior(source, destination);
        else
            File.Move(source, destination);
    }

    public void Delete(string path)
    {
        if (DeleteBehavior is { } behavior)
            behavior(path);
        else
            File.Delete(path);
    }
}

internal sealed class BoundedReadPoseFileSystem : IPoseFileStoreFileSystem
{
    private readonly byte[] _bytes;

    public BoundedReadPoseFileSystem(byte[] bytes) => _bytes = bytes;

    public List<int> ReadSizes { get; } = [];
    public int ReadCount => ReadSizes.Count;

    public Stream OpenRead(string path) => new BoundedReadStream(_bytes, ReadSizes);
    public Stream CreateNew(string path) => throw new NotSupportedException();
    public void CreateDirectory(string path) => throw new NotSupportedException();
    public void FlushToDisk(Stream stream) => throw new NotSupportedException();
    public bool Exists(string path) => false;
    public void Replace(string source, string destination, string backup) => throw new NotSupportedException();
    public void Move(string source, string destination) => throw new NotSupportedException();
    public void Delete(string path) => throw new NotSupportedException();

    private sealed class BoundedReadStream : MemoryStream
    {
        private readonly List<int> _readSizes;

        public BoundedReadStream(byte[] bytes, List<int> readSizes)
            : base(bytes, writable: false)
        {
            _readSizes = readSizes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            EnsureBound(count);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            EnsureBound(buffer.Length);
            return base.Read(buffer);
        }

        private void EnsureBound(int count)
        {
            _readSizes.Add(count);
            if (count > AtomicPoseFileStore.MetadataBufferSize)
                throw new InvalidOperationException("The metadata probe requested an unbounded read.");
        }
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
