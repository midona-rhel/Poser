using System;
using System.IO;
using Poser.Files;

namespace Poser.Tests.Files;

public sealed class AutoSaveHealthStoreTests
{
    [Fact]
    public void Health_state_round_trips_evidence_and_recovers_only_nonterminal_records()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        var record = AutoSaveHealthRecord.Create(
            "op-1", "interval", AutoSaveHealthStatus.Queued,
            DateTime.UtcNow, DateTime.UtcNow,
            intendedActors: 3, writtenActors: 1,
            affectedPaths: new[] { "a.pose", "b.pose" },
            failurePhase: "ActorWrite", detail: "partial",
            recoveryEvidencePaths: new[] { "a.tmp" });

        Assert.True(store.Write(record).Succeeded);
        var recovery = store.RecoverStale();
        var read = store.Read()!;

        Assert.True(recovery.Succeeded);
        Assert.True(recovery.PromotionAttempted);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, read.Status);
        Assert.Equal("Interrupted", read.FailurePhase);
        Assert.Equal(1, read.WrittenActors);
        Assert.Equal(new[] { "a.tmp" }, read.RecoveryEvidencePaths);

        Assert.True(store.Write(record.With(status: AutoSaveHealthStatus.Written)).Succeeded);
        var terminal = store.RecoverStale();
        Assert.True(terminal.Succeeded);
        Assert.False(terminal.PromotionAttempted);
    }

    [Fact]
    public void Health_read_rejects_corruption_and_writes_bounded_records()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, AutoSaveHealthStore.FileName), "{ invalid");
        var store = new AutoSaveHealthStore(root.Path);

        Assert.Null(store.Read());
        Assert.True(store.Write(AutoSaveHealthRecord.Create(
            new string('x', 1000), new string('r', 1000),
            AutoSaveHealthStatus.Pending, DateTime.UtcNow, DateTime.UtcNow,
            affectedPaths: new[] { new string('p', 10000) })).Succeeded);
        Assert.InRange(store.Read()!.OperationId.Length, 1, 128);
    }

    [Fact]
    public void Mutable_filesystem_failure_keeps_the_previous_record_and_reports_evidence()
    {
        using var root = new TempRoot();
        var normal = new AutoSaveHealthStore(root.Path);
        Assert.True(normal.Write(AutoSaveHealthRecord.Create(
            "old", "interval", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow)).Succeeded);

        var fileSystem = new MutableHealthFileSystem { FailReplace = true };
        var failing = new AutoSaveHealthStore(root.Path, fileSystem);
        var result = failing.Write(AutoSaveHealthRecord.Create(
            "new", "final", AutoSaveHealthStatus.Queued,
            DateTime.UtcNow, DateTime.UtcNow));

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.RecoveryEvidencePaths);
        Assert.Equal("old", normal.Read()!.OperationId);
        fileSystem.FailReplace = false;
        Assert.True(failing.Write(AutoSaveHealthRecord.Create(
            "new", "final", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow)).Succeeded);
        Assert.Equal("new", normal.Read()!.OperationId);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-health-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class MutableHealthFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner =
            new SystemAutoSaveHealthFileSystem();

        public bool FailReplace { get; set; }
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup)
        {
            if (FailReplace)
                throw new IOException("injected replace failure");
            _inner.Replace(source, destination, backup);
        }
        public void Move(string source, string destination) =>
            _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
    }
}
