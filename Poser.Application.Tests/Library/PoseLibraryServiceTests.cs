using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Poser.Config;
using Poser.Files;
using Poser.Library;
using Poser.Tests.Files;

namespace Poser.Tests.Library;

public sealed class PoseLibraryServiceTests
{
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
        // The scan is a listing: author, tags and status are read when a
        // tile is selected, never at scan time (2026-09-02).
        Assert.Equal(string.Empty, entry.AuthorLower);
        Assert.Empty(entry.TagsLower);
        Assert.Equal(PoseLibraryMetadataStatus.Valid,
            service.Snapshot.Entries.Single(e => e.Name == "broken").MetadataStatus);
        Assert.Equal(PoseLibraryMetadataStatus.Valid,
            service.Snapshot.Entries.Single(e => e.Name == "future").MetadataStatus);
    }

    [Fact]
    public async Task Cancellation_and_disposal_never_publish_a_partial_or_stale_generation()
    {
        using var fixture = new LibraryFixture();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = fixture.CreateService(path =>
        {
            started.SetResult(true);
            release.Task.GetAwaiter().GetResult();
            return true;
        });
        service.RequestScan();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        service.Dispose();
        release.SetResult(true);
        Assert.True(SpinWait.SpinUntil(
            () => !service.IsScanning, TimeSpan.FromSeconds(10)));
        var revision = service.Snapshot.Revision;
        service.RequestScan();

        Assert.Equal(revision, service.Snapshot.Revision);
        Assert.Equal(0, service.Snapshot.Generation);
        Assert.False(service.IsScanning);
    }

    [Fact]
    public void A_failed_source_does_not_hide_healthy_sources()
    {
        using var fixture = new LibraryFixture();
        var healthy = Path.Combine(fixture.Root, "healthy");
        var brokenRoot = Path.Combine(fixture.Root, "broken-root");
        var broken = Path.Combine(brokenRoot, "broken");
        var missing = Path.Combine(fixture.Root, "missing");
        Directory.CreateDirectory(healthy);
        Directory.CreateDirectory(broken);
        fixture.WritePoseAt(healthy, "sent", PoseFilePersistenceTests.ValidPose());
        fixture.WritePoseAt(broken, "hidden", PoseFilePersistenceTests.ValidPose());

        using var service = fixture.CreateService(
            path => path.Equals(broken, StringComparison.Ordinal)
                ? throw new IOException("injected subtree failure")
                : true,
            new LibrarySourceConfig { Name = "Healthy A", Path = healthy },
            new LibrarySourceConfig { Name = "Broken B", Path = brokenRoot },
            new LibrarySourceConfig { Name = "Missing C", Path = missing });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Equal(PoseLibraryScanResult.PartialFailure, service.Snapshot.TerminalResult);
        Assert.Contains(service.Snapshot.Entries, entry => entry.Name == "sent");
        Assert.DoesNotContain(service.Snapshot.Entries, entry => entry.Name == "hidden");
        Assert.Equal(PoseLibrarySourceHealth.Failed,
            service.Snapshot.Sources.Single(item => item.Name == "Broken B").Health);
        Assert.Equal(PoseLibrarySourceHealth.Missing,
            service.Snapshot.Sources.Single(item => item.Name == "Missing C").Health);
    }

    [Fact]
    public void Checked_library_destination_and_atomic_write_create_only_the_requested_output()
    {
        using var fixture = new LibraryFixture();
        var blocker = Path.Combine(fixture.Root, "file");
        File.WriteAllText(blocker, "not a folder");
        var requested = Path.Combine(blocker, "requested");

        var destination = Path.Combine(fixture.Root, "new home", "Poses");
        Assert.True(LibraryConfiguration.TryEnsureDirectory(destination, out var created), created);
        var saved = Path.Combine(destination, "first.pose");
        Assert.True(AtomicPoseFileStore.Default.Write(
            PoseFilePersistenceTests.ValidPose(), saved).Succeeded);
        Assert.True(AtomicPoseFileStore.Default.Read(saved).Succeeded);
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Equal(new[] { blocker, saved }.Order(), before);

        var approved = LibraryConfiguration.TryEnsureDirectory(requested, out var detail);
        Assert.False(approved);
        if (approved)
            AtomicPoseFileStore.Default.Write(
                PoseFilePersistenceTests.ValidPose(), Path.Combine(requested, "refused.pose"));
        Assert.Contains("Could not create library folder", detail);
        Assert.False(Directory.Exists(requested));
        Assert.Equal(before,
            Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        Assert.True(
            SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(10)),
            "The library scan did not finish.");
    }

    private sealed class LibraryFixture : IDisposable
    {
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

        public PoseLibraryService CreateService(
            params LibrarySourceConfig[] sources) => CreateService(null, sources);

        public PoseLibraryService CreateService(
            Func<string, bool>? observeDirectory,
            params LibrarySourceConfig[] sources)
        {
            var config = new ConfigurationService(new Poser.Tests.Fixtures.MemoryConfigurationPersistence());
            config.Config.Library.Sources.Clear();
            if (sources.Length == 0)
            {
                config.Config.Library.Sources.Add(new LibrarySourceConfig
                {
                    Name = "Tests", Path = Root, Enabled = true,
                });
            }
            else
                config.Config.Library.Sources.AddRange(sources);
            return new PoseLibraryService(
                config, new LibraryScanner(observeDirectory));
        }

        public string WritePoseAt(string directory, string name, PoseFile pose)
        {
            var path = Path.Combine(directory, name + ".pose");
            Assert.True(AtomicPoseFileStore.Default.Write(pose, path).Succeeded);
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
