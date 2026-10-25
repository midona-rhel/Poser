using System.Numerics;
using NSubstitute;
using Poser.Game.Cameras;
using Poser.Game.Entities;

namespace Poser.Game.Tests.Scene;

public sealed class SceneCameraCaptureTests
{
    [Fact]
    public void Main_camera_snapshot_preserves_rendered_position_without_applying_offsets_twice()
    {
        var camera = Substitute.For<IVirtualCamera>();
        camera.IsDefault.Returns(true);
        camera.WorldPosition.Returns(new Vector3(20, 5, -10));
        camera.PositionOffset.Returns(new Vector3(1, 2, 3));
        camera.TargetOffset.Returns(new Vector3(4, 0, -2));
        camera.Position.Returns(Vector3.Zero);
        var saved = CameraDocument.CaptureScene(camera);
        Assert.Equal(camera.WorldPosition, saved.FixedPosition!.Value + saved.PositionOffset + camera.TargetOffset);
        Assert.Null(camera.FixedPosition);
    }
}
