using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

/// <summary>
/// Disk-based retention. The service reads the folder listing rather than an
/// in-session queue, which is what makes retention survive a plugin restart —
/// the Ktisis defect these tests pin down.
/// </summary>
public class AutoSaveServicePruningTests
{
    private static readonly DateTime SaveTime = new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>"2026-03-04 05-MM-00Z" — always older than <see cref="SaveTime"/>.</summary>
    private static string OldStamp(int minute) =>
        AutoSaveHarness.Stamp(new DateTime(2026, 3, 4, 5, minute, 0, DateTimeKind.Utc));

    [Fact]
    public void Prune_keeps_the_newest_MaxAutoSaves_folders_including_folders_from_a_previous_session()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 10;

        // Seeded before the service exists: they stand in for snapshots written
        // by an earlier plugin session. An in-memory queue would never see them.
        for (var minute = 0; minute < 12; minute++)
            h.SeedSnapshot(OldStamp(minute), withFile: true);

        h.AddActor("Alpha");
        var saved = h.Service.SaveNow("test");

        Assert.Equal(1, saved);

        var expected = new List<string> { AutoSaveHarness.Stamp(SaveTime) };
        for (var minute = 11; minute >= 3; minute--)
            expected.Add(OldStamp(minute));

        Assert.Equal(10, expected.Count);
        Assert.Equal(expected, h.SnapshotFolders());

        // The three oldest are gone, folder contents and all.
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(0))));
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(1))));
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(2))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Prune_floors_retention_at_one(int configured)
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = configured;

        h.SeedSnapshot(OldStamp(0));
        h.SeedSnapshot(OldStamp(1));

        h.AddActor("Alpha");
        h.Service.SaveNow("test");

        // Never zero: the snapshot just written always survives its own prune.
        Assert.Equal(new[] { AutoSaveHarness.Stamp(SaveTime) }, h.SnapshotFolders());
    }

    [Fact]
    public void Prune_continues_past_an_undeletable_folder_and_logs_the_failure()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 1;

        var locked = h.SeedSnapshot(OldStamp(1));
        var deletable = h.SeedSnapshot(OldStamp(0));

        // Prune walks the stale list newest-first, so the locked folder is
        // attempted BEFORE the deletable one: if a failure aborted the loop,
        // OldStamp(0) would survive.
        var lockedFile = Path.Combine(locked, "held.pose");
        File.WriteAllText(lockedFile, "{}");

        using (var _ = new FileStream(
                   lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            h.AddActor("Alpha");
            var saved = h.Service.SaveNow("test");

            Assert.Equal(1, saved);
            Assert.True(Directory.Exists(locked), "the locked folder cannot be deleted");
            Assert.False(Directory.Exists(deletable), "pruning must not stop at the failure");
            Assert.True(h.ErrorCount >= 1, "the failed prune must be logged as an error");
        }
    }

    [Fact]
    public void Prune_survives_a_missing_root_directory()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.AddActor("Alpha");

        // The root can vanish under the service (user cleanup, sync client).
        // SaveNow must never throw out onto the framework tick because of it.
        h.Service.SaveNow("first");
        Directory.Delete(h.Root, recursive: true);

        var saved = h.Service.SaveNow("second");

        Assert.Equal(1, saved);
        Assert.NotNull(h.Service.LastSaveUtc);
    }
}
