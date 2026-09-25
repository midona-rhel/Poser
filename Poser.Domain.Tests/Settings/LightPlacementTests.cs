using System.Numerics;
using Poser.Core;

namespace Poser.Tests.Core;

public sealed class LightPlacementTests
{
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(2f, -3f, 4f)]
    public void Light_is_one_yalm_along_camera_ray_and_beams_in_that_direction(float x, float y, float z)
    {
        var camera = new Vector3(10f, -20f, 30f);
        var direction = new Vector3(x, y, z);
        var forward = Vector3.Normalize(direction);
        var placement = LightPlacement.FromCamera(camera, direction, Vector3.One);
        var offset = placement.Position - camera;

        Assert.InRange(Vector3.Distance(offset, forward), 0f, 0.00001f);
        Assert.InRange(offset.Length(), 0.99999f, 1.00001f);
        Assert.True(Vector3.Dot(offset, forward) > 0f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, placement.Rotation), forward), 0f, 0.00001f);
        Assert.Equal(Vector3.One, placement.Scale);
    }

    [Fact]
    public void Move_to_camera_preserves_existing_nonuniform_scale()
    {
        var scale = new Vector3(0.5f, 2f, 3f);
        var placement = LightPlacement.FromCamera(Vector3.Zero, -Vector3.UnitZ, scale);
        Assert.Equal(scale, placement.Scale);
    }

    [Fact]
    public void Forward_distance_remains_small_and_nonzero()
    {
        Assert.InRange(LightPlacement.CameraForwardDistance, 0.1f, 1f);
    }
}
