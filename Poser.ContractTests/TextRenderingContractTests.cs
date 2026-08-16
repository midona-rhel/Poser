using System.Numerics;
using Dalamud.Interface.ManagedFontAtlas;
using NSubstitute;
using Poser.UI;

namespace Poser.ContractTests;

public sealed class TextRenderingContractTests
{
    [Fact]
    public void Small_font_rasterization_and_measurement_reuse_stay_exact()
    {
        var dark = FontRegistry.RasterizationContract(light: false);
        var light = FontRegistry.RasterizationContract(light: true);
        var cjk = FontRegistry.RasterizationContract(
            light: false,
            mergedFallback: true);

        Assert.Equal(2, dark.OversampleH);
        Assert.Equal(1, dark.OversampleV);
        Assert.False(dark.PixelSnapH);
        Assert.Equal(1f, dark.RasterizerMultiply);
        Assert.Equal(1.7f, dark.RasterizerGamma);
        Assert.Equal(1f, light.RasterizerGamma);
        Assert.Equal(1, cjk.OversampleH);
        Assert.True(cjk.PixelSnapH);

        var firstFace = Substitute.For<IFontHandle>();
        var rebuiltFace = Substitute.For<IFontHandle>();
        var memo = new TextMeasurementMemo();
        var firstSize = new Vector2(42.25f, 13f);

        Assert.False(memo.TryGet(10, firstFace, "Translation", out _));
        memo.Store(10, firstFace, "Translation", firstSize);
        Assert.True(memo.TryGet(10, firstFace, "Translation", out var sameFrame));
        Assert.Equal(firstSize, sameFrame);

        Assert.True(memo.TryGet(11, firstFace, "Translation", out var nextFrame));
        Assert.Equal(firstSize, nextFrame);
        Assert.False(memo.TryGet(11, rebuiltFace, "Translation", out _));
        Assert.False(memo.TryGet(11, firstFace, "Rotation", out _));

        memo.Store(11, rebuiltFace, "Translation", new Vector2(43f, 13f));
        Assert.True(memo.TryGet(12, rebuiltFace, "Translation", out var rebuilt));
        Assert.Equal(new Vector2(43f, 13f), rebuilt);
        Assert.False(memo.TryGet(13, firstFace, "Translation", out _));
        Assert.True(memo.TryGet(13, rebuiltFace, "Translation", out _));
    }
}
