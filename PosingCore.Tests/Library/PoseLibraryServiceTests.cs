using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using NSubstitute;
using Poser.Config;
using Poser.Files;
using Poser.Library;
using Poser.Tests.Files;

namespace Poser.Tests.Library;

public sealed class PoseLibraryServiceTests
{
    [Fact]
    public void Metadata_statuses_distinguish_valid_corrupt_future_oversized_and_semantic_invalid()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("valid", PoseFilePersistenceTests.ValidPose());
        fixture.WriteRaw("corrupt", "{ nope");
        var future = PoseFilePersistenceTests.ValidPose();
        future.Version = "future-2";
        fixture.WritePose("future", future);
        fixture.WriteRaw("semantic", "{\"Author\":12,\"Bones\":{},\"MainHand\":{},\"OffHand\":{},\"Prop\":{},\"Ornament\":{}}");
        fixture.WriteOversized("oversized");

        using var service = fixture.CreateService();
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Equal(
            new Dictionary<string, PoseLibraryMetadataStatus>
            {
                ["valid"] = PoseLibraryMetadataStatus.Valid,
                ["corrupt"] = PoseLibraryMetadataStatus.Corrupt,
                ["future"] = PoseLibraryMetadataStatus.Future,
                ["semantic"] = PoseLibraryMetadataStatus.Corrupt,
                ["oversized"] = PoseLibraryMetadataStatus.Oversized,
            },
            service.Snapshot.Entries.ToDictionary(e => e.Name, e => e.MetadataStatus));
        Assert.Contains("future-2", service.Snapshot.Entries.Single(e => e.Name == "future").MetadataDetail);
        Assert.NotEmpty(service.Snapshot.Entries.Single(e => e.Name == "corrupt").MetadataDetail);
    }

    [Fact]
    public void Metadata_and_snapshot_values_are_frozen_at_publication()
    {
        var tags = new List<string> { "one" };
        var entry = new PoseLibraryEntry
        {
            Kind = PoseLibraryEntryKind.Pose,
            FilePath = "x.pose",
            Name = "x",
            NameLower = "x",
            ModifiedText = "",
            Modified = default,
            Folder = 0,
            Tags = tags,
            TagsLower = new[] { "one" },
        };
        var entries = new List<PoseLibraryEntry> { entry };
        var folders = new List<PoseLibraryFolder>
        {
            new()
            {
                Key = "0|",
                Label = "root",
                LabelLower = "root",
                Depth = 0,
                Count = 1,
            }
        };
        var snapshot = new PoseLibrarySnapshot { Revision = 1, Entries = entries, Folders = folders };

        tags.Add("mutated");
        entries.Clear();
        folders.Clear();

        Assert.Equal(new[] { "one" }, entry.Tags);
        Assert.Single(snapshot.Entries);
        Assert.Single(snapshot.Folders);
    }

    [Fact]
    public void Deep_traversal_aborts_without_publishing_a_partial_snapshot()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("before", PoseFilePersistenceTests.ValidPose());
        using var service = fixture.CreateService();
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        var before = service.Snapshot;

        var deep = fixture.Root;
        for (var i = 0; i <= PoseLibraryLimits.MaxDepth; i++)
        {
            deep = Path.Combine(deep, $"d{i}");
            Directory.CreateDirectory(deep);
        }
        File.WriteAllText(Path.Combine(deep, "too-deep.pose"), "{}");

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Same(before, service.Snapshot);
    }

    [Fact]
    public void Missing_configured_root_aborts_without_publishing_a_partial_snapshot()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("before", PoseFilePersistenceTests.ValidPose());
        using var service = fixture.CreateService();
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        var before = service.Snapshot;
        fixture.AddMissingSource();

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Same(before, service.Snapshot);
    }

    [Fact]
    public void Configured_root_preflight_failure_aborts_without_publishing_a_partial_snapshot()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("before", PoseFilePersistenceTests.ValidPose());
        using var service = fixture.CreateService(path => path == fixture.Root);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        var before = service.Snapshot;
        fixture.AddObservationFailureSource();

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Same(before, service.Snapshot);
    }

    [Fact]
    public void Cancellation_dispose_and_stale_generation_never_publish_after_their_pass()
    {
        using var fixture = new LibraryFixture();
        for (var i = 0; i < 128; i++)
            fixture.WritePose($"pose-{i}", PoseFilePersistenceTests.ValidPose());

        using var service = fixture.CreateService();
        service.RequestScan();
        service.RequestScan();
        service.Dispose();
        var revision = service.Snapshot.Revision;

        service.RequestScan();
        Thread.Sleep(100);

        Assert.Equal(revision, service.Snapshot.Revision);
        Assert.False(service.IsScanning);
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(predicate(), "The library scan did not finish.");
    }

    private sealed class LibraryFixture : IDisposable
    {
        private ConfigurationService? _config;

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "poser-library-tests", Guid.NewGuid().ToString("N"));

        public LibraryFixture() => Directory.CreateDirectory(Root);

        public void WritePose(string name, PoseFile pose)
        {
            var path = Path.Combine(Root, name + ".pose");
            var result = AtomicPoseFileStore.Default.Write(pose, path);
            Assert.True(result.Succeeded, result.Failure?.Detail);
        }

        public void WriteRaw(string name, string json) =>
            File.WriteAllText(Path.Combine(Root, name + ".pose"), json, Encoding.UTF8);

        public void WriteOversized(string name)
        {
            using var stream = new FileStream(
                Path.Combine(Root, name + ".pose"), FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);
        }

        public PoseLibraryService CreateService(Func<string, bool>? observeDirectory = null)
        {
            var plugin = Substitute.For<IDalamudPluginInterface>();
            var config = new ConfigurationService(plugin);
            _config = config;
            config.Config.Library.Sources.Clear();
            config.Config.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = "Tests",
                Path = Root,
                Enabled = true,
            });
            return new PoseLibraryService(config, AtomicPoseFileStore.Default, observeDirectory);
        }

        public void AddMissingSource() =>
            _config!.Config.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = "Missing",
                Path = Path.Combine(Root, "missing"),
                Enabled = true,
            });

        public void AddObservationFailureSource()
        {
            var path = Path.Combine(Root, "not-a-directory");
            File.WriteAllText(path, "not a directory");
            _config!.Config.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = "Inaccessible",
                Path = path,
                Enabled = true,
            });
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
