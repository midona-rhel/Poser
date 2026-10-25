using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using System;
using Poser.Domain.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Actors;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The destroy-first clear a clear-first load runs before it restores
/// anything.
/// </summary>
internal sealed class SceneClearRuntime(
    IActorManager actors,
    StableBindingRegistry bindings,
    GazeService gaze,
    Poser.Application.Integration.IntegrationReset integration,
    IActorSpawnService spawns,
    PropSpawnService props,
    Poser.Game.Overlays.OverlayNodeService overlays,
    ILightingService lighting,
    IVirtualCameraService cameras,
    WorldObjects.WorldService worldObjects,
    Poser.Application.Selection.SelectionSession selection)
{
    /// <summary>
    /// Removes session entities and reports refused actor removals or cleanup.
    /// Companions leave with their owners. Borrowed map objects are released,
    /// and the default camera remains. Selection is cleared even after a partial clear.
    /// </summary>
    public SceneClearOutcome ClearScene()
    {
        int actorCount = 0;
        var refused = new List<string>();
        var refusedCleanup = new List<string>();
        foreach (var actor in actors.Actors.ToList())
        {
            // A companion body goes with its owner, and removing an owner
            // earlier in this sweep can retire later wrappers in the
            // snapshot: neither is a refusal worth naming.
            if (actor.ActorKind is ActorKind.Companion or ActorKind.Mount or ActorKind.Ornament
                || actor.Address == nint.Zero || !actors.Actors.Contains(actor))
                continue;
            // Gaze and appearance are released BEFORE the delete, while the
            // actor still exists to release them against; Brio does the same
            // in CleanObject (Brio/Game/Actor/ActorSpawnService.cs:245-256)
            // and for the same reason — after the delete there is nothing left
            // to name.
            var lineage = bindings.GetActorId(actor)?.LogicalId;
            ActorRemovalCleanup.Prepare(actor, gaze, integration, bindings, refusedCleanup.Add);

            // One verb for both provenances: the service routes an owned
            // actor to its ledger and an adopted one to the scene table.
            if (spawns.RemoveActorFromScene(actor))
            {
                actorCount++;
                // Deselect the moment it is gone, per actor, rather than only
                // at the end: a removal that succeeds for some actors and is
                // refused for others must not leave the successful ones
                // selected.
                if (lineage is { } gone)
                    selection.RemoveActorLineage(gone);
            }
            else
            {
                // The row says why, in the gate's own words, not just who.
                refused.Add(spawns.RemovalRefusal(actor) is { } why
                    ? $"{actor.Name}: {why}"
                    : actor.Name);
            }
        }

        int propCount = props.Props.Count;
        props.DestroyAll();

        int overlayCount = overlays.Nodes.Count;
        overlays.DestroyAll();

        int lightCount = lighting.Lights.Count(lighting.IsSpawnedLight);
        lighting.DestroyAllLights();

        int cameraCount = cameras.Cameras.Count(camera => !camera.IsDefault);
        cameras.DestroyAllCameras();

        // Not a destruction: releasing writes each borrowed object's captured
        // placement and flags back to the map. Clearing the scene is one of the
        // four exits the restore contract names, and this is where it runs.
        int worldObjectCount = worldObjects.Count;
        worldObjects.ReleaseAll();

        // Clear selection for all entity kinds, including actors whose removal
        // was refused. Per-actor deselection above takes effect during removal.
        selection.Clear();

        foreach (var note in refusedCleanup)
            refused.Add(note);

        return new SceneClearOutcome(
            actorCount, propCount, overlayCount, lightCount, cameraCount, worldObjectCount, refused);
    }
}
