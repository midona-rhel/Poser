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
        var record = AutoSaveHealthRecord.Create(
            "op-1", "interval", AutoSaveHealthStatus.DispatchAccepted,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 2,
            affectedPaths: new[] { "a.pose", "b.pose" });

        var write = store.Write(record);
        Assert.True(write.Succeeded, write.Detail);
        Assert.Equal(AutoSaveHealthStore.FileName, Path.GetFileName(store.HealthPath));
        Assert.Equal(AutoSaveHealthStatus.DispatchAccepted, store.Read()!.Status);
    }

    [Fact]
    public void Stale_nonterminal_record_is_promoted_to_recovery_required()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        Assert.True(store.Write(AutoSaveHealthRecord.Create(
            "stale", "final", AutoSaveHealthStatus.Queued,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 1)).Succeeded);

        var recovered = store.RecoverStale().Record;

        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, recovered!.Status);
        Assert.Equal("Interrupted", recovered.FailurePhase);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, store.Read()!.Status);
    }

    [Fact]
    public void Oversized_health_record_is_bounded_before_write()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        var result = store.Write(AutoSaveHealthRecord.Create(
            new string('x', 1000), new string('r', 1000), AutoSaveHealthStatus.Pending,
            DateTime.UtcNow, DateTime.UtcNow,
            affectedPaths: new[] { new string('p', 10000) }));

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

    [Fact]
    public void Health_record_preserves_operation_evidence_fields()
    {
        using var root = new TempRoot();
        var store = new AutoSaveHealthStore(root.Path);
        var result = store.Write(AutoSaveHealthRecord.Create(
            "op-evidence", "final", AutoSaveHealthStatus.RecoveryRequired,
            DateTime.UtcNow, DateTime.UtcNow,
            intendedActors: 3,
            writtenActors: 1,
            affectedPaths: new[] { "a.pose", "b.pose" },
            failurePhase: "ActorWrite",
            detail: "write failed",
            recoveryEvidencePaths: new[] { "a.tmp" }));

        Assert.True(result.Succeeded);
        var read = store.Read()!;
        Assert.Equal("op-evidence", read.OperationId);
        Assert.Equal(1, read.WrittenActors);
        Assert.Equal("ActorWrite", read.FailurePhase);
        Assert.Equal(new[] { "a.tmp" }, read.RecoveryEvidencePaths);
    }

    [Fact]
    public void Partial_replace_retains_old_record_and_recovery_evidence()
    {
        using var root = new TempRoot();
        var real = new SystemAutoSaveHealthFileSystem();
        var store = new AutoSaveHealthStore(root.Path, new PartialReplaceFileSystem(real));
        var old = AutoSaveHealthRecord.Create(
            "old", "interval", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 1, writtenActors: 1);
        Assert.True(new AutoSaveHealthStore(root.Path).Write(old).Succeeded);

        var result = store.Write(AutoSaveHealthRecord.Create(
            "new", "final", AutoSaveHealthStatus.DispatchAccepted,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 1));

        Assert.False(result.Succeeded);
        Assert.Contains(store.HealthPath, result.RecoveryEvidencePaths);
        Assert.NotNull(new AutoSaveHealthStore(root.Path).Read());
    }

    [Fact]
    public void Backup_cleanup_failure_is_recovery_evidence()
    {
        using var root = new TempRoot();
        var real = new SystemAutoSaveHealthFileSystem();
        var normal = new AutoSaveHealthStore(root.Path, real);
        Assert.True(normal.Write(AutoSaveHealthRecord.Create(
            "old", "interval", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow)).Succeeded);

        var failing = new AutoSaveHealthStore(root.Path, new CleanupFailureFileSystem(real));
        var result = failing.Write(AutoSaveHealthRecord.Create(
            "new", "final", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow));

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.RecoveryEvidencePaths);
    }

    [Fact]
    public void Replace_that_loses_the_destination_retains_backup_and_validated_temp()
    {
        using var root = new TempRoot();
        var real = new SystemAutoSaveHealthFileSystem();
        var normal = new AutoSaveHealthStore(root.Path, real);
        Assert.True(normal.Write(AutoSaveHealthRecord.Create(
            "old", "interval", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow)).Succeeded);

        var failing = new AutoSaveHealthStore(
            root.Path,
            new DestinationLossFileSystem(real));
        var result = failing.Write(AutoSaveHealthRecord.Create(
            "new", "final", AutoSaveHealthStatus.Written,
            DateTime.UtcNow, DateTime.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Contains(result.RecoveryEvidencePaths,
            path => path.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Contains(result.RecoveryEvidencePaths,
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Null(new AutoSaveHealthStore(root.Path).Read());
    }

    [Fact]
    public void Stale_promotion_failure_is_typed_and_retains_the_interrupted_record()
    {
        using var root = new TempRoot();
        var real = new SystemAutoSaveHealthFileSystem();
        var normal = new AutoSaveHealthStore(root.Path, real);
        Assert.True(normal.Write(AutoSaveHealthRecord.Create(
            "stale", "final", AutoSaveHealthStatus.Queued,
            DateTime.UtcNow, DateTime.UtcNow, intendedActors: 1)).Succeeded);

        var failing = new AutoSaveHealthStore(
            root.Path,
            new WriteFailureFileSystem(real));
        var recovery = failing.RecoverStale();

        Assert.False(recovery.Succeeded);
        Assert.Equal(AutoSaveHealthStatus.RecoveryRequired, recovery.Record!.Status);
        Assert.Equal("Interrupted", recovery.Record.FailurePhase);
        Assert.False(recovery.Write!.Succeeded);
        Assert.NotNull(new AutoSaveHealthStore(root.Path).Read());
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-health-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class PartialReplaceFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner;
        public PartialReplaceFileSystem(IAutoSaveHealthFileSystem inner) => _inner = inner;
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
        public void Replace(string source, string destination, string backup)
        {
            File.Copy(destination, backup, overwrite: true);
            throw new IOException("simulated partial replace");
        }
    }

    private sealed class CleanupFailureFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner;
        public CleanupFailureFileSystem(IAutoSaveHealthFileSystem inner) => _inner = inner;
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) => _inner.Replace(source, destination, backup);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path)
        {
            if (path.EndsWith(".bak", StringComparison.Ordinal))
                throw new IOException("simulated cleanup failure");
            _inner.Delete(path);
        }
    }

    private sealed class DestinationLossFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner;
        public DestinationLossFileSystem(IAutoSaveHealthFileSystem inner) => _inner = inner;
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
        public void Replace(string source, string destination, string backup)
        {
            File.Copy(destination, backup, overwrite: true);
            File.Delete(destination);
            throw new IOException("simulated destination loss");
        }
    }

    private sealed class WriteFailureFileSystem : IAutoSaveHealthFileSystem
    {
        private readonly IAutoSaveHealthFileSystem _inner;
        public WriteFailureFileSystem(IAutoSaveHealthFileSystem inner) => _inner = inner;
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => throw new IOException("simulated health write failure");
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) => _inner.Replace(source, destination, backup);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
    }
}
