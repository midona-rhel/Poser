using System.Numerics;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Documents.Scene;

namespace Poser.Application.Scene;

/// <summary>
/// Adjusts positions in the loaded document before creating game entities.
/// Invalid placement returns a reason without requiring scene rollback.
/// </summary>
internal static class SceneLoadPlacement
{
    /// <summary>Rebases <paramref name="scene"/> in place. <paramref name="origin"/>
    /// is where the user stands now; only a relative-to-origin load reads it.
    /// Returns the refusal, or null when the document is placed.</summary>
    public static string? Apply(
        SceneFile scene, SceneLoadOptions options, Vector3? origin, List<string> notes)
    {
        // Validate both origins before changing the live scene.
        if (options.PlaceRelativeToCurrentOrigin)
        {
            if (origin is not { } anchor)
                return "There is nobody to place the scene relative to, so the " +
                    "load was not started. Load it as saved instead.";
            if (SceneRelativePlacement.Rebase(scene, anchor) is { } refusal)
                return refusal;
            notes.Add("Placed relative to where you are standing.");
        }

        // The caller supplies the current anchor. A missing saved anchor
        // falls back to the content's average position below.
        if (options.Placement == ObjectPlacementMode.AsSaved)
            return null;
        PlacementAnchorData? savedAnchor;
        if (options.Placement == ObjectPlacementMode.InFrontOfCamera)
        {
            // Move the content's average position in front of the camera.
            // Matching anchor yaw values prevents rotation.
            savedAnchor = ContentCentroid(scene) is { } centroid
                ? new PlacementAnchorData
                {
                    Position = centroid,
                    Yaw = options.PlacementYaw,
                }
                : null;
            if (savedAnchor is null)
                notes.Add(
                    "The entry places nothing, so it loaded as "
                    + "saved.");
        }
        else
        {
            savedAnchor = options.Placement == ObjectPlacementMode.RelativeToCamera
                ? scene.CameraAnchor
                : scene.ActorAnchor;
            // Without a saved anchor, center the content on the current
            // camera or actor without rotating it.
            if (savedAnchor is null)
            {
                savedAnchor =
                    ContentCentroid(scene) is { } centre
                        ? new PlacementAnchorData
                        {
                            Position = centre,
                            Yaw = options.PlacementYaw,
                        }
                        : null;
                if (savedAnchor is null)
                    notes.Add(
                        "The entry places nothing, so it loaded as "
                        + "saved.");
                else
                    notes.Add(
                        "No saved anchor: the content's centre "
                        + "lands on the anchor instead.");
            }
        }
        if (savedAnchor is { } saved)
        {
            if (ScenePlacementRebase.Rebase(
                    scene, saved,
                    options.PlacementPosition, options.PlacementYaw)
                is { } placementRefusal)
                return placementRefusal;
            notes.Add(options.Placement switch
            {
                ObjectPlacementMode.RelativeToCamera =>
                    "Placed relative to the camera.",
                ObjectPlacementMode.InFrontOfCamera =>
                    "Placed in front of the camera.",
                _ => "Placed relative to the actor.",
            });
        }
        return null;
    }

    /// <summary>The average position of placed actors, props, unattached lights,
    /// spawned world objects, free cameras, and overlay colliders.
    /// Returns null when none have a placement.</summary>
    public static Vector3? ContentCentroid(SceneFile scene)
    {
        var sum = Vector3.Zero;
        int counted = 0;
        foreach (var actor in scene.Actors)
            if (actor.ModelTransform is { } placement)
            {
                sum += placement.Position;
                counted++;
            }
        foreach (var prop in scene.Props)
        {
            sum += prop.Transform.Position;
            counted++;
        }
        foreach (var light in scene.Lights)
            if (light.Attachment is null && light.Light is { } document)
            {
                sum += document.Transform.Position;
                counted++;
            }
        foreach (var worldObject in scene.WorldObjects ?? [])
            if (worldObject.Spawned)
            {
                sum += worldObject.Transform.Position;
                counted++;
            }
        foreach (var camera in scene.Cameras)
            if (camera.Camera is { Kind: CameraKind.Free } document)
            {
                sum += document.Position;
                counted++;
            }
        foreach (var overlay in scene.Overlays ?? [])
            if (overlay.Node?.Collider is { } collider)
            {
                sum += collider.Transform.Position;
                counted++;
            }
        return counted == 0 ? null : sum / counted;
    }
}
