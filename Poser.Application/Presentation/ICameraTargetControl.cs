using Poser.Application.Transforms;
using Poser.Domain;
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
    Outcome Follow(CameraId id, ActorId actor, string displayName);
    Outcome SetTargetLocked(CameraId id, bool value);
    Outcome ToggleGameTarget(CameraId id);
    Outcome SetTracking(CameraId id, bool value);
    Outcome SetTrackingMode(CameraId id, CameraTrackingMode value);
    Outcome ToggleTrackedBone(CameraId id, BoneId bone);
    Outcome CenterOnActor(ActorId actor);
    Outcome Recenter(CameraId id, SelectionId? selection);
    Outcome CenterTrackedActor(CameraId id);
}
