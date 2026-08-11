using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

/// <summary>
/// Disk-based retention. The service reads the folder listing rather than an
/// in-session queue, which is what makes retention survive a plugin restart —
/// the Ktisis defect these tests pin down. Order is by folder DATE, so every
/// test that cares about which folder dies stamps its folders explicitly
/// instead of leaning on the name.
///
/// <para>The prune runs on the write worker, at the very end of it, so every
/// retention assertion here comes after <c>WaitForWrite</c>.</para>
/// </summary>
public class AutoSaveServicePruningTests
{
    private static readonly DateTime SaveTime = new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>"2026-03-04 05-MM-00Z" — always older than <see cref="SaveTime"/>.</summary>
    private static string OldStamp(int minute) =>
        AutoSaveHarness.Stamp(new DateTime(2026, 3, 4, 5, minute, 0, DateTimeKind.Utc));

    /// <summary>
    /// Anchor for the write times these tests hand out. It tracks the REAL
    /// clock, an hour back, because the snapshot the service writes mid-test
    /// gets a real filesystem timestamp and has to come out newest — pinning
    /// the anchor to the harness's fake <see cref="SaveTime"/> instead would
    /// only hold while the machine clock happens to be past it.
    /// </summary>
    private static readonly DateTime WriteBase = DateTime.UtcNow.AddHours(-1);

    /// <summary>
    /// Backdates <paramref name="dir"/> to <c>WriteBase + minutes</c>. Call it
    /// AFTER anything that writes into the folder, since that bumps the
    /// folder's own timestamp.
    /// </summary>
    private static string Age(string dir, int minutes)
    {
        Directory.SetLastWriteTimeUtc(dir, WriteBase.AddMinutes(minutes));
        return dir;
    }

    [Fact]
    public void Prune_keeps_the_newest_MaxAutoSaves_folders_including_folders_from_a_previous_session()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 10;

        // Seeded before the service exists: they stand in for snapshots written
        // by an earlier plugin session. An in-memory queue would never see them.
        // Written oldest-first, and stamped to match, so name order and date
        // order agree here — the divergent case is its own test below.
        for (var minute = 0; minute < 12; minute++)
            Age(h.SeedSnapshot(OldStamp(minute), withFile: true), minute);

        h.AddActor("Alpha");
        var captured = h.Service.SaveNow("test");
        h.WaitForWrite();

        Assert.Equal(1, captured);

        // Retention counts SAVE EVENTS: each seeded legacy folder is one, the
        // new save's time-prefix group is one. Top-level order stays ordinal
        // descending, and the new layout's bare day name sorts after every
        // legacy "<day> <time>Z" sibling.
        var expected = new List<string>();
        for (var minute = 11; minute >= 3; minute--)
            expected.Add(OldStamp(minute));
        expected.Add(h.DayNow());

        Assert.Equal(10, expected.Count);
        Assert.Equal(expected, h.SnapshotFolders());

        // The three oldest are gone, folder contents and all.
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(0))));
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(1))));
        Assert.False(Directory.Exists(Path.Combine(h.Root, OldStamp(2))));
    }

    /// <summary>
    /// The whole point of ordering by date: the user is free to rename a
    /// recovery folder, and retention must still treat it as exactly as old as
    /// it is. A name-sorted prune would keep <c>zzz-…</c> forever and eat the
    /// newer timestamped folders instead.
    /// </summary>
    [Fact]
    public void Prune_orders_by_folder_date_not_by_folder_name()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 3;

        // Sorts LAST by name, oldest by date.
        var renamedOld = Age(h.SeedSnapshot("zzz-renamed-by-user", withFile: true), 0);
        var timestampedOld = Age(h.SeedSnapshot(OldStamp(10), withFile: true), 10);
        var timestampedNew = Age(h.SeedSnapshot(OldStamp(20), withFile: true), 20);
        // Sorts FIRST by name, newest of the seeded folders by date.
        var renamedNew = Age(h.SeedSnapshot("aaa-renamed-by-user", withFile: true), 50);

        h.AddActor("Alpha");
        var captured = h.Service.SaveNow("test");
        h.WaitForWrite();

        Assert.Equal(1, captured);

        // Kept: the save just written, plus the two newest by DATE.
        Assert.True(
            Directory.Exists(Path.Combine(h.Root, h.DayNow())),
            "the save just written always survives");
        Assert.True(
            Directory.Exists(renamedNew),
            "an alphabetically-first name with a NEW write time must survive");
        Assert.True(
            Directory.Exists(timestampedNew),
            "the newest timestamped folder must survive");

        // Pruned: the two oldest by DATE, whatever they are called.
        Assert.False(
            Directory.Exists(renamedOld),
            "an alphabetically-last name with an OLD write time must still be pruned");
        Assert.False(
            Directory.Exists(timestampedOld),
            "the older timestamped folder must be pruned");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Prune_floors_retention_at_one(int configured)
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = configured;

        Age(h.SeedSnapshot(OldStamp(0)), 0);
        Age(h.SeedSnapshot(OldStamp(1)), 1);

        h.AddActor("Alpha");
        h.Service.SaveNow("test");
        h.WaitForWrite();

        // Never zero: the save just written always survives its own prune.
        Assert.Equal(new[] { h.DayNow() }, h.SnapshotFolders());
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
        // stamped NEWER and is therefore attempted BEFORE the deletable one: if
        // a failure aborted the loop, OldStamp(0) would survive.
        var lockedFile = Path.Combine(locked, "held.pose");
        File.WriteAllText(lockedFile, "{}");
        Age(locked, 1);
        Age(deletable, 0);

        using (var _ = new FileStream(
                   lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            h.AddActor("Alpha");
            var captured = h.Service.SaveNow("test");
            h.WaitForWrite();

            Assert.Equal(1, captured);
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
        // Neither half may throw because of it: SaveNow runs on the framework
        // tick, and the worker has nowhere to report to at all.
        h.Service.SaveNow("first");
        h.WaitForWrite();
        Directory.Delete(h.Root, recursive: true);

        var captured = h.Service.SaveNow("second");
        h.WaitForWrite();

        Assert.Equal(1, captured);
        Assert.NotNull(h.Service.LastSaveUtc);

        // The worker rebuilt the root it was handed rather than giving up.
        Assert.Equal(
            new[] { $"{h.PrefixNow()} Alpha.pose" },
            h.SnapshotFiles(h.DayNow()));
    }
}
