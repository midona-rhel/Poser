using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        Assert.Equal(4, config.Sources.Count);
        config.EnsureDefaults();
        Assert.Equal(4, config.Sources.Count);
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
        Assert.Equal(PoseLibraryMetadataStatus.Valid,
            service.Snapshot.Entries.Single(e => e.Name == "broken").MetadataStatus);
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
    public void Missing_source_does_not_hide_a_healthy_source_and_publishes_partial_health()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("sent", PoseFilePersistenceTests.ValidPose());
        var missing = Path.Combine(fixture.Root, "missing");
        using var service = fixture.CreateService(
            new LibrarySourceConfig { Name = "Healthy A", Path = fixture.Root },
            new LibrarySourceConfig { Name = "Missing B", Path = missing });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Equal(PoseLibraryScanResult.PartialFailure,
            service.Snapshot.TerminalResult);
        Assert.Contains(service.Snapshot.Entries, entry => entry.Name == "sent");
        var source = service.Snapshot.Sources.Single(item => item.Name == "Missing B");
        Assert.Equal(PoseLibrarySourceHealth.Missing, source.Health);
        Assert.Contains("does not exist", source.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, service.Snapshot.Sources.Count);
    }

    [Fact]
    public void Injected_denied_source_is_reported_independently_of_a_healthy_source()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("sent", PoseFilePersistenceTests.ValidPose());
        var denied = Path.Combine(fixture.Root, "denied");
        using var service = fixture.CreateService(
            path => path.Equals(denied, StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("injected denial")
                : true,
            new LibrarySourceConfig { Name = "Healthy A", Path = fixture.Root },
            new LibrarySourceConfig { Name = "Denied C", Path = denied });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Contains(service.Snapshot.Entries, entry => entry.Name == "sent");
        var source = service.Snapshot.Sources.Single(item => item.Name == "Denied C");
        Assert.Equal(PoseLibrarySourceHealth.Denied, source.Health);
        Assert.Contains("denied", source.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PoseLibraryScanResult.PartialFailure,
            service.Snapshot.TerminalResult);
    }

    [Fact]
    public void Failed_subtree_is_omitted_without_discarding_other_sources()
    {
        using var fixture = new LibraryFixture();
        var healthy = Path.Combine(fixture.Root, "healthy");
        var brokenRoot = Path.Combine(fixture.Root, "broken-root");
        var broken = Path.Combine(brokenRoot, "broken");
        Directory.CreateDirectory(healthy);
        Directory.CreateDirectory(broken);
        fixture.WritePoseAt(healthy, "sent", PoseFilePersistenceTests.ValidPose());
        fixture.WritePoseAt(broken, "hidden", PoseFilePersistenceTests.ValidPose());

        using var service = fixture.CreateService(
            path => path.Equals(broken, StringComparison.Ordinal)
                ? throw new IOException("injected subtree failure")
                : true,
            new LibrarySourceConfig { Name = "Healthy A", Path = healthy },
            new LibrarySourceConfig { Name = "Broken B", Path = brokenRoot });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Contains(service.Snapshot.Entries, entry => entry.Name == "sent");
        Assert.DoesNotContain(service.Snapshot.Entries, entry => entry.Name == "hidden");
        Assert.Equal(PoseLibrarySourceHealth.Failed,
            service.Snapshot.Sources.Single(item => item.Name == "Broken B").Health);
    }

    [Fact]
    public void Scan_result_distinguishes_all_failed_healthy_empty_and_disabled_sources()
    {
        using var fixture = new LibraryFixture();
        var missing = Path.Combine(fixture.Root, "missing");
        using var allFailed = fixture.CreateService(
            new LibrarySourceConfig { Name = "Missing", Path = missing });
        allFailed.RequestScan();
        WaitUntil(() => !allFailed.IsScanning);
        Assert.Equal(PoseLibraryScanResult.Failure, allFailed.Snapshot.TerminalResult);
        Assert.Empty(allFailed.Snapshot.Entries);

        using var empty = fixture.CreateService(
            new LibrarySourceConfig { Name = "Empty", Path = fixture.Root });
        empty.RequestScan();
        WaitUntil(() => !empty.IsScanning);
        Assert.Equal(PoseLibraryScanResult.Success, empty.Snapshot.TerminalResult);
        Assert.Equal(PoseLibrarySourceHealth.Ready, empty.Snapshot.Sources[0].Health);
        Assert.Single(empty.Snapshot.Folders);

        using var disabled = fixture.CreateService(
            new LibrarySourceConfig
            {
                Name = "Disabled", Path = missing, Enabled = false
            });
        disabled.RequestScan();
        WaitUntil(() => !disabled.IsScanning);
        Assert.Equal(PoseLibraryScanResult.Success, disabled.Snapshot.TerminalResult);
        Assert.Equal(PoseLibrarySourceHealth.Disabled, disabled.Snapshot.Sources[0].Health);
    }

    [Fact]
    public async Task Queued_refresh_is_deterministic_and_publishes_only_the_latest_generation()
    {
        using var fixture = new LibraryFixture();
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = fixture.CreateService(
            path =>
            {
                if (path == fixture.Root && Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult(true);
                    release.Task.GetAwaiter().GetResult();
                }
                return true;
            });

        service.RequestScan();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.WritePose("after-refresh", PoseFilePersistenceTests.ValidPose());
        service.RequestScan();
        release.SetResult(true);

        WaitUntil(() => !service.IsScanning);
        Assert.Contains(service.Snapshot.Entries,
            entry => entry.Name == "after-refresh");
        Assert.Equal(2, service.Snapshot.Generation);
    }

    [Fact]
    public void External_copy_appears_after_refresh_even_when_another_source_failed()
    {
        using var fixture = new LibraryFixture();
        var missing = Path.Combine(fixture.Root, "missing");
        using var service = fixture.CreateService(
            new LibrarySourceConfig { Name = "Healthy", Path = fixture.Root },
            new LibrarySourceConfig { Name = "Missing", Path = missing });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.DoesNotContain(service.Snapshot.Entries,
            entry => entry.Name == "external");

        fixture.WritePose("external", PoseFilePersistenceTests.ValidPose());
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Contains(service.Snapshot.Entries,
            entry => entry.Name == "external");
        Assert.Equal(PoseLibraryScanResult.PartialFailure,
            service.Snapshot.TerminalResult);
    }

    [Fact]
    public void Source_health_snapshot_is_immutable_and_keeps_identity_order()
    {
        using var fixture = new LibraryFixture();
        using var service = fixture.CreateService(
            new LibrarySourceConfig { Name = "First", Path = fixture.Root },
            new LibrarySourceConfig { Name = "Second", Path = "" });
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        var snapshot = service.Snapshot;
        Assert.Equal(new[] { "First", "Second" },
            snapshot.Sources.Select(source => source.Name));
        Assert.Equal(PoseLibrarySourceHealth.Ready, snapshot.Sources[0].Health);
        Assert.Equal(PoseLibrarySourceHealth.Invalid, snapshot.Sources[1].Health);
        Assert.True(((IList<PoseLibrarySourceSnapshot>)snapshot.Sources).IsReadOnly);
        Assert.True(((IList<PoseLibraryEntry>)snapshot.Entries).IsReadOnly);
        Assert.True(((IList<PoseLibraryFolder>)snapshot.Folders).IsReadOnly);
    }

    [Fact]
    public void Overlapping_sources_keep_distinct_ordered_folder_and_entry_indexes()
    {
        using var fixture = new LibraryFixture();
        fixture.WritePose("shared", PoseFilePersistenceTests.ValidPose());
        using var service = fixture.CreateService(
            new LibrarySourceConfig { Name = "First", Path = fixture.Root },
            new LibrarySourceConfig { Name = "Second", Path = fixture.Root });

        service.RequestScan();
        WaitUntil(() => !service.IsScanning);

        Assert.Equal(2, service.Snapshot.Folders.Count);
        Assert.Equal(new[] { "0|", "1|" },
            service.Snapshot.Folders.Select(folder => folder.Key));
        Assert.Equal(new[] { 0, 1 },
            service.Snapshot.Entries.Select(entry => entry.Folder));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_defaults_create_only_managed_homes_and_refresh_after_first_save(bool blockedHome)
    {
        using var fixture = new LibraryFixture();
        var config = new LibraryConfiguration();
        config.EnsureDefaults(fixture.Root);
        Assert.Equal(6, config.Sources.Count);
        Assert.All(config.Sources, source => Assert.StartsWith(fixture.Root, source.Path));
        var sceneRoot = config.Sources.Single(s => s.Name == LibraryConfiguration.SceneSourceName).Path;
        if (blockedHome)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sceneRoot)!);
            File.WriteAllText(sceneRoot, "blocked home");
        }
        config.EnsureHomeRootsExist();
        using var service = fixture.CreateConfiguredService(config);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(PoseLibraryScanResult.PartialFailure, service.Snapshot.TerminalResult);
        Assert.Empty(service.Snapshot.Entries);
        Assert.All(service.Snapshot.Sources.Take(4), source =>
            Assert.Equal(blockedHome && source.Path == sceneRoot
                ? PoseLibrarySourceHealth.Failed : PoseLibrarySourceHealth.Ready, source.Health));
        Assert.All(service.Snapshot.Sources.Skip(4), source =>
        {
            Assert.Equal(PoseLibrarySourceHealth.Missing, source.Health);
            Assert.False(Directory.Exists(source.Path));
        });

        var poseRoot = config.ResolvePoseRoot();
        Assert.True(LibraryConfiguration.TryEnsureDirectory(poseRoot, out _));
        var saved = fixture.WritePoseAt(poseRoot, "first-save", PoseFilePersistenceTests.ValidPose());
        var copied = Path.Combine(poseRoot, "external-copy.pose");
        File.Copy(saved, copied);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(new[] { copied, saved }.Order(),
            service.Snapshot.Entries.Select(e => e.FilePath).Order());

        var objects = config.Sources.Single(s => s.Name == LibraryConfiguration.ObjectsSourceName);
        objects.Path = Path.Combine(fixture.Root, "disabled custom objects");
        objects.Enabled = false;
        var external = config.Sources.Last();
        external.Path = Path.Combine(fixture.Root, "custom external");
        external.Enabled = false;
        config.EnsureDefaults(fixture.Root);
        config.EnsureHomeRootsExist();
        Assert.Equal(6, config.Sources.Count);
        Assert.False(objects.Enabled);
        Assert.False(external.Enabled);
        Assert.False(Directory.Exists(objects.Path));
        Assert.False(Directory.Exists(external.Path));
        fixture.SaveConfiguration();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(PoseLibrarySourceHealth.Disabled,
            service.Snapshot.Sources.Single(s => s.Name == objects.Name).Health);
        Assert.Equal(Path.Combine(fixture.Root, "Poser"), config.ResolveRoot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Denied_during_subtree_enumeration_keeps_typed_health_and_discards_partial_source(bool folders)
    {
        using var fixture = new LibraryFixture();
        var healthy = fixture.MakeSource("healthy", 1);
        var failing = fixture.MakeSource("failing", 1);
        var subtree = Path.Combine(failing.Path, "denied subtree");
        Directory.CreateDirectory(subtree);
        var partial = fixture.WritePoseAt(subtree, "partial", PoseFilePersistenceTests.ValidPose());
        var config = new LibraryConfiguration { Sources = [failing, healthy] };
        IEnumerable<string> Denied(string path)
        {
            if (path == subtree)
            {
                yield return folders ? Path.Combine(subtree, "listed-child") : partial;
                throw new UnauthorizedAccessException("injected enumeration denial");
            }
            foreach (var item in folders
                         ? Directory.EnumerateDirectories(path) : Directory.EnumerateFiles(path))
                yield return item;
        }
        using var service = fixture.CreateConfiguredService(config,
            enumerateFiles: folders ? null : Denied,
            enumerateDirectories: folders ? Denied : null);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        var failure = service.Snapshot.Sources[0];
        Assert.Equal(PoseLibrarySourceHealth.Denied, failure.Health);
        Assert.Contains(subtree, failure.Detail);
        Assert.Single(service.Snapshot.Entries);
        Assert.StartsWith(healthy.Path, service.Snapshot.Entries[0].FilePath);
        Assert.Equal(PoseLibraryScanResult.PartialFailure, service.Snapshot.TerminalResult);
    }

    [Fact]
    public void Aggregate_capacity_rejects_whole_sources_and_leaves_room_for_later_small_roots()
    {
        using var fixture = new LibraryFixture();
        var first = fixture.MakeSource("first", 1);
        var aggregate = fixture.MakeSource("aggregate-overflow", 2);
        var huge = fixture.MakeSource("oversized", 3);
        var last = fixture.MakeSource("last", 1);
        using var service = fixture.CreateConfiguredService(
            new LibraryConfiguration { Sources = [first, aggregate, huge, last] },
            maxFiles: 2, maxFolders: 2);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(2, service.Snapshot.Entries.Count);
        Assert.Equal(2, service.Snapshot.Folders.Count);
        Assert.Equal(new[] { "0|", "3|" }, service.Snapshot.Folders.Select(f => f.Key));
        Assert.Equal(new[] { 0, 1 }, service.Snapshot.Entries.Select(e => e.Folder));
        Assert.Equal(PoseLibrarySourceHealth.Failed, service.Snapshot.Sources[1].Health);
        Assert.Equal(PoseLibrarySourceHealth.Failed, service.Snapshot.Sources[2].Health);
        Assert.Equal(PoseLibrarySourceHealth.Ready, service.Snapshot.Sources[3].Health);
    }

    [Fact]
    public void Excessive_sources_are_bounded_and_explicitly_reported()
    {
        using var fixture = new LibraryFixture();
        var config = new LibraryConfiguration
        {
            Sources = [fixture.MakeSource("first", 0), fixture.MakeSource("second", 0),
                fixture.MakeSource("third", 0)]
        };
        using var service = fixture.CreateConfiguredService(config, maxSources: 2);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(2, service.Snapshot.Sources.Count);
        Assert.Equal(1, service.Snapshot.SkippedSourceCount);
        Assert.Equal(PoseLibraryScanResult.PartialFailure, service.Snapshot.TerminalResult);
    }

    [Fact]
    public void Aggregate_folder_capacity_rejection_does_not_reserve_space_for_a_failed_source()
    {
        using var fixture = new LibraryFixture();
        var first = fixture.MakeSource("first", 0);
        var overflow = fixture.MakeSource("overflow", 0);
        var child = Path.Combine(overflow.Path, "child");
        Directory.CreateDirectory(child);
        fixture.WritePoseAt(child, "nested", PoseFilePersistenceTests.ValidPose());
        var last = fixture.MakeSource("last", 0);
        using var service = fixture.CreateConfiguredService(
            new LibraryConfiguration { Sources = [first, overflow, last] },
            maxFiles: 10, maxFolders: 2);
        service.RequestScan();
        WaitUntil(() => !service.IsScanning);
        Assert.Equal(new[] { "0|", "2|" }, service.Snapshot.Folders.Select(f => f.Key));
        Assert.Equal(PoseLibrarySourceHealth.Failed, service.Snapshot.Sources[1].Health);
        Assert.Contains("remaining library capacity", service.Snapshot.Sources[1].Detail);
        Assert.Empty(service.Snapshot.Entries);
    }

    [Fact]
    public void Configured_managed_root_with_spaces_is_independent_of_external_and_disabled_sources()
    {
        using var fixture = new LibraryFixture();
        var managed = Path.Combine(fixture.Root, "Custom Poser Root");
        var config = new LibraryConfiguration();
        config.SetHomeRoot(LibraryConfiguration.PoseSourceName,
            Path.Combine(fixture.Root, "unused"), Path.Combine(managed, LibraryConfiguration.PosesLeaf));
        config.Sources[0].Enabled = false;
        config.Sources.Add(new LibrarySourceConfig
        {
            Name = "External", Path = Path.Combine(fixture.Root, "external")
        });
        Assert.Equal(managed, config.ResolveRoot());
        Assert.False(Directory.Exists(managed));
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        Assert.True(
            SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(10)),
            "The library scan did not finish.");
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

        public PoseLibraryService CreateService(
            params LibrarySourceConfig[] sources) => CreateService(null, sources);

        public PoseLibraryService CreateService(
            Func<string, bool>? observeDirectory,
            params LibrarySourceConfig[] sources)
        {
            var config = new ConfigurationService(Substitute.For<IDalamudPluginInterface>());
            _config = config;
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
                config, AtomicPoseFileStore.Default, observeDirectory);
        }

        public string WritePoseAt(string directory, string name, PoseFile pose)
        {
            var path = Path.Combine(directory, name + ".pose");
            Assert.True(AtomicPoseFileStore.Default.Write(pose, path).Succeeded);
            return path;
        }

        public void SaveConfiguration() => _config!.Save();

        public LibrarySourceConfig MakeSource(string name, int files)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            for (var i = 0; i < files; i++)
                WritePoseAt(path, $"pose-{i}", PoseFilePersistenceTests.ValidPose());
            return new LibrarySourceConfig { Name = name, Path = path };
        }

        public PoseLibraryService CreateConfiguredService(
            LibraryConfiguration library,
            Func<string, IEnumerable<string>>? enumerateFiles = null,
            Func<string, IEnumerable<string>>? enumerateDirectories = null,
            int maxFiles = PoseLibraryLimits.MaxFiles,
            int maxFolders = PoseLibraryLimits.MaxFolders,
            int maxSources = PoseLibraryLimits.MaxSources)
        {
            _config = new ConfigurationService(Substitute.For<IDalamudPluginInterface>());
            _config.Config.Library = library;
            return new PoseLibraryService(_config, AtomicPoseFileStore.Default,
                enumerateFiles: enumerateFiles, enumerateDirectories: enumerateDirectories,
                maxFiles: maxFiles, maxFolders: maxFolders, maxSources: maxSources);
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
