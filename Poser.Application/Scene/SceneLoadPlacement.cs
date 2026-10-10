using System.Numerics;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Scene;

namespace Poser.Application.Scene;

/// <summary>
/// Moves a READ document to where the load asked it to land, before one
/// native call: a refusal here costs nothing, because nothing has happened
/// and there is nothing to roll back. Every placement it makes or declines
/// is stated as a note.
/// </summary>
internal static class SceneLoadPlacement
{
    /// <summary>Rebases <paramref name="scene"/> in place. <paramref name="origin"/>
    /// is where the user stands now; only a relative-to-origin load reads it.
    /// Returns the refusal, or null when the document is placed.</summary>
    public static string? Apply(
        SceneFile scene, SceneLoadOptions options, Vector3? origin, List<string> notes)
    {
        // Relative placement rebases the READ document, before one native
        // call: a file with no origin refuses HERE, where nothing has
        // happened and there is nothing to roll back.
        if (options.PlaceRelativeToCurrentOrigin)
        {
            if (origin is not { } anchor)
                return "There is nobody to place the scene relative to, so the " +
                    "load was not started. Load it as saved instead.";
            if (SceneRelativePlacement.Rebase(scene, anchor) is { } refusal)
                return refusal;
            notes.Add("Placed relative to where you are standing.");
        }

        // The object-entry placement: the caller resolved the CURRENT
        // anchor; the document carries the SAVED one. A mode whose saved
        // anchor the file does not record refuses before anything is
        // touched.
        if (options.Placement == ObjectPlacementMode.AsSaved)
            return null;
        PlacementAnchorData? savedAnchor;
        if (options.Placement == ObjectPlacementMode.InFrontOfCamera)
        {
            // The anchor is the content ITSELF: its centroid moves
            // to the point in front of the camera, no turn — the
            // light spawn's behavior, generalized. An entry that
            // places nothing simply loads as saved.
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
            // No saved anchor is no longer a refusal (ruled
            // 2026-08-31): the content's CENTROID stands in, so
            // the content lands ON the current camera or actor —
            // no turn — instead of keeping an offset the entry
            // never recorded.
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

    /// <summary>The average position of everything the document PLACES —
    /// actors, props, unattached lights, spawned world objects, free
    /// cameras. Null when it places nothing.</summary>
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
