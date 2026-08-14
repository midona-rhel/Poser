using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Cameras;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>Typed result of one whole-shot capture. Notes are per-entity
/// observations about state the capture could not represent (an actor with
/// no skeleton, a camera target that no longer resolves) — they are part of
/// the read model, never silently dropped facts.</summary>
public sealed class SceneCaptureOutcome
{
    public bool Success { get; }
    public string? Detail { get; }
    public SceneFile? Scene { get; }
    public IReadOnlyList<string> Notes { get; }

    private SceneCaptureOutcome(
        bool success, string? detail, SceneFile? scene, IReadOnlyList<string> notes)
    {
        Success = success;
        Detail = detail;
        Scene = scene;
        Notes = notes;
    }

    internal static SceneCaptureOutcome Ok(SceneFile scene, List<string> notes) =>
        new(true, null, scene, notes.AsReadOnly());

    internal static SceneCaptureOutcome Fail(string detail) =>
        new(false, detail, null, Array.Empty<string>());
}

/// <summary>
/// Read-only, pointer-free whole-shot capture. Runs synchronously on the
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
/// </summary>
public sealed class SceneCaptureService
{
    private readonly IFramework _framework;
    private readonly IActorManager _actors;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly IActorSpawnService _spawns;
    private readonly PropSpawnService _props;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IEnvironmentService _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly CleanPoseFacade _poses;

    public SceneCaptureService(
        IFramework framework,
        IActorManager actors,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        IActorSpawnService spawns,
        PropSpawnService props,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IEnvironmentService environment,
        StableBindingRegistry bindings,
        CleanPoseFacade poses)
    {
        _framework = framework;
        _actors = actors;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _spawns = spawns;
        _props = props;
        _lighting = lighting;
        _cameras = cameras;
        _environment = environment;
        _bindings = bindings;
        _poses = poses;
    }

    /// <summary>Captures the current shot. Framework thread only; the
    /// workflow owns the marshal. <paramref name="sceneId"/> keeps a
    /// re-saved scene's identity stable across saves.</summary>
    public SceneCaptureOutcome Capture(Guid sceneId, string? description)
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

            var actorKeys = CaptureActors(scene, notes);
            CaptureProps(scene, notes);
            CaptureLights(scene, actorKeys, notes);
            CaptureCameras(scene, actorKeys, notes);
            scene.Environment = CaptureEnvironment();

            var validation = SceneFileValidation.Validate(scene);
            if (!validation.Succeeded)
                return SceneCaptureOutcome.Fail(
                    $"The captured scene did not validate: {validation.Failure!.Detail}");

            return SceneCaptureOutcome.Ok(scene, notes);
        }
        catch (Exception ex)
        {
            return SceneCaptureOutcome.Fail(
                $"Scene capture failed unexpectedly: {ex.Message}");
        }
    }

    private Dictionary<IActor, Guid> CaptureActors(
        SceneFile scene, List<string> notes)
    {
        var keys = new Dictionary<IActor, Guid>();
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
            var key = _bindings.GetActorId(actor)?.LogicalId ?? Guid.NewGuid();
            keys[actor] = key;
            scene.Actors.Add(new SceneActor
            {
                Key = key,
                Name = Bounded(actor.Name, $"Actor {key:N}"),
                ModelCharaId = Math.Max(0, _spawns.GetModelCharaId(actor)),
                Visible = _spawns.IsVisible(actor),
                // A live attachment proves the slot exists even when the
                // actor was not spawned by Poser with an explicit reservation.
                HasCompanionSlot = _spawns.HasCompanionSlot(actor) ||
                    companion.Kind != Types.CompanionKind.None,
                CompanionKind = companion.Kind,
                CompanionId = companion.Kind == Types.CompanionKind.None
                    ? (ushort)0
                    : companion.Id,
                Pose = pose,
            });
        }

        return keys;
    }

    private void CaptureProps(SceneFile scene, List<string> notes)
    {
        foreach (var prop in _props.Props)
        {
            if (!prop.IsValid)
            {
                notes.Add($"Prop '{prop.Name}' is no longer valid and was not captured.");
                continue;
            }

            scene.Props.Add(new SceneProp
            {
                Key = _bindings.GetPropId(prop)?.LogicalId ?? Guid.NewGuid(),
                Name = Bounded(prop.Name, "Prop"),
                Model = prop.Model.Model,
                Submodel = prop.Model.Submodel,
                Variant = prop.Model.Variant,
                Visible = prop.Visible,
                Transform = NormalizedTransform(
                    prop.Transform, $"Prop '{prop.Name}'", notes),
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
            if (!string.IsNullOrEmpty(camera.TargetActorName))
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
            });
        }
    }

    /// <summary>Resolves the followed actor: the retained exact reference
    /// first, then a unique display-name match for cameras that predate the
    /// retention. Ambiguity resolves to nothing rather than guessing.</summary>
    private static Guid? ResolveTarget(
        IVirtualCamera camera, Dictionary<IActor, Guid> actorKeys)
    {
        if (camera is VirtualCamera { TargetActor: { } exact } &&
            actorKeys.TryGetValue(exact, out var key))
            return key;

        var matches = actorKeys
            .Where(pair => string.Equals(
                pair.Key.Name, camera.TargetActorName, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private SceneEnvironment CaptureEnvironment()
    {
        var environment = new SceneEnvironment
        {
            MinuteOfDay = Math.Clamp(_environment.MinuteOfDay, 0, 1439),
            DayOfMonth = Math.Clamp(_environment.DayOfMonth, 1, 31),
            IsTimeFrozen = _environment.IsTimeFrozen,
            WeatherId = _environment.CurrentWeatherId,
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
