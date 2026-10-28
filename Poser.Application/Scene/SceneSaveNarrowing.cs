using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>
/// Restricts a capture to selected entity keys, applies its entry name, and
/// clears actor references left dangling by filtering. DetachDangling records
/// each cleared reference and preserves attached lights' world positions.
/// </summary>
internal static class SceneSaveNarrowing
{
    /// <summary>Keeps the selected entities. Returns an error if none were captured.</summary>
    public static string? Narrow(
        SceneFile scene,
        IReadOnlyCollection<Guid>? onlyKeys,
        ref IReadOnlyDictionary<Guid, ActorId> actorIdentities)
    {
        if (onlyKeys is null)
            return null;

        var keep = new HashSet<Guid>(onlyKeys);
        // Translate selected actor IDs to their keys in the captured scene.
        foreach (var pair in actorIdentities)
            if (keep.Contains(pair.Value.LogicalId))
                keep.Add(pair.Key);
        scene.Actors.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.Props.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.Lights.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.Cameras.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.Overlays?.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.WorldObjects?.RemoveAll(entry => !keep.Contains(entry.Key));
        scene.Groups?.RemoveAll(group =>
            !(group.Transform?.Members.Select(member => member.Member) ?? group.Members)
                .All(member => keep.Contains(member.Key)));
        // Make a group top-level when its parent was excluded.
        if (scene.Groups is { } remaining)
            foreach (var group in remaining)
                if (group.Parent is { } parentKey
                    && !remaining.Any(candidate => candidate.Key == parentKey))
                    group.Parent = null;
        // The destination scene determines the entry's sidebar position.
        scene.RootOrder = null;
        if (scene.Actors.Count + scene.Props.Count
            + scene.Lights.Count + scene.Cameras.Count
            + (scene.Overlays?.Count ?? 0)
            + (scene.WorldObjects?.Count ?? 0) == 0)
            return "Nothing the entry names was in the capture; it may have "
                + "just been removed. Nothing was saved.";

        actorIdentities = actorIdentities
            .Where(pair => keep.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return null;
    }

    /// <summary>Names the single saved group when present; otherwise names the
    /// entity only if exactly one remains. Group members retain their names.</summary>
    public static void ApplyEntryName(SceneFile scene, string? entryName)
    {
        if (entryName is not { Length: > 0 })
            return;
        if (scene.Groups is { Count: 1 } groups)
        {
            groups[0].Name = entryName;
            return;
        }
        int count = scene.Actors.Count + scene.Props.Count + scene.Lights.Count
            + scene.Cameras.Count + (scene.Overlays?.Count ?? 0)
            + (scene.WorldObjects?.Count ?? 0);
        if (count != 1)
            return;
        if (scene.WorldObjects is { Count: 1 } objects)
            objects[0].Name = entryName;
        else if (scene.Props.Count == 1)
            scene.Props[0].Name = entryName;
        else if (scene.Lights.Count == 1 && scene.Lights[0].Light is { } light)
            light.Name = entryName;
        else if (scene.Cameras.Count == 1 && scene.Cameras[0].Camera is { } camera)
            camera.Name = entryName;
        else if (scene.Overlays is { Count: 1 } overlays && overlays[0].Node is { } node)
            overlays[0].Node = node with { Name = entryName };
        else if (scene.Actors.Count == 1)
            scene.Actors[0].Name = entryName;
    }

    /// <summary>Clears references to excluded actors and chooses a live camera
    /// from those included in the save.</summary>
    public static void DetachDangling(SceneFile scene, List<string> notes)
    {
        var actorKeys = scene.Actors.Select(actor => actor.Key).ToHashSet();

        foreach (var actor in scene.Actors)
        {
            if (actor.Gaze is not { TargetActorKey: { } target } gaze
                || actorKeys.Contains(target))
                continue;
            gaze.TargetActorKey = null;
            notes.Add($"Actor '{actor.Name}' looks at an actor this save left out; "
                + "it is saved without a gaze target.");
        }

        foreach (var light in scene.Lights)
        {
            if (light.Attachment is not { } attachment
                || actorKeys.Contains(attachment.ActorKey))
                continue;
            // The captured world transform keeps the light in place after detaching.
            light.Attachment = null;
            notes.Add($"Light '{light.Light?.Name}' is attached to an actor this "
                + "save left out; it is saved at its current world placement.");
        }

        foreach (var camera in scene.Cameras)
        {
            if (camera.TargetActorKey is not { } target || actorKeys.Contains(target))
                continue;
            camera.TargetActorKey = null;
            camera.TargetActorName = string.Empty;
            camera.TargetOffset = Vector3.Zero;
            camera.IsTargetLocked = false;
            notes.Add($"Camera '{camera.Camera?.Name}' follows an actor this save "
                + "left out; it is saved without a target.");
        }

        // Choose the default camera, or the first remaining camera, when the
        // live camera was excluded.
        if (scene.Cameras.Count > 0 && !scene.Cameras.Any(camera => camera.IsLive))
            (scene.Cameras.FirstOrDefault(camera => camera.IsDefault)
                ?? scene.Cameras[0]).IsLive = true;
    }
}
