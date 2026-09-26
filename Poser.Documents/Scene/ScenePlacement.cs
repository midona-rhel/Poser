using System;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Files;

namespace Poser.Scene;

/// <summary>
/// The relative-placement rebase: one pure pass over a READ document that
/// moves every world position it states by a single offset.
///
/// <para>It runs on the deserialized document before any native work, which is
/// why the wire format stays absolute — the alternative (Ktisis's, which writes
/// positions already relative to a saved origin) makes every number in the file
/// meaningless without the origin beside it, and Poser's listings, diffs and
/// codecs all read the file without one.</para>
///
/// <para>What moves is everything whose position is a point IN the scene:
/// actor placements, prop transforms, world-space lights, free-camera
/// positions, and the gaze world points an actor is looking at. What does NOT
/// move is anything already expressed relative to something that moved with
/// the scene — a bone-attached light (its position is the bone's), an orbit
/// camera (it orbits its target), and a camera's target offset.</para>
///
/// <para>A BORROWED MAP OBJECT also does not move, and for a different reason
/// than either: it is not Poser's to place. Its identity IS the point the map
/// stands it at, so rebasing it would match the fixture at its real spot and
/// then shove it by an arbitrary offset — a pillar hanging over a field. A
/// relative load therefore leaves borrowed objects where the map has them and
/// says so, rather than dragging the zone's own furniture along.</para>
/// </summary>
public static class SceneRelativePlacement
{
    /// <summary>
    /// Rebases <paramref name="scene"/> onto <paramref name="currentOrigin"/>
    /// in place. Returns null when it landed, else the named refusal — the
    /// document states no origin, so there is nothing to rebase FROM.
    /// </summary>
    public static string? Rebase(SceneFile scene, Vector3 currentOrigin)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.Origin is not { } saved)
        {
            return "The scene records no origin, so it cannot be placed " +
                "relative to where you are standing. Load it as saved instead.";
        }
        if (!float.IsFinite(currentOrigin.X) ||
            !float.IsFinite(currentOrigin.Y) ||
            !float.IsFinite(currentOrigin.Z))
        {
            return "The current position could not be read, so the scene " +
                "cannot be placed relative to it.";
        }

        var offset = currentOrigin - saved;
        if (offset == Vector3.Zero)
            return null;

        foreach (var actor in scene.Actors)
        {
            if (actor.ModelTransform is { } placement)
                placement.Position += offset;
            ScenePlacementRebase.RebaseCompanionPlacement(actor, point => point + offset, Quaternion.Identity);
            // The gaze's world points are points in THIS scene: an actor
            // looking at a spot on the floor must keep looking at the same spot
            // on the moved floor. They are moved whatever the mode says,
            // because a per-part lock can pin a point while the mode reads
            // Camera or Entity.
            if (actor.Gaze is { } gaze)
            {
                gaze.Position += offset;
                gaze.EyesPosition += offset;
                gaze.HeadPosition += offset;
                gaze.BodyPosition += offset;
            }
        }

        foreach (var prop in scene.Props)
            prop.Transform.Position += offset;

        // Every world object moves: a document only carries spawnable
        // copies (borrowing never persists, ruled 2026-09-01).
        foreach (var worldObject in scene.WorldObjects ?? [])
            worldObject.Transform.Position += offset;

        foreach (var light in scene.Lights)
        {
            // An attached light's transform is stated against its bone, and
            // the bone moved with its actor already.
            if (light.Attachment is not null)
                continue;
            if (light.Light is { } document)
                document.Transform.Position += offset;
        }

        foreach (var camera in scene.Cameras)
        {
            // Only a free camera states a world position; an orbit camera is
            // angle and zoom about a target that moved with the scene, and its
            // TargetOffset is relative to that target either way.
            if (camera.Camera is { Kind: CameraKind.Free } document)
                document.Position += offset;
        }

        SceneGroupTransformCodec.Rebase(scene, point => point + offset, Quaternion.Identity);
        foreach (var overlay in scene.Overlays ?? [])
            if (overlay.Node?.Collider is { } collider)
                overlay.Node = overlay.Node with { Collider = collider with
                { Transform = collider.Transform with { Position = collider.Transform.Position + offset } } };
        SceneFabrikChain.Rebase(scene, point => point + offset, Quaternion.Identity);
        return null;
    }
}

/// <summary>
/// The object-entry placement rebase: the origin rebase's sibling, with the
/// turn the origin rebase deliberately lacks. Everything whose position is a
/// point IN the document moves to the current anchor and turns by the yaw
/// difference; rotations turn with it, keeping their own pitch and roll.
/// Borrowed map objects do not move, for the origin rebase's own reason.
/// </summary>
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
        Poser.Files.PlacementAnchorData saved,
        Vector3 currentPosition,
        float currentYaw)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(currentPosition.X) ||
            !float.IsFinite(currentPosition.Y) ||
            !float.IsFinite(currentPosition.Z) ||
            !float.IsFinite(currentYaw))
            return "The current anchor could not be read, so the entry " +
                "cannot be placed relative to it.";

        float yawDelta = currentYaw - saved.Yaw;
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, yawDelta);
        Vector3 Move(Vector3 point) => currentPosition +
            Vector3.Transform(point - saved.Position, turn);

        foreach (var actor in scene.Actors)
        {
            if (actor.ModelTransform is { } placement)
            {
                placement.Position = Move(placement.Position);
                placement.Rotation = System.Numerics.Quaternion.Normalize(
                    turn * placement.Rotation);
            }
            RebaseCompanionPlacement(actor, Move, turn);
            if (actor.Gaze is { } gaze)
            {
                gaze.Position = Move(gaze.Position);
                gaze.EyesPosition = Move(gaze.EyesPosition);
                gaze.HeadPosition = Move(gaze.HeadPosition);
                gaze.BodyPosition = Move(gaze.BodyPosition);
            }
        }
        foreach (var prop in scene.Props)
        {
            prop.Transform.Position = Move(prop.Transform.Position);
            prop.Transform.Rotation = System.Numerics.Quaternion.Normalize(
                turn * prop.Transform.Rotation);
        }
        // Every world object moves: a document only carries spawnable
        // copies (borrowing never persists, ruled 2026-09-01).
        foreach (var worldObject in scene.WorldObjects ?? [])
        {
            worldObject.Transform.Position =
                Move(worldObject.Transform.Position);
            worldObject.Transform.Rotation =
                System.Numerics.Quaternion.Normalize(
                    turn * worldObject.Transform.Rotation);
        }
        foreach (var light in scene.Lights)
        {
            if (light.Attachment is not null)
                continue;
            if (light.Light is { } document)
            {
                document.Transform.Position =
                    Move(document.Transform.Position);
                document.Transform.Rotation =
                    System.Numerics.Quaternion.Normalize(
                        turn * document.Transform.Rotation);
            }
        }
        foreach (var camera in scene.Cameras)
        {
            if (camera.Camera is { Kind: CameraKind.Free } document)
            {
                document.Position = Move(document.Position);
                document.Angle = document.Angle with
                {
                    X = document.Angle.X + yawDelta,
                };
            }
        }

        SceneGroupTransformCodec.Rebase(scene, Move, turn);
        foreach (var overlay in scene.Overlays ?? [])
            if (overlay.Node?.Collider is { } collider)
                overlay.Node = overlay.Node with { Collider = collider with
                { Transform = collider.Transform with { Position = Move(collider.Transform.Position),
                    Rotation = Quaternion.Normalize(turn * collider.Transform.Rotation) } } };
        SceneFabrikChain.Rebase(scene, Move, turn);
        return null;
    }
}
