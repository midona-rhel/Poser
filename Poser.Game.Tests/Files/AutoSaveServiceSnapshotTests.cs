using System;
using System.IO;
using System.Linq;
using Poser.Files;
using Poser.Tests.Fixtures;
using Poser.Documents.AutoSave;
using Poser.Documents.Files;

namespace Poser.Tests.Files;

public sealed class AutoSaveServiceSnapshotTests
{
    [Fact]
    public void Snapshot_capture_is_complete_before_dispatch_and_round_trips_real_pose_bytes()
    {
        using var h = new AutoSaveHarness();
        Action? queued = null;
        h.Dispatch = work =>
        {
            queued = work;
            return true;
        };
        h.AddActor("Alpha");

        Assert.Equal(1, h.Service.SaveNow("manual"));
        Assert.Empty(Directory.GetDirectories(h.Root));
        Assert.NotNull(queued);

        queued!();
        h.WaitForWrite();

        var path = Path.Combine(h.Root, h.DayNow(),
            $"{h.PrefixNow()} Alpha.pose");
        var read = AtomicPoseFileStore.Default.Read(path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Contains("j_kosi", read.Pose!.Bones.Keys);
    }

    [Fact]
    public void Snapshot_write_failures_keep_ordered_health_history_and_later_files()
    {
        using var h = new AutoSaveHarness();
        var first = h.AddActor("First");
        var middle = h.AddActor("Middle");
        h.AddActor("Last");
        h.FailWriteFor(first);
        h.FailWriteFor(middle);

        Assert.Equal(3, h.Service.SaveNow("manual"));
        h.WaitForWrite();

        var health = new AutoSaveHealthStore(h.Root).Read()!;
        Assert.Equal(3, health.IntendedActors);
        Assert.Equal(1, health.WrittenActors);
        Assert.Equal(
            new[]
            {
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} First.pose"),
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Middle.pose"),
                Path.Combine(h.Root, h.DayNow(), $"{h.PrefixNow()} Last.pose"),
            },
            health.AffectedPaths);
        Assert.Equal(new[] { $"{h.PrefixNow()} Last.pose" },
            h.SnapshotFiles(h.DayNow()));
        Assert.True(h.ErrorCount >= 1);
    }
}
