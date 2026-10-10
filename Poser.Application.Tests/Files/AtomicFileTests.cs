using System;
using System.IO;
using Poser.Documents.Files;

namespace Poser.Application.Tests.Files;

public sealed class AtomicFileTests
{
    [Fact]
    public void Interrupted_write_keeps_the_original_and_leaves_no_temp()
    {
        var root = Directory.CreateTempSubdirectory("poser-atomic-");
        try
        {
            var path = Path.Combine(root.FullName, "target.json");
            File.WriteAllText(path, "original");

            foreach (var failAt in new[] { AtomicWritePhase.WriteTemporary, AtomicWritePhase.FlushTemporary })
            {
                var result = AtomicFile.Write(
                    new FlushFailingFileSystem(), path, "replacement"u8.ToArray(),
                    new AtomicWriteOptions
                    {
                        Subject = "test",
                        BeforePhase = (phase, _) =>
                        {
                            if (phase == failAt && failAt == AtomicWritePhase.WriteTemporary)
                                throw new IOException("injected write failure");
                        },
                    });

                Assert.False(result.Succeeded);
                Assert.Equal(failAt, result.Phase);
                Assert.Empty(result.RecoveryEvidencePaths);
                Assert.Equal("original", File.ReadAllText(path));
                Assert.Single(root.GetFiles());
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Failed_replace_deletes_the_temp_unless_evidence_is_requested()
    {
        var root = Directory.CreateTempSubdirectory("poser-atomic-");
        try
        {
            var path = Path.Combine(root.FullName, "target.json");
            File.WriteAllText(path, "original");

            foreach (var keep in new[] { false, true })
            {
                var result = AtomicFile.Write(
                    new ReplaceFailingFileSystem(), path, "replacement"u8.ToArray(),
                    new AtomicWriteOptions { Subject = "test", KeepTemporaryOnFailure = keep });

                Assert.False(result.Committed);
                Assert.Equal(AtomicWritePhase.ReplaceDestination, result.Phase);
                Assert.Equal("original", File.ReadAllText(path));
                Assert.Equal(keep ? 2 : 1, root.GetFiles().Length);
                Assert.Equal(keep ? 1 : 0, result.RecoveryEvidencePaths.Count);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Replace is refused as by a sync client or network share.
    private sealed class ReplaceFailingFileSystem : IAtomicFileSystem
    {
        private readonly SystemAtomicFileSystem _inner = new();
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) =>
            throw new IOException("injected replace failure");
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
    }

    // Writes go through; only the durable flush fails, as on a full disk.
    private sealed class FlushFailingFileSystem : IAtomicFileSystem
    {
        private readonly SystemAtomicFileSystem _inner = new();
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream CreateNew(string path) => _inner.CreateNew(path);
        public void FlushToDisk(Stream stream) => throw new IOException("injected flush failure");
        public bool Exists(string path) => _inner.Exists(path);
        public void Replace(string source, string destination, string backup) =>
            _inner.Replace(source, destination, backup);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
    }
}
