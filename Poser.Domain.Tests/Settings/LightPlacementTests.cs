using System.Numerics;
using Poser.Domain.Posing;

namespace Poser.Tests.Core;

public sealed class LightPlacementTests
{
    [Fact]
    public void Light_is_one_yalm_along_camera_ray_and_beams_in_that_direction()
    {
        var camera = new Vector3(10f, -20f, 30f);
        var direction = new Vector3(2f, -3f, 4f);
        var forward = Vector3.Normalize(direction);
        var scale = new Vector3(0.5f, 2f, 3f);
        var placement = LightPlacement.FromCamera(camera, direction, scale);
        var offset = placement.Position - camera;

        Assert.InRange(Vector3.Distance(offset, forward), 0f, 0.00001f);
        Assert.InRange(offset.Length(), 0.99999f, 1.00001f);
        Assert.True(Vector3.Dot(offset, forward) > 0f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, placement.Rotation), forward), 0f, 0.00001f);
        Assert.Equal(scale, placement.Scale);
    }
}
