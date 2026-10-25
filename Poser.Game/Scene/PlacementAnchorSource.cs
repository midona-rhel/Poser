using Poser.Domain.Scene;
using System;
using System.Numerics;
using Poser.Application.Scene;
using Poser.Domain.Identity;

using Poser.Application.Viewport;
using Poser.Documents.Files;

namespace Poser.Game.Scene;

/// <summary>
/// Reads the camera or actor position and horizontal heading used for relative
/// placement. Saves record these anchors; loads compare them with current anchors.
/// </summary>
public sealed class PlacementAnchorSource : IPlacementAnchorSource
{
    private readonly ICameraProjection _camera;
    private readonly SceneSession _scene;
    private readonly Viewport.ViewportProjection _viewport;

    public PlacementAnchorSource(
        ICameraProjection camera,
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

    /// <summary>Reads the primary selected actor, or the first actor when the
    /// primary selection is not an actor. Returns null if that actor's transform
    /// cannot be read; it does not try other actors as a fallback.</summary>
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
    /// Resolves the current placement origin and heading for the requested mode,
    /// or returns an explanation when the required camera or actor is unavailable.
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
