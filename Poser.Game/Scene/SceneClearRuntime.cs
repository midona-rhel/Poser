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
    /// Empties the session of everything it can empty, and NAMES what it
    /// cannot.
    ///
    /// <para>EVERY kind comes out, actors included. An actor Poser spawned
    /// goes through its ownership ledger; one that was already in the GPose
    /// scene goes through the native scene-removal route, which deletes it
    /// from the temporary GPose object table and never touches the overworld
    /// actor. "Clear the session first" means the session, not the part of it
    /// Poser happens to own.</para>
    ///
    /// <para>Props, overlays, lights and cameras each have a bulk verb that
    /// already applies the same ownership rule (a borrowed world light is
    /// released, the default camera cannot be destroyed), so their counts are
    /// read before the sweep.</para>
    ///
    /// <para>Companion bodies are skipped: they leave with their owner. A
    /// removal the native gates refuse — a stale wrapper, the GPose primary,
    /// an actor no longer in the table — is named in the outcome. That is the
    /// exception path now, not the design.</para>
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

        // Everything this session pointed at is gone, so the selection is
        // gone with it. The per-actor deselect above covers a partial clear;
        // this covers the props, overlays, lights, cameras and borrowed
        // objects that have no lineage of their own.
        selection.Clear();

        foreach (var note in refusedCleanup)
            refused.Add(note);

        return new SceneClearOutcome(
            actorCount, propCount, overlayCount, lightCount, cameraCount, worldObjectCount, refused);
    }
}
