using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>Typed result of one whole-scene capture. Notes are per-entity
/// observations about state the capture could not represent (an actor with
/// no skeleton, a camera target that no longer resolves) — they are part of
/// the read model, never silently dropped facts.</summary>
public sealed class SceneCaptureOutcome
{
    public bool Success { get; }
    public string? Detail { get; }
    public SceneFile? Scene { get; }
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Document actor key → the EXACT live actor generation it was
    /// captured from. The document deliberately carries no native identity, so
    /// a post-capture step that must talk to the live actor — sealing a
    /// portable appearance payload, which needs the exporter — reads it here
    /// rather than re-resolving an actor by name. An unbound actor has no
    /// entry, and a step that needs one refuses by name instead of guessing.
    /// </summary>
    public IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> ActorIdentities
    { get; }

    private SceneCaptureOutcome(
        bool success,
        string? detail,
        SceneFile? scene,
        IReadOnlyList<string> notes,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> identities)
    {
        Success = success;
        Detail = detail;
        Scene = scene;
        Notes = notes;
        ActorIdentities = identities;
    }

    internal static SceneCaptureOutcome Ok(
        SceneFile scene,
        List<string> notes,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId>? identities = null) =>
        new(true, null, scene, notes.AsReadOnly(),
            identities ?? EmptyIdentities);

    internal static SceneCaptureOutcome Fail(string detail) =>
        new(false, detail, null, Array.Empty<string>(), EmptyIdentities);

    private static readonly IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId>
        EmptyIdentities = new Dictionary<Guid, Poser.Domain.Identity.ActorId>();
}

/// <summary>
/// Read-only, pointer-free whole-scene capture. Runs synchronously on the
/// framework thread and produces the complete <see cref="SceneFile"/> in
/// memory BEFORE any file work — no native handle, address, or entity
/// reference survives into the document. Entity keys are the stable binding
/// logical ids (fresh ids for unregistered entities), so relationships in the
/// file are independent of any native binding generation.
///
/// Capture refuses while a pose import owns the caches for the same reason
/// <see cref="CleanPoseFacade"/>'s copy capture does: the apply window pauses
/// and rewinds the animation, and a snapshot landing inside it would persist
/// a half-transitioned pose.
///
/// <para>The capture is ARMED, not called — see <see cref="BeginCapture"/>.
/// The bone values a scene serializes come out of the same raw transform
/// caches an ordinary pose export reads, and those caches are only refreshed
/// for skeletons the per-frame rebuild qualified, so a never-posed actor's
/// cache still holds its skeleton-build-time snapshot. Reading them
/// synchronously is exactly the bug <see cref="PoseExportCapture"/> exists
/// for.</para>
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
    private readonly IEnvironmentService _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly CleanPoseFacade _poses;
    private readonly IPlaceService _place;
    private readonly IObjectTable _objects;
    private readonly IPosingService _posing;
    private readonly AnimationSession _animation;
    private readonly IGazeService _gaze;
    private readonly PoseExportCapture _exports;
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly IWorldRenderingService _rendering;
    private readonly WorldObjects.WorldObjectService _worldObjects;
    private readonly PlacementAnchorSource _anchors;

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
        IEnvironmentService environment,
        StableBindingRegistry bindings,
        CleanPoseFacade poses,
        IPlaceService place,
        IObjectTable objects,
        IPosingService posing,
        AnimationSession animation,
        IGazeService gaze,
        PoseExportCapture exports,
        Poser.Application.Integration.ActorIntegrationSession integration,
        IWorldRenderingService rendering,
        WorldObjects.WorldObjectService worldObjects,
        PlacementAnchorSource anchors)
    {
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
    /// Arms one whole-scene capture and answers through
    /// <paramref name="onCaptured"/> once the refresh has landed. Returns the
    /// refusal detail, or null when armed. Framework thread only; the workflow
    /// owns the marshal.
    ///
    /// <para>The arming registers the SAME no-op transitive batch a pose export
    /// registers, on every posable slot skeleton in the scene at once, so ONE
    /// update pass makes every actor's raw transform cache current before a
    /// single bone is read. Without it a never-posed actor serializes the
    /// values written when its skeleton was built — an actor standing in its
    /// idle would come back in whatever it held at spawn, and nothing about the
    /// saved file would say so. Brio needs no such arming because it refreshes
    /// every capability-bearing skeleton's caches every frame
    /// (Brio SkeletonService.cs:205-249, unconditional over
    /// <c>_skeletonToPosingCapability</c>); Poser refreshes only the skeletons
    /// the per-frame rebuild qualified, so the parity is restored per save.</para>
    ///
    /// <para>A scene with no posable skeleton at all has nothing to refresh and
    /// captures inline — the answer still arrives through the callback, so the
    /// caller has one path.</para>
    /// </summary>
    public string? BeginCapture(
        Guid sceneId, string? description, Action<SceneCaptureOutcome> onCaptured)
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
            onCaptured(Capture(sceneId, description));
            return null;
        }

        // The outcome is built inside the refresh's own write step, which runs
        // on the framework thread once the pass has ended (or once the export
        // capture's tick bound gives up, in which case the caches are exactly
        // as fresh as a synchronous capture would have found them).
        SceneCaptureOutcome? outcome = null;
        var begun = _exports.Begin(
            slots,
            _ =>
            {
                outcome = Capture(sceneId, description);
                return outcome.Success;
            },
            _ => onCaptured(outcome ?? SceneCaptureOutcome.Fail(
                "The scene capture produced no result.")));
        return begun.Success
            ? null
            : begun.Detail ?? "The scene capture could not be armed.";
    }

    /// <summary>The capture itself, run only from inside the armed refresh
    /// above so the caches it reads are current. <paramref name="sceneId"/>
    /// keeps a re-saved scene's identity stable across saves.</summary>
    private SceneCaptureOutcome Capture(Guid sceneId, string? description)
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
                Description = description,
                SavedAt = DateTimeOffset.UtcNow,
            };
            CaptureTerritory(scene);
            // Anchors cost nothing at capture and make every container entry
            // placeable later.
            scene.CameraAnchor = _anchors.CameraAnchorNow();
            scene.ActorAnchor = _anchors.ActorAnchorNow();

            var identities = new Dictionary<Guid, Poser.Domain.Identity.ActorId>();
            var actorKeys = CaptureActors(scene, notes, identities);
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
    /// Where the capture ran. The id is the durable machine fact; the NAME is
    /// persisted beside it, because the listing that groups scenes by place
    /// runs in PosingCore, which has no game data to resolve an id with. The
    /// resolution itself lives in <see cref="IPlaceService"/>, which pose
    /// auto-save stamps from too — a place must mean the same thing in both
    /// documents.
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
        Dictionary<Guid, Poser.Domain.Identity.ActorId> identities)
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
            var id = _bindings.GetActorId(actor);
            var key = id?.LogicalId ?? Guid.NewGuid();
            keys[actor] = key;
            if (id is { } bound)
                identities[key] = bound;
            var entry = new SceneActor
            {
                Key = key,
                Name = Bounded(actor.Name, $"Actor {key:N}"),
                ModelCharaId = Math.Max(0, _spawns.GetModelCharaId(actor)),
                Visible = _spawns.IsVisible(actor),
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
                ModelTransform = NormalizedTransform(
                    _posing.GetEffectiveTransform(actor),
                    $"Actor '{actor.Name}' placement", notes),
                Mcdf = id is { } mcdfActorId ? CaptureMcdf(mcdfActorId, notes) : null,
            };
            captured.Add((actor, entry));
            scene.Actors.Add(entry);
        }

        CaptureGaze(captured, keys, notes);
        return keys;
    }

    /// <summary>
    /// WHICH character file the actor is wearing, as a reference — the path
    /// and a name, never the package. Appearance itself stays with its owners
    /// (Glamourer/Penumbra/Customize+); this is the ONE appearance fact a
    /// scene records, because it is the one Poser itself put on the actor and
    /// therefore the one it can put back.
    ///
    /// <para>An MCDF that Poser owns but whose source path was never recorded —
    /// ownership committed by a build before this — records nothing, with a
    /// note. Guessing at a path would be worse than saying so.</para>
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
    /// The attached companion's own pose, read off the companion's own
    /// skeletons. A minion, mount or ornament is a posable body: restoring the
    /// attachment alone brings it back idling, which is what a scene saved
    /// before this did. Brio captures the same document
    /// (ActorDTO.cs:130-138).
    ///
    /// <para>An attachment whose body has not drawn yet, or has no skeleton,
    /// records NO pose with a note — an empty pose document would restore as a
    /// pose import that resets the companion to nothing.</para>
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
            if (state.Mode == GazeTargetMode.None)
                continue;

            Guid? target = null;
            if (state.Mode == GazeTargetMode.Entity)
            {
                var address = _gaze.GetGazeTargetAddress(actor);
                foreach (var (candidate, _) in captured)
                {
                    if (candidate.Address == address && address != nint.Zero)
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
                CenterRelative = centred,
                Node = overlay.State with
                {
                    Name = Bounded(overlay.State.Name, "Overlay"),
                    Position = centred
                        ? overlay.State.Position - center
                        : overlay.State.Position,
                },
            });
        }
    }

    /// <summary>
    /// The map's own objects the scene has BORROWED. Each is written by the
    /// identity that outlives the session — the model path, plus the point the
    /// MAP stands it at, which is the claim's captured placement and therefore
    /// does not move when the user drags the object — and beside that, the
    /// placement the user actually gave it.
    ///
    /// <para>The list is left ABSENT when nothing was borrowed, so a scene that
    /// borrowed nothing writes exactly the file it wrote before this existed.
    /// </para>
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
                VfxLoop = worldObject.LoopVfx,
                VfxSpeed = worldObject.VfxSpeed,
                VfxIntensity = worldObject.VfxIntensity,
                VfxPaused = worldObject.VfxPaused,
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

            var document = LightFileService.CreateLightFile(light);
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

            var document = CameraFileService.CreateCameraFile(camera);
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
            !ReferenceEquals(exact, camera.TargetActor) ||
            _bindings.GetActorId(exact) != targetId)
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
    internal SceneEnvironment CaptureEnvironment()
    {
        var environment = new SceneEnvironment
        {
            MinuteOfDay = Math.Clamp(_environment.MinuteOfDay, 0, 1439),
            DayOfMonth = Math.Clamp(_environment.DayOfMonth, 1, 31),
            IsTimeFrozen = _environment.IsTimeFrozen,
            WeatherId = _environment.CurrentWeatherId,
            WeatherName = _environment.GetWeatherInfo(
                _environment.CurrentWeatherId)?.Name ?? string.Empty,
            IsWeatherOverrideEnabled = _environment.IsWeatherOverrideEnabled,
            TransitionTime = float.IsFinite(_environment.TransitionTime) &&
                _environment.TransitionTime >= 0
                    ? _environment.TransitionTime
                    : 0.5f,
        };

        foreach (var section in Enum.GetValues<EnvSection>())
        {
            if (!_environment.IsSectionHeld(section))
                continue;
            environment.HeldSections.Add(section);
            switch (section)
            {
                case EnvSection.Sky:
                    environment.Sky = _environment.Sky;
                    break;
                case EnvSection.Clouds:
                    environment.Clouds = _environment.Clouds;
                    break;
                case EnvSection.Lighting:
                    environment.Lighting = _environment.Lighting;
                    break;
                case EnvSection.Fog:
                    environment.Fog = _environment.Fog;
                    break;
                case EnvSection.Rain:
                    environment.Rain = _environment.Rain;
                    break;
                case EnvSection.Particles:
                    environment.Particles = _environment.Particles;
                    break;
                case EnvSection.Stars:
                    environment.Stars = _environment.Stars;
                    break;
                case EnvSection.Wind:
                    environment.Wind = _environment.Wind;
                    break;
            }
        }

        return environment;
    }

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
