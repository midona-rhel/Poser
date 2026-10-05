using Poser.Domain.Cameras;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Game.Cameras;
using Poser.Services;

namespace Poser.Game.Tests.Journal;

public sealed class CameraSessionTests
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
        Assert.True(VirtualCameraService.TranslateOrbitPivot(camera, pivot, new(10, 10, 10)).Success);
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
        Assert.True(VirtualCameraService.TranslateOrbitPivot(camera, pivot, pivot).Success);
        Assert.Equal(new Vector3(11, 22, 33), camera.PositionOffset);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_centering_pivots_leave_the_shot_untouched(float invalid)
    {
        var camera = new FakeCamera { PositionOffset = new(1, 2, 3), Zoom = 17f };
        Assert.False(VirtualCameraService.TranslateOrbitPivot(camera, new(invalid, 0, 0), Vector3.Zero).Success);
        Assert.Equal(new Vector3(1, 2, 3), camera.PositionOffset);
        Assert.Equal(17f, camera.Zoom);
    }

    [Fact]
    public void A_locked_camera_takes_no_value_and_journals_nothing()
    {
        var history = new TransformHistory();
        var session = new CameraSession(new ValueJournal(history), new NoCameras(), new NoBindings());
        var camera = new FakeCamera { IsLocked = true, Zoom = 2f };

        Assert.False(session.SetZoom(camera, 5f));

        Assert.Equal(2f, camera.Zoom);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void An_unlocked_camera_journals_the_value_and_the_lock_is_a_step_of_its_own()
    {
        var history = new TransformHistory();
        var session = new CameraSession(new ValueJournal(history), new NoCameras(), new NoBindings());
        var camera = new FakeCamera { Zoom = 2f };

        Assert.True(session.SetZoom(camera, 5f));
        session.SetLocked(camera, true);

        Assert.Equal("Lock camera", history.UndoDescription);
        var lockStep = (JournalStep)history.PeekUndo()!;
        Assert.True(lockStep.Undo());
        history.CommitUndo(lockStep);
        Assert.False(camera.IsLocked);
        var zoomStep = (JournalStep)history.PeekUndo()!;
        Assert.True(zoomStep.Undo());
        Assert.Equal(2f, camera.Zoom);
    }

    private sealed class NoBindings : IEntityBindings
    {
        public ActorId? GetActorId(IActor actor) => null;
        public BoneId? GetBoneId(IBone bone) => null;
        public LightId? GetLightId(ILight light) => null;
        public CameraId? GetCameraId(IVirtualCamera camera) => null;
        public PropId? GetPropId(IPropHandle prop) => null;
        public WorldObjectId? GetWorldObjectId(IWorldObject worldObject) => null;
        public OverlayId? GetOverlayId(IOverlayNode overlay) => null;
        public BindingResult<IActor> Resolve(ActorId id) => new(BindingStatus.Missing);
        public BindingResult<IBone> Resolve(BoneId id) => new(BindingStatus.Missing);
        public BindingResult<ILight> Resolve(LightId id) => new(BindingStatus.Missing);
        public BindingResult<IVirtualCamera> Resolve(CameraId id) => new(BindingStatus.Missing);
        public BindingResult<IPropHandle> Resolve(PropId id) => new(BindingStatus.Missing);
        public BindingResult<IWorldObject> Resolve(WorldObjectId id) => new(BindingStatus.Missing);
        public BindingResult<IOverlayNode> Resolve(OverlayId id) => new(BindingStatus.Missing);
        public ISkeleton? ResolveSkeleton(SkeletonId id) => null;
    }

    private sealed class NoCameras : IVirtualCameraService
    {
        public bool SuppressFlightKeys { get; set; }
        public bool FlightActive => false;
        public bool IsAvailable => true;
        public IReadOnlyList<IVirtualCamera> Cameras => Array.Empty<IVirtualCamera>();
        public IVirtualCamera? LiveCamera => null;
        public FreeCameraSpeedNotice? SpeedNotice => null;
        public IVirtualCamera? CreateCamera(CameraKind kind, bool makeLive = true) => null;
        public IVirtualCamera? CloneCamera(IVirtualCamera source) => null;
        public void DestroyCamera(IVirtualCamera camera) { }
        public void DestroyAllCameras() { }
        public void SetLive(IVirtualCamera camera) { }
        public bool SetTargetActor(IVirtualCamera camera, IActor actor, ActorId actorId, string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
        public CameraCenterResult CenterOnActor(IActor actor) => CameraCenterResult.Refused("none");
        public CameraCenterResult CenterOnBone(IBone bone) => CameraCenterResult.Refused("none");
        public void Dispose() { }
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
