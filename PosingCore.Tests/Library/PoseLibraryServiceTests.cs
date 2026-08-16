using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    public void Library_home_defaults_are_seeded_once_and_repointed_roots_stay_scanned()
    {
        var config = new LibraryConfiguration
        {
            DefaultsSeeded = true,
            SceneRootSeeded = true,
        };
        config.EnsureDefaults();
        config.SetHomeRoot(LibraryConfiguration.PoseSourceName,
            LibraryConfiguration.DefaultPoseRoot, @"D:\Poses");
        config.SetHomeRoot(LibraryConfiguration.SceneSourceName,
            LibraryConfiguration.DefaultSceneRoot, "   ");

        Assert.Equal(@"D:\Poses", config.ResolvePoseRoot());
        Assert.Equal(LibraryConfiguration.DefaultSceneRoot, config.ResolveSceneRoot());
        Assert.Equal(3, config.Sources.Count);
        config.EnsureDefaults();
        Assert.Equal(3, config.Sources.Count);
    }

    [Fact]
    public void Scan_publishes_ordered_names_normalized_search_fields_and_typed_statuses()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("zeta", PoseFilePersistenceTests.ValidPose());
        var authored = PoseFilePersistenceTests.ValidPose();
        authored.Author = "MiDoNa";
        authored.Tags = ["TagOne"];
        fixture.WritePose("Alpha", authored);
        fixture.WriteRaw("broken", "{ nope");
        var future = PoseFilePersistenceTests.ValidPose();
        future.Version = "future-2";
        fixture.WritePose("future", future);

        using var service = fixture.CreateService();
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Equal(new[] { "Alpha", "broken", "future", "zeta" },
            service.Snapshot.Entries.Select(entry => entry.Name));
        var entry = service.Snapshot.Entries[0];
        Assert.Equal("midona", entry.AuthorLower);
        Assert.Equal(new[] { "tagone" }, entry.TagsLower);
        Assert.Equal(PoseLibraryMetadataStatus.Corrupt,
            service.Snapshot.Entries.Single(e => e.Name == "broken").MetadataStatus);
        Assert.Equal(PoseLibraryMetadataStatus.Future,
            service.Snapshot.Entries.Single(e => e.Name == "future").MetadataStatus);
    }

    [Fact]
    public void Quarantine_and_restore_change_the_next_published_snapshot_without_indexing_evidence()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.WriteRaw("broken", "{ nope");
        using var service = fixture.CreateService();
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        var quarantined = PoseLibraryFileActions.Default.Quarantine(path);
        Assert.True(quarantined.Succeeded, quarantined.Detail);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.DoesNotContain(service.Snapshot.Entries, e => e.Name == "broken");
        Assert.DoesNotContain(service.Snapshot.Folders,
            folder => folder.Label.Contains(PoseLibraryFileActions.QuarantineFolderName,
                StringComparison.OrdinalIgnoreCase));

        var restored = PoseLibraryFileActions.Default.Restore(quarantined.ResultPath!);
        Assert.True(restored.Succeeded, restored.Detail);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(PoseLibraryMetadataStatus.Corrupt,
            service.Snapshot.Entries.Single(e => e.Name == "broken").MetadataStatus);
    }

    [Fact]
    public void Cancellation_and_disposal_never_publish_a_partial_or_stale_generation()
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

        public PoseLibraryService CreateService()
        {
            var config = new ConfigurationService(Substitute.For<IDalamudPluginInterface>());
            _config = config;
            config.Config.Library.Sources.Clear();
            config.Config.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = "Tests", Path = Root, Enabled = true,
            });
            return new PoseLibraryService(config, AtomicPoseFileStore.Default);
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
