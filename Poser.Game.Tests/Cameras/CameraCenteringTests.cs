using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Cameras;

namespace Poser.Game.Tests.Cameras;

public sealed class CameraCenteringTests
{
    [Fact]
    public void Centering_an_actor_translates_the_shot_without_refitting_it()
    {
        var camera = new FakeCamera
        {
            PositionOffset = new(1, 2, 3), Zoom = 17f, FoV = 0.8f,
            Angle = new(0.4f, 0.7f), Pan = new(2, 4), Roll = 0.2f,
            Orthographic = true, OrthographicZoom = 12f,
            TargetOffset = new(7, 8, 9), TargetActorName = "Followed actor",
            IsTargetLocked = true,
        };
        var pivot = new Vector3(20, 30, 40);
        Assert.True(CameraTargeting.TranslateOrbitPivot(camera, pivot, new(10, 10, 10)).Success);
        Assert.Equal(new Vector3(11, 22, 33), camera.PositionOffset);
        Assert.Equal(17f, camera.Zoom);
        Assert.Equal(0.8f, camera.FoV);
        Assert.Equal(new Vector2(0.4f, 0.7f), camera.Angle);
        Assert.Equal(new Vector2(2, 4), camera.Pan);
        Assert.Equal(0.2f, camera.Roll);
        Assert.True(camera.Orthographic);
        Assert.Equal(12f, camera.OrthographicZoom);
        Assert.Equal(new Vector3(7, 8, 9), camera.TargetOffset);
        Assert.Equal("Followed actor", camera.TargetActorName);
        Assert.True(camera.IsTargetLocked);
        Assert.True(CameraTargeting.TranslateOrbitPivot(camera, pivot, pivot).Success);
        Assert.Equal(new Vector3(11, 22, 33), camera.PositionOffset);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_centering_pivots_leave_the_shot_untouched(float invalid)
    {
        var camera = new FakeCamera { PositionOffset = new(1, 2, 3), Zoom = 17f };
        Assert.False(CameraTargeting.TranslateOrbitPivot(camera, new(invalid, 0, 0), Vector3.Zero).Success);
        Assert.Equal(new Vector3(1, 2, 3), camera.PositionOffset);
        Assert.Equal(17f, camera.Zoom);
    }

    private sealed class FakeCamera : IVirtualCamera
    {
        public bool IsValid => true;
        public string Name { get; set; } = "Camera";
        public CameraKind Kind => CameraKind.Game;
        public bool IsLive => false;
        public bool IsDefault => false;
        public bool IsLocked { get; set; }
        public Vector2 Angle { get; set; }
        public Vector2 Pan { get; set; }
        public float Roll { get; set; }
        public float Zoom { get; set; }
        public Vector2 ZoomLimits => new(1f, 20f);
        public float FoV { get; set; }
        public Vector3 PositionOffset { get; set; }
        public Vector3 TargetOffset { get; set; }
        public string TargetActorName { get; set; } = string.Empty;
        public ActorId? TargetActorId { get; set; }
        public bool IsTargetLocked { get; set; }
        public IActor? TargetActor => null;
        public Vector3 WorldPosition => Vector3.Zero;
        public Vector3? FixedPosition { get; set; }
        public bool DisableCollision { get; set; }
        public bool DelimitCamera { get; set; }
        public bool IsPortraitMode => false;
        public void TogglePortraitMode() { }
        public Vector3 Position { get; set; }
        public Vector3 SpawnPosition => Vector3.Zero;
        public Vector3 Rotation { get; set; }
        public bool MovementEnabled { get; set; }
        public bool Move2D { get; set; }
        public float MovementSpeed { get; set; }
        public float MouseSensitivity { get; set; }
        public bool DelimitAngle { get; set; }
        public bool Orthographic { get; set; }
        public float OrthographicZoom { get; set; }
        public bool IsTracking { get; set; }
        public CameraTrackingMode TrackingMode { get; set; }
        public IList<IBone> TrackedBones { get; } = new List<IBone>();
        public void ResetProperties() { }
        public float DefaultFoV => 0f;
        public float DefaultRoll => 0f;
        public Vector3 DefaultRotation => Vector3.Zero;
        public void CaptureOwnedDefaults() { }
    }
}
