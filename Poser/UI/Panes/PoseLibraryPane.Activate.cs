using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Posing;
using Poser.Game.Preview;
using Poser.Game.Scene;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Activating a tile: apply, spawn, load a scene, apply a character file.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>
    /// An object tile's one action, by what the file is. An actor entry
    /// SPAWNS its actor — through the same scene workflow a scene load uses,
    /// with fresh additive options so a clear-first preference set for
    /// scenes can never fire from a library tile. Lights and cameras import
    /// through their own services, which spawn a new light and create a new
    /// camera respectively.
    /// </summary>
    private void ActivateObject(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count ||
            index >= _tileKinds.Count)
            return;
        var path = _vm.Tiles[index].ThumbKey;
        var name = _vm.Tiles[index].Label;
        switch (_tileKinds[index])
        {
            // ONE pipeline: every container entry — actor, group, object,
            // light, camera — spawns through the same placement-anchored
            // load.
            case PoseLibraryEntryKind.Actor:
            case PoseLibraryEntryKind.Group:
            case PoseLibraryEntryKind.WorldObject:
            case PoseLibraryEntryKind.Prop:
            case PoseLibraryEntryKind.Light:
            case PoseLibraryEntryKind.Camera:
                var actorMode = EffectiveMode();
                if (!_anchors.TryCurrentFor(
                        actorMode, out var anchorPosition,
                        out var anchorYaw, out var anchorRefusal))
                {
                    _notices.Refused(anchorRefusal!);
                    break;
                }
                var started = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    Placement = actorMode,
                    PlacementPosition = anchorPosition,
                    PlacementYaw = anchorYaw,
                });
                if (!started.Success)
                    _notices.Failed(
                        started.Detail ?? "The actor could not be spawned.");
                break;
            case PoseLibraryEntryKind.Overlay:
                // Screen-space: placement modes do not apply; the stored
                // centre-relative position re-attaches at the current
                // centre inside the load.
                var overlayLoad = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    IncludeActors = false,
                    IncludeProps = false,
                    IncludeLights = false,
                    IncludeCameras = false,
                    IncludeEnvironment = false,
                });
                if (!overlayLoad.Success)
                    _notices.Failed(
                        overlayLoad.Detail ??
                        "The overlay could not be staged.");
                break;
            case PoseLibraryEntryKind.Environment:
                // The load applies only what the file states; an environment
                // entry states nothing but the environment.
                var applied = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    IncludeActors = false,
                    IncludeProps = false,
                    IncludeLights = false,
                    IncludeCameras = false,
                    IncludeOverlays = false,
                });
                if (!applied.Success)
                    _notices.Failed(
                        applied.Detail ??
                        "The environment could not be applied.");
                break;
        }
    }

    /// <summary>Restores a highlighted scene through the ONE scene workflow —
    /// the same single-flight transaction the scene workspace starts, so a
    /// refusal reads the same on either surface.</summary>
    private void LoadScene(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        // Scenes obey the placement rule like every entry (ruled
        // 2026-08-31): the standing load options, plus wherever the
        // footer's choice puts the content.
        var sceneLoad = _sceneOptions.Options;
        var sceneMode = EffectiveMode();
        if (sceneMode != ObjectPlacementMode.AsSaved
            && _anchors.TryCurrentFor(
                sceneMode, out var scenePoint, out var sceneYaw, out _))
            sceneLoad = sceneLoad with
            {
                Placement = sceneMode,
                PlacementPosition = scenePoint,
                PlacementYaw = sceneYaw,
            };
        var started = _scenes.BeginLoad(
            _vm.Tiles[index].ThumbKey, sceneLoad);
        if (!started.Success)
            _notices.Failed(
                started.Detail ?? "The scene could not be loaded.");
    }

    private void Apply(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        double now = ImGui.GetTime();
        if (index == _lastAppliedTile && now - _lastAppliedAt < ReactivationSwallow)
            return;
        _lastAppliedTile = index;
        _lastAppliedAt = now;

        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index);
            return;
        }

        if (TargetActor() is not { HasSkeleton: true } actor)
        {
            _notices.Refused("Select an actor to apply a pose to.");
            return;
        }
        ApplyTo(index, actor);
    }

    /// <summary>The one apply: a tile onto an EXPLICIT actor — the picker's
    /// choice or the double-click path's selection target.</summary>
    private void ApplyTo(int index, IActor actor)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index);
            return;
        }
        if (!actor.HasSkeleton)
        {
            _notices.Refused("That actor has no skeleton to pose.");
            return;
        }
        var path = _vm.Tiles[index].ThumbKey;
        // Brio's expression-only .cmp gate: reported, and NOT imported.
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }
        // The target's stance is about to change, so the preview's rebase
        // baseline is stale from this call on — the NEXT tile has to be shown
        // landing on this one, not on what stood before it.
        _previewBinder.InvalidateBaseline();
        if (_bindings.GetActorId(actor) is not { } expectedActor)
        {
            _notices.Failed("Apply: the actor could not be resolved.");
            return;
        }
        var result = _poseFacade.ImportPose(
            actor,
            path,
            BuildImportOptions(path),
            onReceipt: TrackImport(expectedActor));
        if (!result.Success)
            _notices.Failed(Failure(result));
        else if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    /// <summary>
    /// The MCDF apply: the SAME call the appearance pane's Import… dialog
    /// makes (<c>AppearancePane.OpenMcdfImport</c>), so a character file picked
    /// here travels the identical mods/appearance/body-scale pipeline. The
    /// session reports progress and every failure on its own surface; the
    /// notification only carries a refusal to start.
    /// </summary>
    private void ApplyCharacterFile(int index)
    {
        if (TargetActor() is not { } actor
            || _bindings.GetActorId(actor) is not { } id)
        {
            _notices.Refused("Select an actor to apply a character file to.");
            return;
        }
        string path = _vm.Tiles[index].ThumbKey;
        var begun = _disruptive.Run(id, "Import character file",
            () => _integration.BeginImport(id, path),
            () => _integration.ResetMcdf(id), asset: path);
        if (!begun.Success)
            _notices.Failed("Import", begun.Detail);
    }

    private void Spawn(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count || _type == LibraryType.Mcdf)
            return;

        // The .cmp verdict is taken BEFORE the spawn: an import Brio would
        // refuse must not leave a spare actor standing in the scene.
        var path = _vm.Tiles[index].ThumbKey;
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }

        var spawned = _spawnService.SpawnNewActor(reserveCompanionSlot: false);
        if (spawned is null)
        {
            _notices.Failed("The actor could not be spawned.");
            return;
        }

        // The options are frozen HERE, at the click, so a toggle or tab
        // change made while the scene binds the new actor cannot retarget
        // the import.
        _pendingActor = spawned;
        _pendingPath = path;
        _pendingOptions = BuildImportOptions(path);
        _pendingFrames = 0;
        if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    /// <summary>Second half of <see cref="Spawn"/>: the scene has not rescanned
    /// at click time, so the new actor is selected and posed once the refresh
    /// has bound it. The pending state is cleared BEFORE the import, so no
    /// outcome can apply the same pose twice.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingActor is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
        {
            if (++_pendingFrames < PendingSpawnFrames)
                return;
            ClearPendingSpawn();
            _notices.Failed("Spawned actor never became ready.");
            return;
        }

        var path = _pendingPath!;
        var options = _pendingOptions!;
        ClearPendingSpawn();

        _selection.Select(SelectionId.ForActor(id));
        var result = _poseFacade.ImportPose(
            spawned,
            path,
            options,
            onReceipt: TrackImport(id));
        if (!result.Success)
            _notices.Failed(Failure(result));
    }

    private void ClearPendingSpawn()
    {
        _pendingActor = null;
        _pendingPath = null;
        _pendingOptions = null;
        _pendingFrames = 0;
    }

    private static string Failure(PoseEditResult result) =>
        "Apply: " + (result.Detail ?? "the pose could not be applied.");
}
