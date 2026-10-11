using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Cameras;

public sealed class CameraTargetControl(
    IEntityBindings bindings, IVirtualCameraService cameras,
    IActorManager actors, IActorSpawnService spawns, ValueJournal journal, ICameraControl values,
    IEntityHistoryResolver<IVirtualCamera>? history = null) : ICameraTargetControl
{
    private const string Unavailable = "The camera is no longer available.";
    private static Outcome Refused(string detail) => new(false, detail);

    private IVirtualCamera? Current(IVirtualCamera original)
    {
        var current = history is null ? original : history.Resolve(original);
        return current is { IsValid: true } ? current : null;
    }
    private IVirtualCamera? Resolve(CameraId id) =>
        bindings.Resolve(id) is { Success: true, Value: { IsValid: true } camera } ? camera : null;
    private IActor? Resolve(ActorId id) => bindings.Resolve(id).Value;
    private IBone? Resolve(BoneId id) => bindings.Resolve(id).Value;

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

    public Outcome Reconcile(CameraId id)
    {
        if (Resolve(id) is not { } camera) return Refused("The camera is no longer available.");
        bool staleTarget = false;
        if (camera.TargetActorId is { } target)
        {
            if (Resolve(target) is not { } current || !ReferenceEquals(current, camera.TargetActor))
            {
                ClearTargetActor(camera);
                staleTarget = true;
            }
        }
        else if (camera.IsTargetLocked)
            ClearTargetActor(camera);

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

    private Outcome Edit(CameraId id, Func<IVirtualCamera, Outcome> action)
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

    public Outcome Follow(CameraId id, ActorId actorId, string displayName) => Edit(id, camera =>
    {
        if (camera.IsTracking) return Refused("Follow: turn off bone tracking first.");
        if (camera.IsTargetLocked) return Refused("Follow: unlock the actor first.");
        if (Resolve(actorId) is not { } actor) return Refused("Follow: that actor is no longer available.");
        if (!SetTargetActor(camera, actor, actorId, displayName))
            return Refused("Follow: the actor is not drawn yet.");
        ClearOutside(camera, actorId);
        return new(true);
    });

    public Outcome SetTargetLocked(CameraId id, bool value) => Edit(id, camera =>
    {
        if (!value) { ClearTargetActor(camera); return new(true); }
        if (camera.TargetActorId is { } target)
        {
            if (Resolve(target) is { } current && ReferenceEquals(current, camera.TargetActor))
                return values.Set(id, CameraProperties.IsTargetLocked, true);
            ClearTargetActor(camera);
            return Refused("Follow: that actor is no longer available.");
        }
        if (GameTarget() is { } native &&
            SetTargetActor(camera, native.Actor, native.Id, native.Actor.Name))
            return values.Set(id, CameraProperties.IsTargetLocked, true);
        return Refused("Follow: no current actor can be locked.");
    });

    public Outcome ToggleGameTarget(CameraId id) => Edit(id, camera =>
    {
        if (camera.IsTracking || camera.IsTargetLocked)
            return Refused("Follow: unlock the target and turn off tracking first.");
        if (GameTarget() is not { } target) return Refused("Follow: the game target is no longer available.");
        if (camera.TargetActorId == target.Id) ClearTargetActor(camera);
        else if (SetTargetActor(camera, target.Actor, target.Id, target.Actor.Name))
            ClearOutside(camera, target.Id);
        else return Refused("Follow: the actor is not drawn yet.");
        return new(true);
    });

    public Outcome SetTracking(CameraId id, bool value) =>
        Edit(id, _ => values.Set(id, CameraProperties.IsTracking, value));
    public Outcome SetTrackingMode(CameraId id, CameraTrackingMode value) =>
        Edit(id, _ => values.Set(id, CameraProperties.TrackingMode, value));

    public Outcome ToggleTrackedBone(CameraId id, BoneId boneId) => Edit(id, camera =>
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

    public Outcome CenterOnActor(ActorId id)
    {
        if (Resolve(id) is not { } actor) return Refused("Center: that actor is no longer available.");
        if (!spawns.IsVisible(actor)) return Refused("Center: that actor is not visible.");
        return Center(() => cameras.CenterOnActor(actor));
    }

    public Outcome CenterTrackedActor(CameraId id)
    {
        if (Resolve(id) is not { } camera || TrackingActor(camera) is not { } actor ||
            !CanCenter(camera, actor)) return Refused("Center: the tracked actor cannot be framed.");
        return CenterOnActor(actor);
    }

    public Outcome Recenter(CameraId id, SelectionId? selection) => Edit(id, camera =>
    {
        var reconciled = Reconcile(id);
        if (!reconciled.Success) return reconciled;
        if (camera.TargetActorId is { } target) return CenterOnActor(target);
        if (GameTarget() is { } native) return CenterOnActor(native.Id);
        if (selection?.Bone is { } boneId)
        {
            if (Resolve(boneId) is not { } bone) return Refused("Center: that bone is no longer available.");
            return Center(() => cameras.CenterOnBone(bone));
        }
        if (selection?.Actor is { } actorId) return CenterOnActor(actorId);
        foreach (var tracked in camera.TrackedBones)
            if (bindings.GetBoneId(tracked) is { } trackedId && Resolve(trackedId) is { } bone)
                return Center(() => cameras.CenterOnBone(bone));
        return Refused("Center: select or track an actor or bone first.");
    });

    /// <summary>Centres the live camera; a landed centre is one step on that camera.</summary>
    private Outcome Center(Func<Outcome> center)
    {
        var camera = cameras.LiveCamera;
        var before = camera is null ? default : (camera.PositionOffset, camera.Zoom);
        var result = center();
        if (!result.Success || camera is null)
            return result;
        // Scoped to the centred camera (#411); an unbound camera stays global.
        object key = bindings.GetCameraId(camera) is { } id ? SelectionId.ForCamera(id) : cameras;
        journal.Record(key, "Centre camera", before, (camera.PositionOffset, camera.Zoom), next =>
        {
            if (Current(camera) is not { } live) return Refused(Unavailable);
            live.PositionOffset = next.Item1;
            live.Zoom = next.Item2;
            return Outcome.Ok();
        }, () => Current(camera) is not null);
        return result;
    }

    /// <summary>Follows the actor; the step's undo restores the previous
    /// target, or clears it.</summary>
    private bool SetTargetActor(IVirtualCamera camera, IActor actor, ActorId actorId, string displayName)
    {
        if (camera.IsLocked)
            return false;
        var before = camera.TargetActorId;
        if (!cameras.SetTargetActor(camera, actor, actorId, displayName))
            return false;
        // Keys led by the camera service name no entity: these steps stay global.
        journal.Record((cameras, camera), "Follow actor", before, (ActorId?)actorId,
            next => PutTarget(camera, next), () => Current(camera) is not null);
        return true;
    }

    private void ClearTargetActor(IVirtualCamera camera)
    {
        var before = camera.TargetActorId;
        cameras.ClearTargetActor(camera);
        journal.Record((cameras, camera), "Stop following", before, (ActorId?)null,
            next => PutTarget(camera, next), () => Current(camera) is not null);
    }

    private Outcome PutTarget(IVirtualCamera original, ActorId? target) =>
        Current(original) is { } live ? PutTarget(bindings, cameras, live, target) : Refused(Unavailable);

    internal static Outcome PutTarget(IEntityBindings bindings, IVirtualCameraService cameras,
        IVirtualCamera camera, ActorId? target)
    {
        if (target is not { } id)
        {
            cameras.ClearTargetActor(camera);
            return Outcome.Ok();
        }
        // A followed actor that is gone has nothing to restore, as before.
        var resolved = bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { } actor)
            return Outcome.Ok();
        return cameras.SetTargetActor(camera, actor, id, actor.Name)
            ? Outcome.Ok() : Refused("The camera could not follow the actor.");
    }
}
