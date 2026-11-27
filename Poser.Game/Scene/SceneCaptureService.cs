using Poser.Application.World;
using Poser.Application.Posing;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;


/// <summary>
/// Builds a scene file from what is in the scene now. The file holds no
/// pointers into game memory: actors, lights and the rest point at each other
/// by ids saved in the file. Before reading poses it has the game update every
/// skeleton, then builds the file on the game's main thread. It does nothing
/// while a pose is being applied, so a half-applied pose is never saved.
/// </summary>
public sealed class SceneCaptureService
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IActor,
        System.Runtime.CompilerServices.StrongBox<string>> _savedIdentities = new();

    internal void RememberIdentity(IActor actor, string? hash)
    {
        _savedIdentities.Remove(actor);
        if (hash != null) _savedIdentities.Add(actor, new(hash));
    }

    private readonly IFramework _framework;
    private readonly IActorManager _actors;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly IActorSpawnService _spawns;
    private readonly PropSpawnService _props;
    private readonly Poser.Game.Overlays.OverlayNodeService _overlays;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly Poser.Game.Environment.EnvironmentControl _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly IPoseImportCommands _poses;
    private readonly IPlaceService _place;
    private readonly IObjectTable _objects;
    private readonly IPosingService _posing;
    private readonly Poser.Game.Animation.AnimationSession _animation;
    private readonly IBonePosingService _bonePosing;
    private readonly Poser.Application.Appearance.IInvisibleSkinService _invisibleSkin;
    private readonly GazeSession _gaze;
    private readonly PoseExportCapture _exports;
    private readonly Poser.Application.Integration.IntegrationSelectors _integration;
    private readonly Poser.Game.Environment.WorldRenderingService _rendering;
    private readonly WorldObjects.WorldService _worldObjects;
    private readonly PlacementAnchorSource _anchors;
    private readonly Poser.Application.Settings.ConfigurationService _configuration;

    public SceneCaptureService(
        IFramework framework,
        IActorManager actors,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        IActorSpawnService spawns,
        PropSpawnService props,
        Poser.Game.Overlays.OverlayNodeService overlays,
        ILightingService lighting,
        IVirtualCameraService cameras,
        Poser.Game.Environment.EnvironmentControl environment,
        StableBindingRegistry bindings,
        IPoseImportCommands poses,
        IPlaceService place,
        IObjectTable objects,
        IPosingService posing,
        Poser.Game.Animation.AnimationSession animation,
        GazeSession gaze,
        PoseExportCapture exports,
        Poser.Application.Integration.IntegrationSelectors integration,
        Poser.Game.Environment.WorldRenderingService rendering,
        WorldObjects.WorldService worldObjects,
        PlacementAnchorSource anchors,
        Poser.Application.Settings.ConfigurationService configuration,
        IBonePosingService bonePosing,
        Poser.Application.Appearance.IInvisibleSkinService invisibleSkin)
    {
        _invisibleSkin = invisibleSkin;
        _bonePosing = bonePosing;
        _configuration = configuration;
        _anchors = anchors;
        _worldObjects = worldObjects;
        _rendering = rendering;
        _integration = integration;
        _exports = exports;
        _place = place;
        _objects = objects;
        _posing = posing;
        _animation = animation;
        _gaze = gaze;
        _framework = framework;
        _actors = actors;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _spawns = spawns;
        _props = props;
        _overlays = overlays;
        _lighting = lighting;
        _cameras = cameras;
        _environment = environment;
        _bindings = bindings;
        _poses = poses;
    }

    /// <summary>
    /// Starts saving the scene and reports the result through onCaptured.
    /// It first updates the skeleton of every actor and companion, because
    /// Poser's copy of a bone no one has edited can still hold an old value.
    /// With no skeletons it saves at once. Returns the reason when it cannot start.
    /// </summary>
    public string? BeginCapture(
        Guid sceneId, string? description, Action<SceneCaptureOutcome> onCaptured, bool includeAnimation = false)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return "Scene capture must run on the framework thread.";
        if (_poses.IsImportBusy)
            return "A pose import is applying; capturing now would snapshot a half-transitioned pose.";
        if (sceneId == Guid.Empty)
            return "A scene capture needs a scene identity.";

        // Companions too. A companion is not saved as an actor of its own,
        // but its pose is saved with the actor it belongs to, from the same
        // skeleton data.
        var slots = new List<ISkeleton>();
        foreach (var actor in _actors.Actors)
            slots.AddRange(_skeletons.GetSkeletons(actor));

        if (slots.Count == 0)
        {
            onCaptured(Capture(sceneId, description, includeAnimation));
            return null;
        }

        // The scene file is built right after every skeleton has updated, on
        // the game's main thread. If the update is interrupted or takes too
        // long, nothing is saved, rather than saving old bone values.
        SceneCaptureOutcome? outcome = null;
        var begun = _exports.Begin(
            slots,
            _ =>
            {
                outcome = Capture(sceneId, description, includeAnimation);
                return outcome.Success;
            },
            _ => onCaptured(outcome ?? SceneCaptureOutcome.Fail(
                "The scene pose refresh did not complete; no snapshot was captured.")));
        return begun.Success
            ? null
            : begun.Detail ?? "The scene capture could not be armed.";
    }

    /// <summary>Builds the scene file once the skeletons have updated, or at once
    /// when there are none. Saving over an existing scene passes its own sceneId,
    /// so it stays the same scene.</summary>
    private SceneCaptureOutcome Capture(Guid sceneId, string? description, bool includeAnimation)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return SceneCaptureOutcome.Fail(
                "Scene capture must run on the framework thread.");
        if (_poses.IsImportBusy)
            return SceneCaptureOutcome.Fail(
                "A pose import is applying; capturing now would snapshot a half-transitioned pose.");
        if (sceneId == Guid.Empty)
            return SceneCaptureOutcome.Fail("A scene capture needs a scene identity.");

        try
        {
            var notes = new List<string>();
            var scene = new SceneFile
            {
                SceneId = sceneId,
                SavedBy = _configuration.Config.Library.InstallId,
                Description = description is { Length: > SceneFileLimits.MaxDescriptionCharacters }
                    ? description[..SceneFileLimits.MaxDescriptionCharacters]
                    : description,
                SavedAt = DateTimeOffset.UtcNow,
            };
            CaptureTerritory(scene);
            // The camera and actor positions are cheap to save, and they let
            // every saved item be placed relative to them later.
            scene.CameraAnchor = _anchors.CameraAnchorNow();
            scene.ActorAnchor = _anchors.ActorAnchorNow();

            var identities = new Dictionary<Guid, Poser.Domain.Identity.ActorId>();
            var actorKeys = CaptureActors(scene, notes, identities, includeAnimation);
            CaptureProps(scene, notes);
            CaptureOverlays(scene);
            CaptureWorldObjects(scene, notes);
            CaptureLights(scene, actorKeys, notes);
            CaptureCameras(scene, actorKeys, notes);
            scene.Environment = CaptureEnvironment();
            var world = CaptureWorld();
            scene.World = world.IsDefault ? null : world;

            var validation = SceneFileValidation.Validate(scene);
            if (!validation.Succeeded)
                return SceneCaptureOutcome.Fail(
                    $"The captured scene did not validate: {validation.Failure!.Detail}");

            return SceneCaptureOutcome.Ok(scene, notes, identities);
        }
        catch (Exception ex)
        {
            return SceneCaptureOutcome.Fail(
                $"Scene capture failed unexpectedly: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves which zone the scene is in, the zone's name, and where the player's
    /// own character stands. The Library can then show the place without
    /// looking it up in game data.
    /// </summary>
    private void CaptureTerritory(SceneFile scene)
    {
        var place = _place.Current;
        scene.TerritoryId = place.TerritoryId;
        scene.PlaceName = place.PlaceName;
        // Loading a scene relative to the player moves everything by the
        // player's position, as Brio and Ktisis do. With no player character
        // this stays empty instead of zero, because zero would move the whole
        // scene to a spot where no one stood.
        scene.Origin = _objects.LocalPlayer?.Position;
    }

    private Dictionary<IActor, Guid> CaptureActors(
        SceneFile scene,
        List<string> notes,
        Dictionary<Guid, Poser.Domain.Identity.ActorId> identities,
        bool includeAnimation)
    {
        var keys = new Dictionary<IActor, Guid>();
        var captured = new List<(IActor Actor, SceneActor Entry)>();
        foreach (var actor in _actors.Actors)
        {
            if (actor.IsCompanion)
            {
                // A companion comes back through the actor it belongs to; it is
                // not saved as an actor of its own.
                continue;
            }

            var slots = _skeletons.GetSkeletons(actor);
            if (slots.Count == 0)
            {
                notes.Add($"Actor '{actor.Name}' has no skeleton and was not captured.");
                continue;
            }

            var pose = _poseFiles.CreatePoseFile(slots);
            if (pose is null)
            {
                notes.Add($"Actor '{actor.Name}' pose could not be captured; the actor was skipped.");
                continue;
            }

            var companion = _spawns.GetCompanionInfo(actor);
            var spawnedKind = _spawns.GetSpawnedKind(actor);
            var id = _bindings.GetActorId(actor);
            var key = id?.LogicalId ?? Guid.NewGuid();
            keys[actor] = key;
            if (id is { } bound)
                identities[key] = bound;
            var entry = new SceneActor
            {
                Key = key,
                // Anonymous saves use numbered labels even when a nickname contains a real name.
                Name = _configuration.Config.Display.AnonymousMode
                    ? $"Actor {scene.Actors.Count + 1}"
                    : Bounded(_configuration.GetDisplayName(key, actor.Name), $"Actor {scene.Actors.Count + 1}"),
                IdentityHash = !_spawns.IsSpawnedActor(actor)
                    ? SceneActorNames.Hash(actor.Name)
                    : _savedIdentities.TryGetValue(actor, out var savedIdentity) ? savedIdentity.Value : null,
                ModelCharaId = Math.Max(0, _spawns.GetModelCharaId(actor)),
                SpawnedKind = spawnedKind,
                // Poser saves no appearance for a character it did not spawn, other than the player's own.
                AppearanceNotSaved = spawnedKind is null && !_actors.IsLocalPlayer(actor),
                LocalPlayer = spawnedKind is null && _actors.IsLocalPlayer(actor),
                NameIsDisplayName = true,
                PenumbraCollection = id is { } collectionActor ? CaptureCollection(collectionActor) : null,
                Visible = _spawns.IsVisible(actor),
                ClothingOnly = id is { } clothingActor && _invisibleSkin.IsApplied(clothingActor),
                // An actor that has a companion right now has room for one,
                // even if Poser did not spawn it with that room set aside.
                HasCompanionSlot = _spawns.HasCompanionSlot(actor) ||
                    companion is not null,
                CompanionKind = companion?.Kind,
                CompanionId = companion?.Id ?? 0,
                CompanionPose = companion is null
                    ? null
                    : CaptureCompanionPose(actor, notes),
                Pose = pose,
                // The saved pose already includes what IK did, so loading must
                // not run IK over it again.
                ModelTransform = NormalizedTransform(
                    _posing.GetEffectiveTransform(actor),
                    $"Actor '{actor.Name}' placement", notes),
                Mcdf = id is { } mcdfActorId ? CaptureMcdf(mcdfActorId, notes) : null,
            };
            if (includeAnimation && id is { } animated)
            {
                entry.Animation = Poser.Application.Scene.SceneAnimationCodec.ToDocument(_animation.CaptureForScene(animated));
                entry.PoseEdits = CaptureEdits(slots);
                if (companion is not null && _spawns.GetCompanionActor(actor) is { } companionActor
                    && _bindings.GetActorId(companionActor) is { } companionId)
                {
                    entry.CompanionAnimation = Poser.Application.Scene.SceneAnimationCodec.ToDocument(_animation.CaptureForScene(companionId));
                    entry.CompanionPoseEdits = CaptureEdits(_skeletons.GetSkeletons(companionActor));
                }
            }
            captured.Add((actor, entry));
            scene.Actors.Add(entry);
        }

        CaptureGaze(captured, keys, notes);
        return keys;
    }

    /// <summary>Only the bone changes made in Poser, saved as offsets from what
    /// the animation shows. The animation's own pose is not saved.</summary>
    private List<SceneBoneEdit> CaptureEdits(IReadOnlyList<ISkeleton> slots) =>
        Poser.Game.Posing.AuthoredPoseState.Capture(slots, _bonePosing).Bones
            .Select(bone => new SceneBoneEdit
            {
                Slot = bone.Slot,
                Partial = bone.Partial,
                Name = bone.Name,
                PartialRootScale = bone.PartialRootScale,
                Offsets = bone.Stacks.Select(stack => new SceneBoneOffset
                {
                    Propagate = stack.PropagateComponents,
                    Position = stack.Transform.Position,
                    Rotation = stack.Transform.Rotation,
                    Scale = stack.Transform.Scale,
                    Frame = stack.Frame,
                    IkPosition = stack.IkTransform?.Position,
                    IkRotation = stack.IkTransform?.Rotation,
                    IkScale = stack.IkTransform?.Scale,
                }).ToList(),
            })
            .ToList();

    private Guid? CaptureCollection(Poser.Domain.Identity.ActorId actor)
    {
        if (_integration.ReadCollection(actor) is not { Success: true, Value: { } assignment })
            return null;
        // Every spawned actor gets the player's Penumbra collection anyway.
        // Saving it would set the collection and redraw each actor again on
        // load, and would name a collection other computers do not have.
        if (!assignment.HasIndividualAssignment
            || _integration.ReadPlayerCollection() is { Success: true, Value: var player }
                && player == assignment.EffectiveId)
            return null;
        // A temporary collection, made for a copied actor or an MCDF, goes away
        // with its actor. Only collections that stay in Penumbra can be used again.
        if (assignment.EffectiveId == Guid.Empty)
            return Guid.Empty;
        return _integration.ListCollections() is { Success: true, Value: { } collections }
            && collections.Any(x => x.Id == assignment.EffectiveId)
                ? assignment.EffectiveId : null;
    }

    /// <summary>
    /// Saves the path and name of the MCDF applied to the actor, so the scene can
    /// later pack that file in. An MCDF with no known path gets a note instead.
    /// </summary>
    private SceneActorMcdf? CaptureMcdf(
        Poser.Domain.Identity.ActorId id, List<string> notes)
    {
        if (_integration.OverridesFor(id).Mcdf is not { } mcdf)
            return null;
        if (string.IsNullOrWhiteSpace(mcdf.SourcePath))
        {
            notes.Add(
                $"The character file '{mcdf.FileName}' was imported without a " +
                "recorded path and could not be saved into the scene.");
            return null;
        }
        return new SceneActorMcdf
        {
            Path = mcdf.SourcePath,
            FileName = Bounded(mcdf.FileName, "Character file"),
            // An appearance unpacked from a scene file belongs to that scene, so it is not marked as loaded onto the actor.
            LoadedOnto = !System.IO.Path.GetFileName(mcdf.SourcePath)
                .StartsWith(Poser.Application.Scene.SceneAppearanceRule.PackedPrefix, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Saves the companion's pose apart from the actor it belongs to. If the
    /// companion's body or skeleton cannot be found, its pose is left out with a note.
    /// </summary>
    private PoseFile? CaptureCompanionPose(IActor owner, List<string> notes)
    {
        if (_spawns.GetCompanionActor(owner) is not { } companion)
        {
            notes.Add(
                $"Actor '{owner.Name}' has a companion whose body could not be " +
                "resolved; its pose was not captured.");
            return null;
        }

        var slots = _skeletons.GetSkeletons(companion);
        if (slots.Count == 0)
        {
            notes.Add(
                $"Actor '{owner.Name}''s companion has no skeleton; its pose " +
                "was not captured.");
            return null;
        }

        var pose = _poseFiles.CreatePoseFile(slots);
        if (pose is null)
            notes.Add(
                $"Actor '{owner.Name}''s companion pose could not be captured.");
        return pose;
    }

    /// <summary>
    /// Where each saved actor is looking. This runs after every actor is saved,
    /// because an actor can look at another actor, which the file names by its
    /// saved id, and those ids only exist once every actor is saved.
    /// An actor looking at an actor that was not saved gets no target and a
    /// note: the game's own id would mean nothing after loading, and looking at
    /// the wrong actor is worse than looking at no one.
    /// </summary>
    private void CaptureGaze(
        List<(IActor Actor, SceneActor Entry)> captured,
        Dictionary<IActor, Guid> keys,
        List<string> notes)
    {
        if (!_gaze.IsAvailable)
            return;

        foreach (var (actor, entry) in captured)
        {
            // The gaze controls' own reading, the one undo saves.
            if (_bindings.GetActorId(actor) is not { } id
                || _gaze.Read(id) is not { } reading
                || reading.Settings.Mode == GazeTargetMode.None)
                continue;
            var state = reading.Settings;

            Guid? target = null;
            if (state.Mode == GazeTargetMode.Entity)
            {
                // Match by Poser's own actor id: a GPose copy has the same game
                // id as the actor it copies, so only Poser's id tells them apart.
                if (reading.Target is { } followed)
                    foreach (var (candidate, _) in captured)
                    {
                        if (_bindings.GetActorId(candidate) == followed)
                        {
                            target = keys[candidate];
                            break;
                        }
                    }
                if (target is null)
                    notes.Add(
                        $"Actor '{actor.Name}' looks at an actor that is not in this save or is no longer there; the gaze target was not saved.");
            }

            var locked = GazeTargetType.None;
            foreach (var part in new[]
                     {
                         GazeTargetType.Body, GazeTargetType.Head,
                         GazeTargetType.Eyes,
                     })
            {
                if (state.IsPartLocked(part))
                    locked |= part;
            }

            entry.Gaze = new SceneActorGaze
            {
                // When the actor looked at was not saved, the file keeps the
                // look-at-an-actor mode with no target, rather than a target
                // that cannot be found after loading.
                Mode = state.Mode,
                Parts = state.TargetType & GazeTargetType.All,
                TargetActorKey = target,
                Position = Finite(state.Position),
                EyesPosition = Finite(state.EyesPosition),
                HeadPosition = Finite(state.HeadPosition),
                BodyPosition = Finite(state.BodyPosition),
                LockedParts = locked,
            };
        }
    }

    private static System.Numerics.Vector3 Finite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z)
            ? value
            : System.Numerics.Vector3.Zero;

    private void CaptureProps(SceneFile scene, List<string> notes)
    {
        foreach (var prop in _props.Props)
        {
            if (!prop.IsValid)
            {
                notes.Add($"Object '{prop.Name}' is no longer valid and was not captured.");
                continue;
            }

            scene.Props.Add(new SceneProp
            {
                Key = _bindings.GetPropId(prop)?.LogicalId ?? Guid.NewGuid(),
                Name = Bounded(prop.Name, "Object"),
                Model = prop.Model.Model,
                Submodel = prop.Model.Submodel,
                Variant = prop.Model.Variant,
                Stain0 = prop.Model.Stain0,
                Stain1 = prop.Model.Stain1,
                AnimationVariant = prop.Model.AnimationVariant,
                Visible = prop.Visible,
                Tint = prop.Tint == System.Numerics.Vector4.One ? null : prop.Tint,
                Transform = NormalizedTransform(
                    prop.Transform, $"Object '{prop.Name}'", notes),
            });
        }
    }

    /// <summary>
    /// Saves the overlays in the scene, each one in full. When there are none,
    /// the list is left out of the file, so a scene without overlays saves the
    /// same file as a Poser without overlays would.
    /// </summary>
    private void CaptureOverlays(SceneFile scene)
    {
        // Positions are saved relative to the middle of the screen, so an
        // overlay lands in a sensible place on another screen size. If the
        // screen size cannot be read, positions are saved in plain pixels
        // and the entry says so.
        var display = Dalamud.Bindings.ImGui.ImGui.GetIO().DisplaySize;
        bool centred = display.X > 0f && display.Y > 0f;
        var center = centred
            ? new System.Numerics.Vector2(display.X / 2f, display.Y / 2f)
            : default;
        foreach (var overlay in _overlays.Nodes)
        {
            if (!overlay.IsValid)
                continue;
            scene.Overlays ??= new List<SceneOverlay>();
            scene.Overlays.Add(new SceneOverlay
            {
                Key = _bindings.GetOverlayId(overlay)?.LogicalId
                    ?? Guid.NewGuid(),
                CenterRelative = centred && overlay.State.Collider == null,
                Node = overlay.State with
                {
                    Name = Bounded(overlay.State.Name, "Overlay"),
                    Position = centred && overlay.State.Collider == null
                        ? overlay.State.Position - center
                        : overlay.State.Position,
                },
            });
        }
    }

    /// <summary>
    /// Saves the world objects, both those taken over from the map and those
    /// Poser spawned, with how they look and where they are. MapPosition keeps
    /// where the object first stood on the map; loading a scene does not use it
    /// to find the object, but creates a new one from the saved path.
    /// </summary>
    private void CaptureWorldObjects(SceneFile scene, List<string> notes)
    {
        foreach (var worldObject in _worldObjects.Adopted)
        {
            if (!worldObject.IsValid)
            {
                notes.Add(
                    $"World object '{worldObject.Name}' is no longer there and " +
                    "was not captured.");
                continue;
            }

            scene.WorldObjects ??= new List<SceneWorldObject>();
            scene.WorldObjects.Add(new SceneWorldObject
            {
                Key = _bindings.GetWorldObjectId(worldObject)?.LogicalId
                    ?? Guid.NewGuid(),
                Path = Bounded(worldObject.Path, "World object"),
                MapPosition = Finite(worldObject.InitialPlacement.Position),
                Visible = worldObject.Visible,
                Spawned = worldObject.Spawned,
                Name = worldObject.Name,
                Opacity = worldObject.Opacity,
                Tint = worldObject.Tint,
                Stain = worldObject.Stain,
                FurnitureLights = System.Linq.Enumerable.ToArray(worldObject.FurnitureLights),
                VfxLoop = worldObject.LoopVfx,
                VfxSpeed = worldObject.VfxSpeed,
                VfxIntensity = worldObject.VfxIntensity,
                VfxPaused = worldObject.VfxPaused,
                NightState = worldObject.NightState,
                AnimPaused = worldObject.AnimationPaused,
                Transform = NormalizedTransform(
                    worldObject.Transform,
                    $"World object '{worldObject.Name}'",
                    notes),
            });
        }
    }

    private void CaptureLights(
        SceneFile scene, Dictionary<IActor, Guid> actorKeys, List<string> notes)
    {
        foreach (var light in _lighting.Lights)
        {
            if (!light.IsValid)
            {
                notes.Add($"Light '{light.Name}' is no longer valid and was not captured.");
                continue;
            }

            var document = Lighting.LightDocument.Capture(light);
            document.Name = Bounded(document.Name, "Light");
            document.Transform = NormalizedTransform(
                (Transform)document.Transform, $"Light '{light.Name}'", notes);

            SceneBoneAttachment? attachment = null;
            if (light.AttachedBone is { } bone)
            {
                if (actorKeys.TryGetValue(bone.Skeleton.Actor, out var owner))
                {
                    attachment = new SceneBoneAttachment
                    {
                        ActorKey = owner,
                        Slot = bone.Skeleton.Slot,
                        PartialId = bone.PartialId,
                        BoneName = bone.BoneName,
                    };
                }
                else
                {
                    notes.Add(
                        $"Light '{light.Name}' is attached to an uncaptured actor; the attachment was not saved.");
                }
            }

            scene.Lights.Add(new SceneLight
            {
                Key = _bindings.GetLightId(light)?.LogicalId ?? Guid.NewGuid(),
                Light = document,
                Attachment = attachment,
            });
        }
    }

    private void CaptureCameras(
        SceneFile scene, Dictionary<IActor, Guid> actorKeys, List<string> notes)
    {
        foreach (var camera in _cameras.Cameras)
        {
            if (!camera.IsValid)
            {
                notes.Add($"Camera '{camera.Name}' is no longer valid and was not captured.");
                continue;
            }

            var document = Cameras.CameraDocument.CaptureScene(camera);
            document.Name = Bounded(document.Name, "Camera");

            Guid? targetKey = null;
            var targetName = string.Empty;
            var targetOffset = System.Numerics.Vector3.Zero;
            if (camera.TargetActorId != null)
            {
                var target = ResolveTarget(camera, actorKeys);
                if (target is { } resolved)
                {
                    targetKey = resolved;
                    // The followed actor's saved name, so Anonymous mode covers it too.
                    targetName = scene.Actors.FirstOrDefault(entry => entry.Key == resolved)?.Name
                        ?? Bounded(camera.TargetActorName, "Camera target");
                    targetOffset = camera.TargetOffset;
                }
                else
                {
                    notes.Add(
                        $"Camera '{camera.Name}' follows '{camera.TargetActorName}', which no longer resolves; the target was not saved.");
                }
            }

            scene.Cameras.Add(new SceneCamera
            {
                Key = _bindings.GetCameraId(camera)?.LogicalId ?? Guid.NewGuid(),
                Camera = document,
                IsLive = camera.IsLive,
                IsDefault = camera.IsDefault,
                TargetActorKey = targetKey,
                TargetActorName = targetName,
                TargetOffset = targetOffset,
                IsTargetLocked = targetKey != null && camera.IsTargetLocked,
            });
        }
    }

    /// <summary>Finds the actor the camera follows by Poser's own actor id and
    /// checks it is still the very same actor. Names are only for display and
    /// are never used to find it.</summary>
    private Guid? ResolveTarget(
        IVirtualCamera camera, Dictionary<IActor, Guid> actorKeys)
    {
        if (camera.TargetActorId is not { } targetId ||
            _bindings.Resolve(targetId) is not
                { Success: true, Value: { } exact } ||
            !ReferenceEquals(exact, camera.TargetActor))
            return null;
        return actorKeys.TryGetValue(exact, out var key) ? key : null;
    }

    /// <summary>
    /// Saves whether water and physics are frozen. Loading a scene also uses
    /// this to remember what to go back to if the load fails. Physics counts as
    /// frozen only when the scene itself froze it: something else may have
    /// frozen it, and a scene that claimed that freeze would later undo a
    /// freeze it never made.
    /// </summary>
    internal SceneWorld CaptureWorld() => new()
    {
        IsWaterFrozen = _rendering.IsWaterFrozen,
        IsPhysicsFrozen = _animation.SceneOwnsPhysics,
    };

    /// <summary>Saves the environment. Loading a scene also uses this to
    /// remember what to go back to if the load fails.</summary>
    internal SceneEnvironment CaptureEnvironment() => _environment.Capture();

    /// <summary>A broken rotation read from the game is saved as no rotation,
    /// with a note. Otherwise one bad value would stop the whole scene from
    /// saving.</summary>
    private static LightFile.TransformData NormalizedTransform(
        Transform transform, string label, List<string> notes)
    {
        var data = (LightFile.TransformData)transform;
        if (data.Rotation.LengthSquared() <
            SceneFileLimits.MinQuaternionLengthSquared)
        {
            notes.Add($"{label} carried a degenerate rotation; identity was saved.");
            data.Rotation = System.Numerics.Quaternion.Identity;
        }
        return data;
    }

    private static string Bounded(string? name, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(name) ? fallback : name;
        return value.Length <= SceneFileLimits.MaxNameCharacters
            ? value
            : value[..SceneFileLimits.MaxNameCharacters];
    }
}
