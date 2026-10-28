using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Domain.Library;
using Poser.Documents.Files;

namespace Poser.Application.Library;

public interface ILibrarySceneActions
{
    Outcome LoadScene(string path, ObjectPlacementMode placement);
    Outcome SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement,
        bool fallbackToSaved = false);
    Outcome SpawnPose(string path, PoseImportOptions options);
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

    public Outcome SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement,
        bool fallbackToSaved = false)
    {
        // Adding a saved entry must not clear the scene, even if the user enabled
        // that option for full-scene loads. Overlays and environment need no anchor.
        var options = new SceneLoadOptions();
        if (kind == PoseLibraryEntryKind.Overlay)
            options = SceneLoadOptions.Only(SceneCategories.Overlays);
        else if (kind == PoseLibraryEntryKind.Environment)
            options = SceneLoadOptions.Only(SceneCategories.Environment);
        else if (anchors.TryCurrentFor(placement, out var position, out var yaw, out var refusal))
            options = options with { Placement = placement, PlacementPosition = position, PlacementYaw = yaw };
        else if (!fallbackToSaved)
            return Outcome.Fail(refusal ?? "The placement anchor is unavailable.");
        return scenes.BeginLoad(path, options);
    }

    public Outcome SpawnPose(string path, PoseImportOptions options)
    {
        var result = creation.CreateActor(new());
        if (result.Handle is not { } actor)
            return Outcome.Fail(result.Detail ?? "The actor could not be spawned.");
        pending.ApplyPoseWhenReady(actor, path, options.Clone());
        return Outcome.Ok();
    }
}

