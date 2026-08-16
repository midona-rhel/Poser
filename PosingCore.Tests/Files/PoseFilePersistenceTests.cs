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
        Assert.Equal(PoseFileStoreFailureKind.Json, result.Failure!.Kind);
        Assert.Null(result.Failure.ValidationFailure);
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

    [Fact]
    public void Metadata_reads_headers_and_rejects_bounded_pose_limits()
    {
        using var fixture = new StoreFixture();
        var pose = ValidPose();
        pose.Author = "Ada";
        pose.Version = "1.2";
        pose.Tags = ["sample", "workflow"];
        pose.Base64Image = "thumbnail";
        pose.TerritoryId = 129;
        pose.PlaceName = "Limsa Lominsa";

        Assert.True(AtomicPoseFileStore.Default.Write(pose, fixture.Path).Succeeded);
        var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);

        Assert.True(metadata.Succeeded, metadata.Failure?.Detail);
        Assert.Equal("Ada", metadata.Author);
        Assert.Equal("1.2", metadata.Version);
        Assert.Equal(new[] { "sample", "workflow" }, metadata.Tags);
        Assert.True(metadata.HasThumbnail);
        Assert.Equal("Limsa Lominsa", metadata.PlaceName);

        var invalidDocuments = new[]
        {
            (Json: $"{{\"Tags\":[{string.Join(",", Enumerable.Repeat("\"tag\"", PoseFileLimits.MaxTags + 1))}]}}",
                Kind: PoseFileValidationFailureKind.TagCount),
            (Json: $"{{\"Tags\":[\"{new string('t', PoseFileLimits.MaxTagCharacters + 1)}\"]}}",
                Kind: PoseFileValidationFailureKind.TagLength),
            (Json: $"{{\"Bones\":{{{BoneEntries(PoseFileLimits.MaxEntriesPerCollection + 1)}}}}}",
                Kind: PoseFileValidationFailureKind.CollectionSize),
            (Json: string.Concat(
                    "{\"Bones\":{", BoneEntries(PoseFileLimits.MaxEntriesPerCollection),
                    "},\"MainHand\":{",
                    BoneEntries(PoseFileLimits.MaxEntriesPerCollection,
                        PoseFileLimits.MaxEntriesPerCollection),
                    "},\"OffHand\":{",
                    BoneEntries(PoseFileLimits.MaxEntriesPerCollection,
                        PoseFileLimits.MaxEntriesPerCollection * 2),
                    "},\"Prop\":{",
                    BoneEntries(PoseFileLimits.MaxEntriesPerCollection,
                        PoseFileLimits.MaxEntriesPerCollection * 3),
                    "},\"Ornament\":{\"overflow\":", ValidBoneJson, "}}"),
                Kind: PoseFileValidationFailureKind.TotalEntries),
            (Json: $"{{\"Bones\":{{\"{new string('b', PoseFileLimits.MaxBoneNameCharacters + 1)}\":{ValidBoneJson}}}}}",
                Kind: PoseFileValidationFailureKind.BoneName),
        };

        foreach (var invalid in invalidDocuments)
        {
            File.WriteAllText(fixture.Path, invalid.Json);
            var rejected = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);

            Assert.False(rejected.Succeeded, invalid.Kind.ToString());
            Assert.Equal(invalid.Kind, rejected.Failure!.ValidationFailure!.Kind);
        }

        using (var stream = new FileStream(fixture.Path, FileMode.Create, FileAccess.Write))
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);

        var oversized = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);
        Assert.False(oversized.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.SizeLimit, oversized.Failure!.Kind);
    }

    [Fact]
    public void Metadata_numeric_wire_matches_full_parse_for_long_and_culture_split_values()
    {
        using var fixture = new StoreFixture();
        var positions = new[]
        {
            $"{new string('9', 400)}, 0, 0",
            "1,25, 2,5, 3,75",
        };

        foreach (var position in positions)
        {
            var json = PoseWithPosition(position);
            File.WriteAllText(fixture.Path, json);

            var metadata = AtomicPoseFileStore.Default.ReadMetadata(fixture.Path);
            var parsed = AtomicPoseFileStore.Default.Parse(json);

            Assert.False(metadata.Succeeded, position);
            Assert.False(parsed.Succeeded, position);
            Assert.Equal(parsed.Failure!.Kind, metadata.Failure!.Kind);
            Assert.Equal(parsed.Failure.ValidationFailure?.Kind,
                metadata.Failure.ValidationFailure?.Kind);
        }
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

    [Fact]
    public void Temp_revalidation_fails_before_commit_and_leaves_destination_untouched()
    {
        using var fixture = new StoreFixture();
        var old = new byte[] { 0x13, 0x37, 0x42 };
        File.WriteAllBytes(fixture.Path, old);
        var store = new AtomicPoseFileStore((phase, path) =>
        {
            if (phase == PoseFileStorePhase.ReopenTemporary)
                File.WriteAllText(path!, "not a pose document");
        });

        var result = store.Write(ValidPose(), fixture.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(PoseFileStoreFailureKind.TemporaryReopen, result.Failure!.Kind);
        Assert.Equal(old, File.ReadAllBytes(fixture.Path));
        Assert.Empty(result.RecoveryEvidencePaths);
    }

    [Fact]
    public void Atomic_commit_reports_backup_cleanup_and_destination_loss()
    {
        using (var fixture = new StoreFixture())
        {
            File.WriteAllText(fixture.Path, "old destination");
            var store = new AtomicPoseFileStore((phase, _) =>
            {
                if (phase == PoseFileStorePhase.CleanupBackup)
                    throw new IOException("injected backup cleanup failure");
            });

            var result = store.Write(ValidPose(), fixture.Path);

            Assert.False(result.Succeeded);
            Assert.Equal(PoseFileStoreFailureKind.Cleanup, result.Failure!.Kind);
            Assert.True(AtomicPoseFileStore.Default.Read(fixture.Path).Succeeded);
            var backup = Assert.Single(result.RecoveryEvidencePaths);
            Assert.EndsWith(".bak", backup, StringComparison.Ordinal);
            Assert.True(File.Exists(backup));
        }

        var destinationLossCases = new[]
        {
            (Existing: true, Kind: PoseFileStoreFailureKind.Replace, Evidence: true),
            (Existing: false, Kind: PoseFileStoreFailureKind.Move, Evidence: false),
        };

        foreach (var testCase in destinationLossCases)
        {
            using var fixture = new StoreFixture();
            if (testCase.Existing)
                File.WriteAllText(fixture.Path, "old destination");

            var fileSystem = new DestinationLossFileSystem();
            var store = new AtomicPoseFileStore(fileSystem);
            var result = store.Write(ValidPose(), fixture.Path);

            Assert.False(result.Succeeded);
            Assert.Equal(testCase.Kind, result.Failure!.Kind);
            Assert.False(File.Exists(fixture.Path));
            Assert.Equal(testCase.Evidence, result.RecoveryEvidencePaths.Count != 0);
        }
    }

    private const string ValidBoneJson =
        "{\"Position\":\"0, 0, 0\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}";

    private static string BoneEntries(int count, int start = 0) =>
        string.Join(",", Enumerable.Range(start, count)
            .Select(index => $"\"b{index}\":{ValidBoneJson}"));

    private static string PoseWithPosition(string position) =>
        string.Concat(
            "{\"Bones\":{\"j_kao\":{\"Position\":\"", position,
            "\",\"Rotation\":\"0, 0, 0, 1\",\"Scale\":\"1, 1, 1\"}}}}");

    private sealed class DestinationLossFileSystem : IPoseFileStoreFileSystem
    {
        private readonly SystemPoseFileStoreFileSystem _inner = new();

        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);

        public void Replace(string source, string destination, string backup)
        {
            _inner.Replace(source, destination, backup);
            _inner.Delete(destination);
        }

        public void Move(string source, string destination)
        {
            _inner.Move(source, destination);
            _inner.Delete(destination);
        }

        public void Delete(string path) => _inner.Delete(path);
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
