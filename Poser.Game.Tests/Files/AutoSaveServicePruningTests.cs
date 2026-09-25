using System;
using System.IO;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

public sealed class AutoSaveServicePruningTests
{
    private static readonly DateTime SaveTime =
        new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    private static string Stamp(int minute) => AutoSaveHarness.Stamp(
        new DateTime(2026, 3, 4, 5, minute, 0, DateTimeKind.Utc));

    private static string Age(string path, int minute)
    {
        Directory.SetLastWriteTimeUtc(path,
            DateTime.UtcNow.AddHours(-1).AddMinutes(minute));
        return path;
    }

    [Fact]
    public void Pruning_keeps_the_newest_disk_events_across_sessions_and_floors_at_one()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 3;
        Age(h.SeedSnapshot(Stamp(0), withFile: true), 0);
        Age(h.SeedSnapshot(Stamp(1), withFile: true), 1);
        Age(h.SeedSnapshot(Stamp(2), withFile: true), 2);
        h.AddActor("Alpha");

        Assert.Equal(1, h.Service.SaveNow("manual"));
        h.WaitForWrite();

        Assert.Equal(3, h.SnapshotFolders().Count);
        Assert.False(Directory.Exists(Path.Combine(h.Root, Stamp(0))));

        h.Settings.MaxAutoSaves = 0;
        Assert.Equal(1, h.Service.SaveNow("again"));
        h.WaitForWrite();
        Assert.Contains(h.DayNow(), h.SnapshotFolders());
    }

    [Fact]
    public void Pruning_orders_by_write_time_not_renamed_folder_name_and_continues_after_failure()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 3;
        var oldNamedLast = Age(h.SeedSnapshot("zzz-renamed", withFile: true), 0);
        var old = Age(h.SeedSnapshot(Stamp(10), withFile: true), 10);
        var newNamedFirst = Age(h.SeedSnapshot("aaa-renamed", withFile: true), 50);
        Age(h.SeedSnapshot(Stamp(20), withFile: true), 20);
        h.AddActor("Alpha");

        Assert.Equal(1, h.Service.SaveNow("manual"));
        h.WaitForWrite();

        Assert.True(Directory.Exists(newNamedFirst));
        Assert.False(Directory.Exists(oldNamedLast));
        Assert.False(Directory.Exists(old));
    }
}
