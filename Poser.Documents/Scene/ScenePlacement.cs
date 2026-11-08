using System;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using TransformMath = Poser.Domain.Transforms.TransformMath;

namespace Poser.Documents.Scene;

/// <summary>Moves saved scene positions relative to the current player position.
/// Files keep absolute coordinates; placement is applied after reading them.</summary>
public static class SceneRelativePlacement
{
    /// <summary>Moves the scene from its saved origin to the current origin.
    /// Returns an explanation when either origin is unavailable.</summary>
    public static string? Rebase(SceneFile scene, Vector3 currentOrigin)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.Origin is not { } saved)
        {
            return "The scene records no origin, so it cannot be placed " +
                "relative to where you are standing. Load it as saved instead.";
        }
        if (!TransformMath.IsFinite(currentOrigin))
        {
            return "The current position could not be read, so the scene " +
                "cannot be placed relative to it.";
        }

        var offset = currentOrigin - saved;
        if (offset == Vector3.Zero)
            return null;

        // A pure translation: rotations are left exactly as saved.
        ScenePlacementRebase.MoveAll(scene, point => point + offset, turn: null, yawDelta: 0f);
        return null;
    }
}

/// <summary>Moves loaded content between placement anchors, preserving its layout.
/// Relative placement also turns it by the change in the anchor's heading.</summary>
public static class ScenePlacementRebase
{
    internal static void RebaseCompanionPlacement(
        SceneActor actor, Func<Vector3, Vector3> move, Quaternion turn)
    {
        if (actor.CompanionPose?.ModelAbsoluteValues is not { } placement)
            return;
        // The pose codec's legacy unset marker is not a world-space origin.
        if (placement.Position == Vector3.Zero &&
            placement.Rotation == Quaternion.Identity && placement.Scale == Vector3.Zero)
            return;
        placement.Position = move(placement.Position);
        placement.Rotation = Quaternion.Normalize(turn * placement.Rotation);
    }

    public static string? Rebase(
        SceneFile scene,
        Poser.Documents.Files.PlacementAnchorData saved,
        Vector3 currentPosition,
        float currentYaw)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!TransformMath.IsFinite(currentPosition) || !float.IsFinite(currentYaw))
            return "The current anchor could not be read, so the entry " +
                "cannot be placed relative to it.";

        float yawDelta = currentYaw - saved.Yaw;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDelta);
        Vector3 Move(Vector3 point) => currentPosition +
            Vector3.Transform(point - saved.Position, turn);

        MoveAll(scene, Move, turn, yawDelta);
        return null;
    }

    /// <summary>
    /// The one walk over every world-space point a document states, shared by
    /// both rebases. <paramref name="turn"/> null is a pure translation: entity
    /// rotations are left exactly as saved (the companion and group codecs
    /// still take the identity turn, as they always did).
    /// </summary>
    internal static void MoveAll(
        SceneFile scene, Func<Vector3, Vector3> move, Quaternion? turn, float yawDelta)
    {
        Quaternion Turn(Quaternion rotation) =>
            turn is { } by ? Quaternion.Normalize(by * rotation) : rotation;
        var codecTurn = turn ?? Quaternion.Identity;

        foreach (var actor in scene.Actors)
        {
            if (actor.ModelTransform is { } placement)
            {
                placement.Position = move(placement.Position);
                placement.Rotation = Turn(placement.Rotation);
            }
            RebaseCompanionPlacement(actor, move, codecTurn);
            // The gaze's world points are points in THIS scene: an actor
            // looking at a spot on the floor must keep looking at the same spot
            // on the moved floor. They are moved whatever the mode says,
            // because a per-part lock can pin a point while the mode reads
            // Camera or Entity.
            if (actor.Gaze is { } gaze)
            {
                gaze.Position = move(gaze.Position);
                gaze.EyesPosition = move(gaze.EyesPosition);
                gaze.HeadPosition = move(gaze.HeadPosition);
                gaze.BodyPosition = move(gaze.BodyPosition);
            }
        }

        foreach (var prop in scene.Props)
        {
            prop.Transform.Position = move(prop.Transform.Position);
            prop.Transform.Rotation = Turn(prop.Transform.Rotation);
        }

        // Every world object moves: a document only carries spawnable
        // copies (borrowing never persists, ruled 2026-09-01).
        foreach (var worldObject in scene.WorldObjects ?? [])
        {
            worldObject.Transform.Position = move(worldObject.Transform.Position);
            worldObject.Transform.Rotation = Turn(worldObject.Transform.Rotation);
        }

        foreach (var light in scene.Lights)
        {
            // An attached light's transform is stated against its bone, and
            // the bone moved with its actor already.
            if (light.Attachment is not null)
                continue;
            if (light.Light is { } document)
            {
                document.Transform.Position = move(document.Transform.Position);
                document.Transform.Rotation = Turn(document.Transform.Rotation);
            }
        }

        foreach (var camera in scene.Cameras)
        {
            if (camera.Camera is not { } document) continue;
            if (document.OrbitPosition is { } eye) document.OrbitPosition = move(eye);
            if (document.FixedPosition is { } pin) document.FixedPosition = move(pin);
            if (document.Kind == CameraKind.Free) document.Position = move(document.Position);
            if (turn is { } rotation)
            {
                document.PositionOffset = Vector3.Transform(document.PositionOffset, rotation);
                camera.TargetOffset = Vector3.Transform(camera.TargetOffset, rotation);
                // Free cameras store yaw in Rotation.X; orbit cameras use Angle.X.
                if (document.Kind == CameraKind.Free)
                    document.Rotation = document.Rotation with { X = document.Rotation.X + yawDelta };
                else
                    document.Angle = document.Angle with { X = document.Angle.X + yawDelta };
            }
        }

        SceneGroupTransformCodec.Rebase(scene, move, codecTurn);
        foreach (var overlay in scene.Overlays ?? [])
            if (overlay.Node?.Collider is { } collider)
                overlay.Node = overlay.Node with { Collider = collider with
                { Transform = collider.Transform with { Position = move(collider.Transform.Position),
                    Rotation = Turn(collider.Transform.Rotation) } } };
    }
}
