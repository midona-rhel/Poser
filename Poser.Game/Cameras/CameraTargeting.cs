using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;

namespace Poser.Game.Cameras;

/// <summary>
/// Aims a camera at an actor or bone: the offset a followed actor is drawn at,
/// and the one-shot Center commands. An actor's address is looked up in the
/// game's object table before anything is read from it.
/// </summary>
internal sealed unsafe class CameraTargeting(
    VirtualCameraService owner, IGPoseService gPose, Dalamud.Plugin.Services.IObjectTable objectTable)
{
    public bool SetTargetActor(IVirtualCamera camera, IActor actor, ActorId actorId, string displayName)
    {
        var target = (VirtualCamera)camera;
        if (objectTable.CreateObjectReference(actor.Address) is not { } resolved || !resolved.IsValid())
            return false;
        var drawObject = ((GameObject*)resolved.Address)->DrawObject;
        if (drawObject == null)
            return false;
        target.TargetOffset = (Vector3)drawObject->Object.Position - resolved.Position;
        target.TargetActorName = displayName;
        target.TargetActor = actor;
        target.TargetActorId = actorId;
        return true;
    }

    public void ClearTargetActor(IVirtualCamera camera)
    {
        var target = (VirtualCamera)camera;
        target.TargetOffset = Vector3.Zero;
        target.TargetActorName = string.Empty;
        target.TargetActor = null;
        target.TargetActorId = null;
        target.IsTargetLocked = false;
    }

    /// <summary>Centers the live orbit camera on the middle of the actor's body,
    /// keeping its angle and zoom.</summary>
    public Outcome CenterOnActor(IActor actor)
    {
        if (Refusal(actor, out var gameObject, out var camera, out var lookAt) is { } refusal)
            return Outcome.Fail(refusal);
        var drawOrigin = (Vector3)gameObject->DrawObject->Object.Position;
        // PositionOffset moves both the eye and the point looked at, so the
        // camera keeps its angle and distance.
        camera.PositionOffset += drawOrigin + Vector3.UnitY * (BodyHeight(gameObject) * 0.5f) - lookAt;
        return Outcome.Ok();
    }

    /// <summary>Centers the live orbit camera on the bone and zooms to a close
    /// framing of it.</summary>
    public Outcome CenterOnBone(IBone bone)
    {
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            return Outcome.Fail("Center: that bone is no longer available.");
        if (Refusal(skeleton.Actor, out var gameObject, out var camera, out var lookAt) is { } refusal)
            return Outcome.Fail(refusal);
        skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
        if (BoneWorld.Of(bone) is not { } world)
            return Outcome.Fail("Center: that bone has no position yet.");
        // A bone is a point rather than a body: a quarter of the body height
        // frames it closely.
        float framingHeight = Math.Clamp(BodyHeight(gameObject) * 0.25f, 0.5f, 1.5f);
        var zoomLimits = camera.ZoomLimits;
        camera.PositionOffset += world.Position - lookAt;
        camera.Zoom = Math.Clamp(framingHeight * 2f, zoomLimits.X, zoomLimits.Y);
        return Outcome.Ok();
    }

    /// <summary>Why the camera can't be centred on this actor now, or null.
    /// Everything is checked before the camera changes.</summary>
    private string? Refusal(IActor actor, out GameObject* gameObject, out VirtualCamera camera, out Vector3 lookAt)
    {
        gameObject = null;
        camera = null!;
        lookAt = default;
        if (objectTable.CreateObjectReference(actor.Address) is not { } resolved || !resolved.IsValid())
            return "Center: that actor is no longer available.";
        gameObject = (GameObject*)resolved.Address;
        if (!gameObject->IsReadyToDraw() || gameObject->DrawObject == null || !gameObject->DrawObject->IsVisible)
            return "Center: that actor is not drawn yet.";
        var native = owner.Native;
        if (!owner.IsAvailable || !gPose.IsGPosing || native == null || owner.Live is not { IsLive: true } live)
            return "Center: the game camera is not ready.";
        camera = live;
        if (camera.Kind == CameraKind.Free)
            return "Center: switch from the free camera first.";
        if (camera.IsLocked)
            return "Center: unlock the camera first.";
        if (camera.FixedPosition != null)
            return "Center: clear the camera position pin first.";
        // The interface draws after the camera update, so this already
        // includes the camera's current offsets.
        lookAt = native->Camera.CameraBase.SceneCamera.LookAtVector;
        return null;
    }

    /// <summary>The actor's height as the game frames it. Objects that are not
    /// characters can report zero; they count as an average person.</summary>
    private static float BodyHeight(GameObject* gameObject)
    {
        float reported = MathF.Abs(gameObject->CameraOffset.Y);
        return reported is >= 0.5f and <= 5f ? reported : 1.7f;
    }
}
