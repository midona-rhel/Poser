using System;
using System.IO;
using Poser.Files;

namespace Poser.Tests.Files;

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
