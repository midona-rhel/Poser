using System;
using System.IO;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class AutoSaveHealthStoreTests
{
    [Fact]
    public void Health_record_round_trips_atomically_at_root()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        var record = new AutoSaveHealthRecord
        {
            OperationId = "op-1",
            Reason = "interval",
            Status = AutoSaveHealthStatus.DispatchAccepted,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            IntendedActors = 2,
            AffectedPaths = new[] { "a.pose", "b.pose" },
        };

        Assert.True(store.Write(record).Succeeded);
        Assert.Equal(AutoSaveHealthStore.FileName, Path.GetFileName(store.HealthPath));
        Assert.Equal(AutoSaveHealthStatus.DispatchAccepted, store.Read()!.Status);
    }

    [Fact]
    public void Stale_nonterminal_record_is_promoted_to_recovery_required()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        Assert.True(store.Write(new AutoSaveHealthRecord
        {
            OperationId = "stale",
            Reason = "final",
            Status = AutoSaveHealthStatus.Queued,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            IntendedActors = 1,
        }).Succeeded);

        var recovered = store.RecoverStale();

        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, recovered!.Status);
        Assert.Equal("Interrupted", recovered.FailurePhase);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, store.Read()!.Status);
    }

    [Fact]
    public void Oversized_health_record_is_bounded_before_write()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        var result = store.Write(new AutoSaveHealthRecord
        {
            OperationId = new string('x', 1000),
            Reason = new string('r', 1000),
            Status = AutoSaveHealthStatus.Pending,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            AffectedPaths = new[] { new string('p', 10000) },
        });

        Assert.True(result.Succeeded);
        Assert.True(store.Read()!.OperationId.Length <= 128);
    }

    [Fact]
    public void Invalid_health_json_is_not_published_as_a_record()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, AutoSaveHealthStore.FileName), "{ invalid");

        Assert.Null(new AutoSaveHealthStore(root.Path).Read());
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-health-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
