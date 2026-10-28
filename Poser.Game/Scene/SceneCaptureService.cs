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
/// Captures a scene document without native handles or entity references.
/// BeginCapture first refreshes skeleton caches through PoseExportCapture, then
/// builds the document on the framework thread. Pose imports block capture to
/// avoid saving an intermediate pose. Document relationships use logical IDs.
/// </summary>
public sealed class SceneCaptureService
{
    private readonly IFramework _framework;
    private readonly IActorManager _actors;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly IActorSpawnService _spawns;
    private readonly PropSpawnService _props;
    private readonly Poser.Game.Overlays.OverlayNodeService _overlays;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly EnvironmentControl _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly IPoseImportCommands _poses;
    private readonly IPlaceService _place;
    private readonly IObjectTable _objects;
    private readonly IPosingService _posing;
    private readonly AnimationSession _animation;
    private readonly IBonePosingService _bonePosing;
    private readonly Poser.Application.Appearance.IInvisibleSkinService _invisibleSkin;
    private readonly GazeService _gaze;
    private readonly PoseExportCapture _exports;
    private readonly Poser.Application.Integration.IntegrationSelectors _integration;
    private readonly IWorldRenderingRuntimePort _rendering;
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
        EnvironmentControl environment,
        StableBindingRegistry bindings,
        IPoseImportCommands poses,
        IPlaceService place,
        IObjectTable objects,
        IPosingService posing,
        AnimationSession animation,
        GazeService gaze,
        PoseExportCapture exports,
        Poser.Application.Integration.IntegrationSelectors integration,
        IWorldRenderingRuntimePort rendering,
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
    /// Starts a framework-thread capture and reports its result through onCaptured.
    /// Refreshes every available actor and companion skeleton before reading poses,
    /// since unedited skeleton caches may still contain their initial values.
    /// With no skeletons, capture runs immediately. Returns a reason if it cannot start.
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

        // Companions included: a companion is not a scene entry of its own,
        // but its pose IS captured through its owner, off the very same
        // caches.
        var slots = new List<ISkeleton>();
        foreach (var actor in _actors.Actors)
            slots.AddRange(_skeletons.GetSkeletons(actor));

        if (slots.Count == 0)
        {
            onCaptured(Capture(sceneId, description, includeAnimation));
            return null;
        }

        // The outcome is built inside the refresh's own write step, which runs
        // on the framework thread after every slot refreshed. An interrupted
        // or timed-out refresh refuses capture instead of saving stale caches.
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

    /// <summary>Builds the document after cache refresh, or directly when there
    /// are no skeletons. sceneId preserves identity when saving an existing scene.</summary>
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
                Description = description is { Length: > SceneFileLimits.MaxDescriptionCharacters }
                    ? description[..SceneFileLimits.MaxDescriptionCharacters]
                    : description,
                SavedAt = DateTimeOffset.UtcNow,
            };
            CaptureTerritory(scene);
            // Anchors cost nothing at capture and make every container entry
            // placeable later.
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
    /// Saves territory ID, display name, and local-player origin. The library can
    /// display the saved place without querying game data.
    /// </summary>
    private void CaptureTerritory(SceneFile scene)
    {
        var place = _place.Current;
        scene.TerritoryId = place.TerritoryId;
        scene.PlaceName = place.PlaceName;
        // The anchor a relative load rebases onto — the same one both
        // references anchor on, the local player. Absent with no local player
        // rather than defaulted to the world origin: a stated zero would rebase
        // a whole scene onto a place nobody ever stood.
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
                // An attached companion restores through its owner's
                // companion attachment; it is not a standalone scene entry.
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
                // Save the authored nickname, not a transient anonymous UI mask
                // or the generated native name needed by appearance providers.
                Name = Bounded((id is { } named ? _configuration.GetNickname(named.LogicalId) : null)
                    ?? Poser.Application.Settings.ConfigurationService.StripObjectIndex(actor.Name), $"Actor {key:N}"),
                ModelCharaId = Math.Max(0, _spawns.GetModelCharaId(actor)),
                SpawnedKind = spawnedKind,
                // Every loaded character is a clone of the local player, so
                // anyone else's look exists only in an appearance package.
                // Marked here and settled by the save policy, which is the
                // step that knows whether a package survived.
                AppearanceNotSaved = spawnedKind is null && !_actors.IsLocalPlayer(actor),
                LocalPlayer = spawnedKind is null && _actors.IsLocalPlayer(actor),
                NameIsDisplayName = true,
                PenumbraCollection = id is { } collectionActor ? CaptureCollection(collectionActor) : null,
                Visible = _spawns.IsVisible(actor),
                ClothingOnly = id is { } clothingActor && _invisibleSkin.IsApplied(clothingActor),
                // A live attachment proves the slot exists even when the
                // actor was not spawned by Poser with an explicit reservation.
                HasCompanionSlot = _spawns.HasCompanionSlot(actor) ||
                    companion is not null,
                CompanionKind = companion?.Kind,
                CompanionId = companion?.Id ?? 0,
                CompanionPose = companion is null
                    ? null
                    : CaptureCompanionPose(actor, notes),
                Pose = pose,
                // Pose already contains the evaluated IK result. Saving must
                // not restart a solver over these baked transforms on load.
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

    /// <summary>Only the user's own bone edits, as the offsets the animation's
    /// frame is moved by (the authored pose stacks), never the frame itself.</summary>
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
        // Every plain spawn is ASSIGNED the player's collection, so wearing
        // it is inheriting: saved, it would force an assignment and a redraw
        // per actor on load, and name a GUID other machines do not have.
        if (!assignment.HasIndividualAssignment
            || _integration.ReadPlayerCollection() is { Success: true, Value: var player }
                && player == assignment.EffectiveId)
            return null;
        // A duplicate/MCDF temporary collection's ID dies with its actor.
        // Only permanent local references can be reused on a later load.
        if (assignment.EffectiveId == Guid.Empty)
            return Guid.Empty;
        return _integration.ListCollections() is { Success: true, Value: { } collections }
            && collections.Any(x => x.Id == assignment.EffectiveId)
                ? assignment.EffectiveId : null;
    }

    /// <summary>
    /// Records the path and name of the actor's current MCDF package. Portable
    /// scene saving can later embed that package. Missing source paths are reported.
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
        };
    }

    /// <summary>
    /// Captures the attached companion's skeletons separately from its owner.
    /// Missing bodies or skeletons omit the companion pose and add a note.
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
    /// Where each captured actor is looking. Runs as a SECOND pass because an
    /// Entity-mode gaze names another actor: the target is written as that
    /// actor's in-document key, which only exists once every actor has one.
    /// A gaze following an actor the capture did not take records no target,
    /// with a note — a saved GameObjectId would name nothing in a restored
    /// scene, and following the wrong body is worse than following none.
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
            var state = _gaze.GetGazeState(actor);
            if (state.Mode == GazeTargetMode.None && !state.PoseAware)
                continue;

            Guid? target = null;
            if (state.Mode == GazeTargetMode.Entity)
            {
                // By stable id: a GPose copy shares its source's GameObjectId,
                // so only the binding names which body the gaze follows.
                if (state.TargetActor is { } followed)
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
                        $"Actor '{actor.Name}' looks at an uncaptured actor; the gaze target was not saved.");
            }

            var locked = GazeTargetType.None;
            foreach (var part in new[]
                     {
                         GazeTargetType.Body, GazeTargetType.Head,
                         GazeTargetType.Eyes,
                     })
            {
                if (_gaze.IsPartLocked(actor, part))
                    locked |= part;
            }

            entry.Gaze = new SceneActorGaze
            {
                PoseAware = state.PoseAware,
                // A remembered Entity target that no captured actor answers
                // for cannot be restored as a follow, so the file states the
                // mode WITHOUT a target rather than an unfollowable one.
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
                Transform = NormalizedTransform(
                    prop.Transform, $"Object '{prop.Name}'", notes),
            });
        }
    }

    /// <summary>
    /// The staged overlay nodes, each as its whole document. The list is left
    /// ABSENT when there are none, so a scene with no staged dialogue writes
    /// exactly the file it wrote before overlay nodes existed.
    /// </summary>
    private void CaptureOverlays(SceneFile scene)
    {
        // Positions are stored relative to the screen CENTRE so an overlay
        // lands sensibly on another resolution or aspect ratio. A centre
        // that cannot be read keeps absolute pixels and says so on the
        // entry.
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
    /// Captures both borrowed and spawned world objects, including their current
    /// appearance and placement. MapPosition retains the original placement for
    /// compatibility; scene loading creates a new object from the saved path.
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
                    targetName = Bounded(camera.TargetActorName, "Camera target");
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

    /// <summary>Resolves the followed actor through its exact stable
    /// generation. Display names are presentation only and never identify a
    /// camera relationship.</summary>
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
    /// The ONE world-toggle snapshot builder; the workflow's rollback baseline
    /// uses it too. Physics states what the SCENE holds rather than the raw
    /// global: the patch can be true because something else set it, and a
    /// scene that claimed that hold would release a freeze it never took.
    /// </summary>
    internal SceneWorld CaptureWorld() => new()
    {
        IsWaterFrozen = _rendering.IsWaterFrozen,
        IsPhysicsFrozen = _animation.SceneOwnsPhysics,
    };

    /// <summary>The ONE environment snapshot builder; the workflow's
    /// rollback baseline uses it too.</summary>
    internal SceneEnvironment CaptureEnvironment() => _environment.Capture();

    /// <summary>A native transform with a degenerate rotation captures as
    /// identity rotation, with a note — the alternative is a whole-save
    /// refusal over one broken native value.</summary>
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
