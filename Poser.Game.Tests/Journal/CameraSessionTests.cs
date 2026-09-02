using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Tests.Journal;

public sealed class CameraSessionTests
{
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
        public IVirtualCamera? CreateCamera(CameraKind kind) => null;
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
