using System.Numerics;
using Poser.Domain.Presentation;
using Poser.Game.Presentation;

namespace Poser.Game.Tests.Journal;

public sealed class AppearanceColorTests
{
    [Fact]
    public void Native_channel_writes_preserve_other_colours_and_non_RGB_lanes()
    {
        var data = new FFXIVClientStructs.FFXIV.Shader.CustomizeParameter();
        data.SkinColor.W = 0.7f;
        data.LeftColor.W = 0.8f;
        data.RightColor.W = 0.9f;
        foreach (var channel in Enum.GetValues<AppearanceColorChannel>())
            PresentationRuntimePort.WriteColor(ref data, channel, new Vector4(0.5f, 0.25f, -0.5f, 0.3f));
        PresentationRuntimePort.WriteColor(ref data, AppearanceColorChannel.Hair, Vector4.One);
        Assert.Equal(0.7f, data.SkinColor.W);
        Assert.Equal(0.8f, data.LeftColor.W);
        Assert.Equal(0.9f, data.RightColor.W);
        Assert.Equal(0.3f, data.LipColor.W);
        Assert.Equal(0.25f, data.SkinColor.X);
        Assert.Equal(-0.25f, data.OptionColor.Z);
        Assert.Equal(1f, data.MainColor.X);
        Assert.Equal(0.25f, data.MeshColor.X);
    }

    [Fact]
    public void Signed_RGB_round_trips_and_mouth_alpha_stays_linear()
    {
        var value = new Vector4(-0.5f, 0.25f, 2f, 0.3f);
        var shader = AppearanceColorSpace.ToShader(value);
        Assert.Equal(new Vector4(-0.25f, 0.0625f, 4f, 0.3f), shader);
        Assert.Equal(value, AppearanceColorSpace.FromShader(shader));
    }
}
