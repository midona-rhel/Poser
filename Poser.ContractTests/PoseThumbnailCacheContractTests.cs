extern alias ProductionPoser;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Files;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

public sealed class PoseThumbnailCacheContractTests
{
    [Fact]
    public void Valid_input_is_forwarded_and_malformed_inputs_are_rejected()
    {
        var provider = Substitute.For<ITextureProvider>();
        provider.CreateFromImageAsync(
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDalamudTextureWrap>(null!));
        using var cache = new PoseThumbnailCache(provider);
        using var fixture = new ImageFixture();

        cache.Get(fixture.WriteValid("valid.pose"));
        Assert.Equal(nint.Zero, cache.Get(fixture.WriteOversized("oversized.pose")));
        Assert.Equal(nint.Zero, cache.Get(
            fixture.WriteDeep("deep.pose", PoseFileLimits.MaxJsonDepth + 1)));

        WaitUntil(() => provider.ReceivedCalls().Any());
        provider.Received(1).CreateFromImageAsync(
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Clear_and_dispose_dispose_wraps_that_complete_late()
    {
        var provider = Substitute.For<ITextureProvider>();
        var pending = new TaskCompletionSource<IDalamudTextureWrap>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.CreateFromImageAsync(
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);
        using var fixture = new ImageFixture();

        var clearedWrap = Substitute.For<IDalamudTextureWrap>();
        using (var cache = new PoseThumbnailCache(provider))
        {
            cache.Get(fixture.WriteValid("clear.pose"));
            WaitUntil(() => provider.ReceivedCalls().Any());
            cache.Clear();
            pending.SetResult(clearedWrap);
            WaitUntil(() => clearedWrap.ReceivedCalls().Any());
        }
        clearedWrap.Received(1).Dispose();

        provider.ClearReceivedCalls();
        var disposedPending = new TaskCompletionSource<IDalamudTextureWrap>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.CreateFromImageAsync(
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(disposedPending.Task);
        var disposedWrap = Substitute.For<IDalamudTextureWrap>();
        var disposedCache = new PoseThumbnailCache(provider);
        disposedCache.Get(fixture.WriteValid("dispose.pose"));
        WaitUntil(() => provider.ReceivedCalls().Any());
        disposedCache.Dispose();
        disposedPending.SetResult(disposedWrap);

        WaitUntil(() => disposedWrap.ReceivedCalls().Any());
        disposedWrap.Received(1).Dispose();
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(predicate(), "The thumbnail worker did not reach its provider seam.");
    }

    private sealed class ImageFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "poser-thumbnail-tests", Guid.NewGuid().ToString("N"));

        public ImageFixture() => Directory.CreateDirectory(Root);

        public string WriteValid(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "{\"Base64Image\":\"AQID\"}", Encoding.UTF8);
            return path;
        }

        public string WriteDeep(string name, int depth)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "{\"x\":" + new string('[', depth) + "0" + new string(']', depth) + "}");
            return path;
        }

        public string WriteOversized(string name)
        {
            var path = Path.Combine(Root, name);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(PoseFileLimits.MaxFileBytes + 1);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
