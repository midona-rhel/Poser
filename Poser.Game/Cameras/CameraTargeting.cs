using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;

namespace Poser.Game.Cameras;

/// <summary>
/// Aims a camera at an actor or bone: the follow target's draw offset, and
/// the one-shot centering commands. Every actor address is resolved through
/// the object table before it is dereferenced — a raw address is a claim,
/// not a proof (WorldActorDiscovery standard).
/// </summary>
internal sealed unsafe class CameraTargeting
{
    private readonly VirtualCameraService _owner;
    private readonly IGPoseService _gPose;
    // Null only in the test ctor (no native actors exist there).
    private readonly Dalamud.Plugin.Services.IObjectTable? _objectTable;

    public CameraTargeting(
        VirtualCameraService owner,
        IGPoseService gPose,
        Dalamud.Plugin.Services.IObjectTable? objectTable)
    {
        _owner = owner;
        _gPose = gPose;
        _objectTable = objectTable;
    }

    private bool IsAvailable => _owner.IsAvailable;

    private NativeCamera* Native => _owner.Native;

    private VirtualCamera? Live => _owner.Live;

    public bool SetTargetActor(
        IVirtualCamera camera, IActor actor, ActorId actorId,
        string displayName)
    {
        if (camera is not VirtualCamera target || actor.Address == nint.Zero)
            return false;
        // Deref-time revalidation: the stored actor address is only a claim;
        // the deref goes through the object-table-resolved wrapper.
        var resolved = _objectTable?.CreateObjectReference(actor.Address);
        if (resolved == null || !resolved.IsValid())
            return false;
        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
            return false;
        Vector3 drawPosition = drawObject->Object.Position;
        Vector3 objectPosition = resolved.Position;
        target.TargetOffset = drawPosition - objectPosition;
        target.TargetActorName = displayName;
        target.TargetActor = actor;
        target.TargetActorId = actorId;
        return true;
    }

    public void ClearTargetActor(IVirtualCamera camera)
    {
        if (camera is not VirtualCamera target)
            return;
        target.TargetOffset = Vector3.Zero;
        target.TargetActorName = string.Empty;
        target.TargetActor = null;
        target.TargetActorId = null;
        target.IsTargetLocked = false;
    }

    /// <summary>Centers the current live orbit camera on the actor's drawn
    /// mid-body pivot while retaining its orientation and zoom. Validation completes
    /// before the first camera setter: stale, hidden, or undrawn actors leave
    /// the shot untouched.</summary>
    public Outcome CenterOnActor(IActor actor)
    {
        if (!IsAvailable)
            return Outcome.Fail("Center: the camera is unavailable.");
        if (actor.Address == nint.Zero ||
            _objectTable?.CreateObjectReference(actor.Address) is not { } resolved ||
            !resolved.IsValid())
            return Outcome.Fail("Center: that actor is no longer available.");

        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (!gameObject->IsReadyToDraw() ||
            drawObject == null || !drawObject->IsVisible)
            return Outcome.Fail("Center: that actor is not drawn yet.");

        var native = Native;
        if (!_gPose.IsGPosing || native == null ||
            Live is not { IsLive: true } camera)
            return Outcome.Fail("Center: the game camera is not ready.");
        if (camera.Kind == CameraKind.Free)
            return Outcome.Fail("Center: switch from the free camera first.");
        if (camera.IsLocked)
            return Outcome.Fail("Center: unlock the camera first.");
        if (camera.FixedPosition != null)
            return Outcome.Fail("Center: clear the camera position pin first.");

        Vector3 drawOrigin = drawObject->Object.Position;
        float reportedHeight = MathF.Abs(gameObject->CameraOffset.Y);
        // CameraOffset is the client's character-aware framing measure. Some
        // non-character draw objects report zero, so use a conservative human
        // height rather than aiming at their feet.
        float height = reportedHeight is >= 0.5f and <= 5f
            ? reportedHeight
            : 1.7f;
        Vector3 pivot = drawOrigin + Vector3.UnitY * (height * 0.5f);
        var scene = &native->Camera.CameraBase.SceneCamera;
        Vector3 baseLookAt = scene->LookAtVector;
        // The UI runs after the camera-update detour, so LookAtVector already
        // includes the current position/target offsets. Add only the delta
        // from that effective pivot; TargetOffset stays untouched and the
        // existing follow relationship remains exactly as it was.
        return TranslateOrbitPivot(camera, pivot, baseLookAt);
    }

    internal static Outcome TranslateOrbitPivot(
        IVirtualCamera camera, Vector3 pivot, Vector3 currentPivot)
    {
        var offset = camera.PositionOffset + (pivot - currentPivot);
        if (!TransformMath.IsFinite(pivot) || !TransformMath.IsFinite(currentPivot)
            || !TransformMath.IsFinite(offset))
            return Outcome.Fail("Center: no usable actor or camera pivot.");
        // PositionOffset translates both the eye and look-at in the camera
        // detour. Do not refit distance/FOV: this command moves the current shot.
        camera.PositionOffset = offset;
        return Outcome.Ok();
    }

    /// <summary>Centers on the selected bone's live model-space transform.
    /// The skeleton cache and object-table draw checks happen before either
    /// camera setter so a replaced or undrawn identity cannot move the shot.
    /// </summary>
    public Outcome CenterOnBone(IBone bone)
    {
        if (!IsAvailable)
            return Outcome.Fail("Center: the camera is unavailable.");
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            return Outcome.Fail("Center: that bone is no longer available.");

        var actor = skeleton.Actor;
        if (actor.Address == nint.Zero ||
            _objectTable?.CreateObjectReference(actor.Address) is not { } resolved ||
            !resolved.IsValid())
            return Outcome.Fail("Center: that actor is no longer available.");
        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (!gameObject->IsReadyToDraw() || drawObject == null ||
            !drawObject->IsVisible)
            return Outcome.Fail("Center: that bone is not drawn yet.");

        var native = Native;
        if (!_gPose.IsGPosing || native == null ||
            Live is not { IsLive: true } camera)
            return Outcome.Fail("Center: the game camera is not ready.");
        if (camera.Kind == CameraKind.Free)
            return Outcome.Fail("Center: switch from the free camera first.");
        if (camera.IsLocked)
            return Outcome.Fail("Center: unlock the camera first.");
        if (camera.FixedPosition != null)
            return Outcome.Fail("Center: clear the camera position pin first.");

        skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
        if (BoneWorld.Of(bone) is not { } world)
            return Outcome.Fail("Center: no usable bone or camera pivot.");
        float reportedHeight = MathF.Abs(gameObject->CameraOffset.Y);
        float actorHeight = reportedHeight is >= 0.5f and <= 5f
            ? reportedHeight
            : 1.7f;
        // A bone is a point rather than a body; a quarter body height gives a
        // useful Ktisis-like close framing without changing the view angles.
        float framingHeight = Math.Clamp(actorHeight * 0.25f, 0.5f, 1.5f);
        var pivot = world.Position;
        var scene = &native->Camera.CameraBase.SceneCamera;
        Vector3 baseLookAt = scene->LookAtVector;
        Vector2 zoomLimits = camera.ZoomLimits;
        if (!TransformMath.IsFinite(pivot) || !TransformMath.IsFinite(baseLookAt) ||
            !float.IsFinite(zoomLimits.X) || !float.IsFinite(zoomLimits.Y) ||
            zoomLimits.X > zoomLimits.Y)
            return Outcome.Fail("Center: no usable bone or camera pivot.");

        camera.PositionOffset += pivot - baseLookAt;
        camera.Zoom = Math.Clamp(framingHeight * 2f, zoomLimits.X, zoomLimits.Y);
        return Outcome.Ok();
    }
}
