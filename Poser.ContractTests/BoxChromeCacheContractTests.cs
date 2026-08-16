using System.Numerics;
using Poser.UI;

namespace Poser.ContractTests;

public sealed class BoxChromeCacheContractTests : IDisposable
{
    public void Dispose() => Crystarium.PanelShadowTextureUploader = null;

    [Fact]
    public void Compatible_panels_reuse_one_fixed_asset_without_size_keying()
    {
        var uploader = new CountingUploader();
        Crystarium.PanelShadowTextureUploader = uploader.Upload;
        var sink = new CountingSink();
        var shadow = SoftShadow(new Vector4(0.12f, 0.2f, 0.3f, 0.6f));

        Assert.True(Draw(sink, shadow, new Vector2(20f, 30f), new Vector2(220f, 180f)));
        Assert.True(Draw(sink, shadow, new Vector2(400f, 90f), new Vector2(520f, 150f)));

        Assert.Equal(1, uploader.UploadCount);
        Assert.Equal(1, BoxShadowTextureCache.EntryCount);
        Assert.Equal(16, sink.DrawCount);
        Assert.All(sink.Handles, handle => Assert.Equal((nint)1, handle));
        Assert.Single(uploader.Widths);
        Assert.Single(uploader.Heights);
    }

    [Fact]
    public void Paint_and_scale_changes_build_distinct_assets()
    {
        var uploader = new CountingUploader();
        Crystarium.PanelShadowTextureUploader = uploader.Upload;
        var sink = new CountingSink();
        var min = new Vector2(20f, 30f);
        var max = new Vector2(220f, 180f);

        Assert.True(Draw(sink, SoftShadow(new Vector4(1f, 0f, 0f, 0.5f)), min, max));
        Assert.True(Draw(sink, SoftShadow(new Vector4(0f, 1f, 0f, 0.5f)), min, max));
        Assert.True(Draw(sink, SoftShadow(new Vector4(0f, 1f, 0f, 0.5f)), min, max, 1.25f));

        Assert.Equal(3, uploader.UploadCount);
        Assert.Equal(3, BoxShadowTextureCache.EntryCount);
    }

    [Fact]
    public void Uploader_replacement_disposes_old_keepalives_and_invalidates_entries()
    {
        var first = new CountingUploader();
        Crystarium.PanelShadowTextureUploader = first.Upload;
        var sink = new CountingSink();
        var shadow = SoftShadow(new Vector4(0.2f, 0.2f, 0.2f, 0.75f));
        Assert.True(Draw(sink, shadow, new Vector2(10f, 10f), new Vector2(210f, 150f)));
        var keepalive = first.Keepalives[0];

        var second = new CountingUploader();
        Crystarium.PanelShadowTextureUploader = second.Upload;

        Assert.True(keepalive.Disposed);
        Assert.Equal(0, BoxShadowTextureCache.EntryCount);
        Assert.True(Draw(sink, shadow, new Vector2(10f, 10f), new Vector2(210f, 150f)));
        Assert.Equal(1, second.UploadCount);
        Assert.False(second.Keepalives[0].Disposed);
    }

    private static bool Draw(
        CountingSink sink,
        BoxShadow shadow,
        Vector2 min,
        Vector2 max,
        float scale = 1f)
        => BoxShadowTextureCache.TryDraw(
            sink,
            min,
            max,
            shadow,
            12f,
            scale,
            0.9f);

    private static BoxShadow SoftShadow(Vector4 color) =>
        new(2f, 3f, 8f, color, 2f);

    private sealed class CountingSink : BoxShadowTextureCache.IShadowDrawSink
    {
        public int DrawCount { get; private set; }
        public List<nint> Handles { get; } = new();

        public void AddImage(
            nint handle,
            Vector2 min,
            Vector2 max,
            Vector2 uvMin,
            Vector2 uvMax)
        {
            DrawCount++;
            Handles.Add(handle);
        }
    }

    private sealed class CountingUploader
    {
        public int UploadCount { get; private set; }
        public List<int> Widths { get; } = new();
        public List<int> Heights { get; } = new();
        public List<DisposalProbe> Keepalives { get; } = new();

        public (nint, IDisposable?) Upload(byte[] pixels, int width, int height)
        {
            UploadCount++;
            Widths.Add(width);
            Heights.Add(height);
            var keepalive = new DisposalProbe();
            Keepalives.Add(keepalive);
            return ((nint)UploadCount, keepalive);
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
