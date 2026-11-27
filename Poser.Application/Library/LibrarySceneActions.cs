using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Domain.Library;
using Poser.Documents.Files;

namespace Poser.Application.Library;

public interface ILibrarySceneActions
{
    Outcome LoadScene(string path, ObjectPlacementMode placement);
    Outcome SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement);
    Outcome SpawnPose(PoseFile pose, PoseImportOptions options, string description);
}

/// <summary>
/// Loads scenes and saved entries for the Library, file dialogs and spawn browser.
/// Placement uses the current camera or actor when the load starts.
/// </summary>
public sealed class LibrarySceneActions(
    ISceneWorkflow scenes, SceneLoadPreferences preferences, IPlacementAnchorSource anchors,
    ISceneCreation creation, IPendingSceneCreation pending,
    Poser.Application.World.IPlaceService? place = null) : ILibrarySceneActions
{
    public Outcome LoadScene(string path, ObjectPlacementMode placement)
    {
        var options = preferences.ForLoad(path, place?.Current.TerritoryId ?? 0, placement, anchors);
        return scenes.BeginLoad(path, options);
    }

    public Outcome SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement)
    {
        // Entry loads add to the scene. The full-scene clear option stays separate.
        var options = new SceneLoadOptions { ReuseExistingActors = false };
        if (kind == PoseLibraryEntryKind.Overlay)
            options = SceneLoadOptions.Only(SceneCategories.Overlays);
        else if (kind == PoseLibraryEntryKind.Environment)
            return scenes.BeginLoad(path, SceneLoadOptions.Only(SceneCategories.Environment));
        if (anchors.TryCurrentFor(placement, out var position, out var yaw, out var refusal))
            options = options with { Placement = placement, PlacementPosition = position, PlacementYaw = yaw };
        else
            return Outcome.Fail(refusal ?? "The placement anchor is unavailable.");
        return scenes.BeginLoad(path, options);
    }

    /// <summary>Spawns a new actor and imports the pose onto it once the
    /// actor is ready.</summary>
    public Outcome SpawnPose(PoseFile pose, PoseImportOptions options, string description)
    {
        var result = creation.CreateActor(new());
        if (result.Handle is not { } actor)
            return Outcome.Fail(result.Detail ?? "The actor could not be spawned.");
        pending.ApplyPoseWhenReady(actor, pose, options, description);
        return Outcome.Ok();
    }
}

