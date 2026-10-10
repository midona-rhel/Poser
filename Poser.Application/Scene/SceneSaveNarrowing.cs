using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>
/// Narrows a captured document to an entry's keys and leaves it saying only
/// what it can still say. A pruned actor takes every reference to it with it:
/// a gaze or camera target is cleared, an attached light stays where it stood
/// in the world. Each cleared reference is a named note, so an entry that
/// saved with less than its source had says what it left behind.
/// </summary>
internal static class SceneSaveNarrowing
{
    /// <summary>Keeps only the keyed entities. Returns the refusal when
    /// nothing the entry names survived the capture.</summary>
    public static string? Narrow(
        SceneFile scene,
        IReadOnlyCollection<Guid>? onlyKeys,
        ref IReadOnlyDictionary<Guid, ActorId> actorIdentities)
    {
        if (onlyKeys is null)
            return null;

        var keep = new HashSet<Guid>(onlyKeys);
        // Actor entries key by capture key, not logical id — admit the
        // capture keys of every kept logical id.
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
        // A parent that fell out takes its nesting with it.
        if (scene.Groups is { } remaining)
            foreach (var group in remaining)
                if (group.Parent is { } parentKey
                    && !remaining.Any(candidate => candidate.Key == parentKey))
                    group.Parent = null;
        // An entry has no sidebar order of its own: its entities seat where
        // the load lands them.
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

    /// <summary>The entry's name IS the thing's name: Stone rail spawns a
    /// Stone rail. A group entry names the GROUP and its children keep their
    /// own saved names, so the group check leads; otherwise the name lands
    /// only when the document holds exactly one entity.</summary>
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

    /// <summary>Clears every reference to an actor the document no longer
    /// holds — the actor twin of <see cref="SceneParenting.Prune"/> — and
    /// hands the live flag to a kept camera when the live one was left out.
    /// </summary>
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
            // The captured transform is the light's world transform (the
            // attachment copies the bone's onto it each frame), so dropping
            // the attachment leaves the light exactly where it stood.
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

        // A document with cameras names exactly one live camera. When the live
        // one was left out, a kept camera goes live — the default when it was
        // kept — so a single-camera entry loads as the camera it names.
        if (scene.Cameras.Count > 0 && !scene.Cameras.Any(camera => camera.IsLive))
            (scene.Cameras.FirstOrDefault(camera => camera.IsDefault)
                ?? scene.Cameras[0]).IsLive = true;
    }
}
