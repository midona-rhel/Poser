using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Services;

namespace Poser.Game.Cameras;

public sealed class CameraTargetControl(
    IEntityBindings bindings, IFramework framework, IVirtualCameraService cameras,
    IActorManager actors, IActorSpawnService spawns, CameraSession values) : ICameraTargetControl
{
    private static ValueWriteResult Refused(string detail) => new(false, detail);
    private IVirtualCamera? Resolve(CameraId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { IsValid: true } camera } &&
        bindings.GetCameraId(camera) == id ? camera : null;
    private IActor? Resolve(ActorId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { } actor } &&
        bindings.GetActorId(actor) == id ? actor : null;
    private IBone? Resolve(BoneId id) => framework.IsInFrameworkUpdateThread &&
        bindings.Resolve(id) is { Success: true, Value: { } bone } &&
        bindings.GetBoneId(bone) == id ? bone : null;

    private (ActorId Id, IActor Actor)? GameTarget()
    {
        if (actors.GetGPoseTarget() is { } actor && bindings.GetActorId(actor) is { } id &&
            ReferenceEquals(Resolve(id), actor)) return (id, actor);
        return null;
    }

    private ActorId? TrackingActor(IVirtualCamera camera)
    {
        if (camera.TargetActorId is { } id)
            return Resolve(id) is { } actor && ReferenceEquals(actor, camera.TargetActor) ? id : null;
        return GameTarget()?.Id;
    }

    private bool CanCenter(IVirtualCamera camera, ActorId? actor) =>
        cameras.IsAvailable && !camera.IsLocked && camera.IsLive &&
        camera.Kind != CameraKind.Free && camera.FixedPosition is null &&
        actor is { } id && Resolve(id) is { } current && spawns.IsVisible(current);

    public CameraTargetReading? Read(CameraId id)
    {
        if (Resolve(id) is not { } camera) return null;
        var target = GameTarget();
        var owner = TrackingActor(camera);
        var bones = camera.TrackedBones.Select(bindings.GetBoneId)
            .OfType<BoneId>().ToArray();
        return new(id, camera.IsLocked, camera.IsTracking, camera.TrackingMode,
            camera.IsTargetLocked, camera.TargetActorId, target?.Id, target?.Actor.Name,
            owner, bones, CanCenter(camera, owner));
    }

    public ValueWriteResult Reconcile(CameraId id)
    {
        if (Resolve(id) is not { } camera) return Refused("The camera is no longer available.");
        bool staleTarget = false;
        if (camera.TargetActorId is { } target)
        {
            if (Resolve(target) is not { } current || !ReferenceEquals(current, camera.TargetActor))
            {
                values.ClearTargetActor(camera);
                staleTarget = true;
            }
        }
        else if (camera.IsTargetLocked)
            values.ClearTargetActor(camera);

        var owner = TrackingActor(camera);
        // A retained native wrapper is never authority for a replacement generation.
        for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
        {
            var bone = camera.TrackedBones[i];
            if (bindings.GetBoneId(bone) is not { } boneId ||
                boneId.Skeleton.Actor != owner || !ReferenceEquals(Resolve(boneId), bone))
                camera.TrackedBones.RemoveAt(i);
        }
        return staleTarget ? Refused("Follow: the target actor is no longer available.") : new(true);
    }

    private ValueWriteResult Edit(CameraId id, Func<IVirtualCamera, ValueWriteResult> action)
    {
        if (Resolve(id) is not { } camera || !cameras.IsAvailable)
            return Refused("Camera controls are unavailable.");
        if (camera.IsLocked) return Refused("Unlock the camera first.");
        return action(camera);
    }

    private void ClearOutside(IVirtualCamera camera, ActorId actor)
    {
        if (camera.TrackedBones.Any(bone => bindings.GetBoneId(bone) is not { } id ||
            id.Skeleton.Actor != actor)) camera.TrackedBones.Clear();
    }

    public ValueWriteResult Follow(CameraId id, ActorId actorId, string displayName) => Edit(id, camera =>
    {
        if (camera.IsTracking) return Refused("Follow: turn off bone tracking first.");
        if (camera.IsTargetLocked) return Refused("Follow: unlock the actor first.");
        if (Resolve(actorId) is not { } actor) return Refused("Follow: that actor is no longer available.");
        if (!values.SetTargetActor(camera, actor, actorId, displayName))
            return Refused("Follow: the actor is not drawn yet.");
        ClearOutside(camera, actorId);
        return new(true);
    });

    public ValueWriteResult SetTargetLocked(CameraId id, bool value) => Edit(id, camera =>
    {
        if (!value) { values.ClearTargetActor(camera); return new(true); }
        if (camera.TargetActorId is { } target)
        {
            if (Resolve(target) is { } current && ReferenceEquals(current, camera.TargetActor))
                return new(values.SetTargetLocked(camera, true));
            values.ClearTargetActor(camera);
            return Refused("Follow: that actor is no longer available.");
        }
        if (GameTarget() is { } native &&
            values.SetTargetActor(camera, native.Actor, native.Id, native.Actor.Name))
            return new(values.SetTargetLocked(camera, true));
        return Refused("Follow: no current actor can be locked.");
    });

    public ValueWriteResult ToggleGameTarget(CameraId id) => Edit(id, camera =>
    {
        if (camera.IsTracking || camera.IsTargetLocked)
            return Refused("Follow: unlock the target and turn off tracking first.");
        if (GameTarget() is not { } target) return Refused("Follow: the game target is no longer available.");
        if (camera.TargetActorId == target.Id) values.ClearTargetActor(camera);
        else if (values.SetTargetActor(camera, target.Actor, target.Id, target.Actor.Name))
            ClearOutside(camera, target.Id);
        else return Refused("Follow: the actor is not drawn yet.");
        return new(true);
    });

    public ValueWriteResult SetTracking(CameraId id, bool value) =>
        Edit(id, c => new(values.SetTracking(c, value)));
    public ValueWriteResult SetTrackingMode(CameraId id, CameraTrackingMode value) =>
        Edit(id, c => new(values.SetTrackingMode(c, value)));

    public ValueWriteResult ToggleTrackedBone(CameraId id, BoneId boneId) => Edit(id, camera =>
    {
        if (TrackingActor(camera) != boneId.Skeleton.Actor)
            return Refused("Track: choose that actor first.");
        if (Resolve(boneId) is not { } bone) return Refused("Track: that bone is no longer available.");
        for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
        {
            if (bindings.GetBoneId(camera.TrackedBones[i]) == boneId)
            {
                camera.TrackedBones.RemoveAt(i);
                return new(true);
            }
            if (bindings.GetBoneId(camera.TrackedBones[i]) is { } other &&
                other.Skeleton.Actor != boneId.Skeleton.Actor)
                return Refused("Track: tracked bones must use one actor.");
        }
        camera.TrackedBones.Add(bone);
        return new(true);
    });

    public ValueWriteResult CenterOnActor(ActorId id)
    {
        if (Resolve(id) is not { } actor) return Refused("Center: that actor is no longer available.");
        if (!spawns.IsVisible(actor)) return Refused("Center: that actor is not visible.");
        var result = values.CenterOnActor(actor);
        return new(result.Success, result.Detail);
    }

    public ValueWriteResult CenterTrackedActor(CameraId id)
    {
        if (Resolve(id) is not { } camera || TrackingActor(camera) is not { } actor ||
            !CanCenter(camera, actor)) return Refused("Center: the tracked actor cannot be framed.");
        return CenterOnActor(actor);
    }

    public ValueWriteResult Recenter(CameraId id, SelectionId? selection) => Edit(id, camera =>
    {
        var reconciled = Reconcile(id);
        if (!reconciled.Success) return reconciled;
        if (camera.TargetActorId is { } target) return CenterOnActor(target);
        if (GameTarget() is { } native) return CenterOnActor(native.Id);
        if (selection?.Bone is { } boneId)
        {
            if (Resolve(boneId) is not { } bone) return Refused("Center: that bone is no longer available.");
            var result = values.CenterOnBone(bone);
            return new(result.Success, result.Detail);
        }
        if (selection?.Actor is { } actorId) return CenterOnActor(actorId);
        foreach (var tracked in camera.TrackedBones)
            if (bindings.GetBoneId(tracked) is { } trackedId && Resolve(trackedId) is { } bone)
            {
                var result = values.CenterOnBone(bone);
                return new(result.Success, result.Detail);
            }
        return Refused("Center: select or track an actor or bone first.");
    });
}
