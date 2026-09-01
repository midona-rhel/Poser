using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The ONE answer to "where does a placement anchor stand right now" — the
/// camera and the anchor actor, yaw-flattened. Saves record these; placed
/// loads resolve against them. One service so every surface that anchors
/// (lights, cameras, actor entries, scene capture) reads the same facts.
/// </summary>
public sealed class PlacementAnchorSource
{
    private readonly ICameraService _camera;
    private readonly SceneSession _scene;
    private readonly Viewport.ViewportProjection _viewport;

    public PlacementAnchorSource(
        ICameraService camera,
        SceneSession scene,
        Viewport.ViewportProjection viewport)
    {
        _camera = camera;
        _scene = scene;
        _viewport = viewport;
    }

    /// <summary>Where the camera stands right now, yaw-flattened; null when
    /// the camera cannot be read.</summary>
    public PlacementAnchorData? CameraAnchorNow()
    {
        var forward = _camera.GetLookDirection();
        if (forward == Vector3.Zero)
            return null;
        return new PlacementAnchorData
        {
            Position = _camera.GetCameraPosition(),
            Yaw = ObjectPlacement.YawOf(forward),
        };
    }

    /// <summary>Where the anchor actor stands right now, yaw-flattened: the
    /// selected actor, else the scene's first — a save always carries an
    /// actor anchor when any actor exists. Null only in an actorless scene
    /// or when nothing can be read.</summary>
    public PlacementAnchorData? ActorAnchorNow()
    {
        ActorId? anchor = _scene.Selection.Primary is
            { Kind: Poser.Domain.Identity.SceneEntityKind.Actor,
                Actor: { } selected }
            ? selected
            : null;
        if (anchor is null)
        {
            foreach (var descriptor in _scene.Snapshot.Actors)
            {
                anchor = descriptor.Id;
                break;
            }
        }
        if (anchor is not { } actorId)
            return null;
        if (_viewport.GetModelTransform(
                TransformTargetId.ForActor(actorId)) is not { } transform)
            return null;
        return new PlacementAnchorData
        {
            Position = transform.Position,
            Yaw = ObjectPlacement.YawOf(transform.Rotation),
        };
    }

    /// <summary>
    /// The current pose the given mode measures against, or the named
    /// refusal a load posts instead of guessing.
    /// </summary>
    public bool TryCurrentFor(
        ObjectPlacementMode mode,
        out Vector3 position,
        out float yaw,
        out string? refusal)
    {
        position = default;
        yaw = 0f;
        refusal = null;
        switch (mode)
        {
            case ObjectPlacementMode.RelativeToCamera:
                if (CameraAnchorNow() is not { } camera)
                {
                    refusal =
                        "The camera could not be read for relative placement.";
                    return false;
                }
                position = camera.Position;
                yaw = camera.Yaw;
                return true;
            case ObjectPlacementMode.RelativeToSelectedActor:
                if (ActorAnchorNow() is not { } actor)
                {
                    refusal = "No actor is in the scene to place relative to.";
                    return false;
                }
                position = actor.Position;
                yaw = actor.Yaw;
                return true;
            case ObjectPlacementMode.InFrontOfCamera:
                var forward = _camera.GetLookDirection();
                if (forward == Vector3.Zero)
                {
                    refusal =
                        "The camera could not be read for placement.";
                    return false;
                }
                position = _camera.GetCameraPosition() + forward * 3f;
                yaw = ObjectPlacement.YawOf(forward);
                return true;
            default:
                return true;
        }
    }
}
