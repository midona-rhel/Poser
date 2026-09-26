using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Presentation;

public sealed record CameraTargetReading(
    CameraId Id, bool IsLocked, bool IsTracking, CameraTrackingMode TrackingMode,
    bool IsTargetLocked, ActorId? FollowedActor, ActorId? GameTarget,
    string? GameTargetName, ActorId? TrackingActor, IReadOnlyList<BoneId> TrackedBones,
    bool CanCenterTrackedActor);

/// <summary>Camera targeting uses exact scene identities, never native actor or bone references.</summary>
public interface ICameraTargetControl
{
    CameraTargetReading? Read(CameraId id);
    ValueWriteResult Follow(CameraId id, ActorId actor, string displayName);
    ValueWriteResult SetTargetLocked(CameraId id, bool value);
    ValueWriteResult ToggleGameTarget(CameraId id);
    ValueWriteResult SetTracking(CameraId id, bool value);
    ValueWriteResult SetTrackingMode(CameraId id, CameraTrackingMode value);
    ValueWriteResult ToggleTrackedBone(CameraId id, BoneId bone);
    ValueWriteResult CenterOnActor(ActorId actor);
    ValueWriteResult Recenter(CameraId id, SelectionId? selection);
    ValueWriteResult CenterTrackedActor(CameraId id);
}
