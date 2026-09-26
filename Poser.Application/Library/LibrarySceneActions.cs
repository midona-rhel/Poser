using Poser.Application.Scene;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Library;
using Poser.Services;

namespace Poser.Application.Library;

public interface ILibrarySceneActions
{
    SceneActionResult LoadScene(string path, ObjectPlacementMode placement);
    SceneActionResult SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement,
        bool fallbackToSaved = false);
    SceneActionResult SpawnPose(string path, PoseImportOptions options);
}

/// <summary>Shared library/spawn entry policy, independent of selection and window lifetime.</summary>
public sealed class LibrarySceneActions(
    ISceneWorkflow scenes, SceneLoadPreferences preferences, IPlacementAnchorSource anchors,
    ISceneCreation creation, IPendingSceneCreation pending) : ILibrarySceneActions
{
    public SceneActionResult LoadScene(string path, ObjectPlacementMode placement)
    {
        var options = preferences.Options;
        if (placement != ObjectPlacementMode.AsSaved
            && anchors.TryCurrentFor(placement, out var position, out var yaw, out _))
            options = options with { Placement = placement, PlacementPosition = position, PlacementYaw = yaw };
        return scenes.BeginLoad(path, options);
    }

    public SceneActionResult SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement,
        bool fallbackToSaved = false)
    {
        // Entries are additive: a scene's clear-first preference never applies.
        var options = new SceneLoadOptions();
        if (kind == PoseLibraryEntryKind.Overlay)
            options = options with
            {
                IncludeActors = false, IncludeProps = false, IncludeLights = false,
                IncludeCameras = false, IncludeEnvironment = false,
            };
        else if (kind == PoseLibraryEntryKind.Environment)
            options = options with
            {
                IncludeActors = false, IncludeProps = false, IncludeLights = false,
                IncludeCameras = false, IncludeOverlays = false,
            };
        else if (anchors.TryCurrentFor(placement, out var position, out var yaw, out var refusal))
            options = options with { Placement = placement, PlacementPosition = position, PlacementYaw = yaw };
        else if (!fallbackToSaved)
            return SceneActionResult.Fail(refusal ?? "The placement anchor is unavailable.");
        return scenes.BeginLoad(path, options);
    }

    public SceneActionResult SpawnPose(string path, PoseImportOptions options)
    {
        var result = creation.CreateActor(new());
        if (result.Handle is not { } actor)
            return SceneActionResult.Fail(result.Detail ?? "The actor could not be spawned.");
        pending.ApplyPoseWhenReady(actor, path, options.Clone());
        return SceneActionResult.Ok();
    }
}

