using System;
using System.IO;
using Poser.Game.Tests.Fixtures;

namespace Poser.Game.Tests.Files;

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
    public void Pruning_orders_by_write_time_not_folder_name_and_floors_at_one()
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

        h.Settings.MaxAutoSaves = 0;
        Assert.Equal(1, h.Service.SaveNow("again"));
        h.WaitForWrite();
        Assert.Contains(h.DayNow(), h.SnapshotFolders());
    }

    [Fact]
    public void Pruning_counts_pose_files_only_and_keeps_recovery_evidence()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = SaveTime;
        h.Settings.MaxAutoSaves = 1;
        var day = h.SeedSnapshot(h.DayNow());
        var stalePose = Path.Combine(day, "00-00-00 Old.pose");
        var temp = Path.Combine(day, ".00-00-00 Old.pose.0123456789abcdef.tmp");
        var backup = Path.Combine(day, ".00-00-00 Old.pose.0123456789abcdef.bak");
        foreach (var file in new[] { stalePose, temp, backup })
        {
            File.WriteAllText(file, "{}");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        }
        h.AddActor("Alpha");

        Assert.Equal(1, h.Service.SaveNow("manual"));
        h.WaitForWrite();

        Assert.False(File.Exists(stalePose));
        Assert.True(File.Exists(temp));
        Assert.True(File.Exists(backup));
    }
}
