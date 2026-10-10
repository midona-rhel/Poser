using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Library;
using Poser.Services;
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

/// <summary>Shared library/spawn entry policy, independent of selection and window lifetime.</summary>
public sealed class LibrarySceneActions(
    ISceneWorkflow scenes, SceneLoadPreferences preferences, IPlacementAnchorSource anchors,
    ISceneCreation creation, IPendingSceneCreation pending) : ILibrarySceneActions
{
    public Outcome LoadScene(string path, ObjectPlacementMode placement)
    {
        var options = preferences.Options;
        if (placement != ObjectPlacementMode.AsSaved
            && anchors.TryCurrentFor(placement, out var position, out var yaw, out _))
            options = options with { Placement = placement, PlacementPosition = position, PlacementYaw = yaw };
        return scenes.BeginLoad(path, options);
    }

    public Outcome SpawnEntry(string path, PoseLibraryEntryKind kind, ObjectPlacementMode placement,
        bool fallbackToSaved = false)
    {
        // Entries are additive: a scene's clear-first preference never applies.
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

