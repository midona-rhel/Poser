using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The production <see cref="ISceneRuntime"/>: thin bindings from the scene
/// transaction's phase vocabulary onto the real owners — the accepted spawn
/// service, the ONE atomic pose import, the lighting/camera/environment
/// services, and the scene codec/store. It owns no transaction state; every
/// method is one materialization step.
/// </summary>
internal sealed class SceneRuntimeAdapter : ISceneRuntime
{
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private readonly SceneCaptureService _capture;
    private readonly SceneFileStore _store;
    private readonly CleanPoseFacade _poses;
    private readonly IActorSpawnService _spawns;
    private readonly ISkeletonService _skeletons;
    private readonly IPosingService _posing;
    private readonly PropSpawnService _props;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IEnvironmentService _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly AnimationSession _animation;
    private readonly IGazeService _gaze;

    public SceneRuntimeAdapter(
        IFramework framework,
        ISessionGenerationSource sessions,
        SceneCaptureService capture,
        CleanPoseFacade poses,
        IActorSpawnService spawns,
        ISkeletonService skeletons,
        IPosingService posing,
        PropSpawnService props,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IEnvironmentService environment,
        StableBindingRegistry bindings,
        AnimationSession animation,
        IGazeService gaze)
    {
        _bindings = bindings;
        _animation = animation;
        _gaze = gaze;
        _framework = framework;
        _sessions = sessions;
        _capture = capture;
        _store = SceneFileStore.Default;
        _poses = poses;
        _spawns = spawns;
        _skeletons = skeletons;
        _posing = posing;
        _props = props;
        _lighting = lighting;
        _cameras = cameras;
        _environment = environment;
    }

    public SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    public Task<T> OnFramework<T>(Func<T> func) =>
        _framework.RunOnFrameworkThread(func);

    public SceneReadOutcome ReadScene(string path) => _store.Read(path);

    public SceneWriteOutcome WriteScene(SceneFile scene, string path) =>
        _store.Write(scene, path);

    public string? ArmSceneCapture(
        Guid sceneId,
        string? description,
        Action<SceneCaptureOutcome> onCaptured) =>
        _capture.BeginCapture(sceneId, description, onCaptured);

    // ── actors ───────────────────────────────────────────────────────────

    public object? SpawnActor(SceneActor data, out string? detail)
    {
        var actor = _spawns.SpawnNewActor(data.HasCompanionSlot);
        if (actor is null)
        {
            detail = "The spawn service returned no actor.";
            return null;
        }
        if (data.ModelCharaId != 0)
            _spawns.SetModelCharaId(actor, data.ModelCharaId);
        detail = null;
        return actor;
    }

    public bool ActorReady(object actor) =>
        _skeletons.GetSkeletons((IActor)actor).Count > 0;

    // Only called for an actor whose attachment is present: the workflow skips
    // an absent kind rather than asking the runtime to detach.
    public string? AttachCompanion(object actor, SceneActor data) =>
        _spawns.SetCompanion(
            (IActor)actor,
            new CompanionAttachment(data.CompanionKind!.Value, data.CompanionId))
            ? null
            : "The companion could not be attached.";

    public string? ArmPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        // Every component and every slot: the embedded pose is the actor's
        // complete captured state, not an interactive rotation-only import.
        // Placement is absolute and separate (PlaceActor), so the
        // difference-based model transform stays off.
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = true,
            ApplyModelTransform = false,
        };
        var result = _poses.ImportPose(
            (IActor)actor, data.Pose!, options, description, onReceipt);
        return result.Success ? null : result.Detail ?? "The pose import refused.";
    }

    public string? PlaceActor(object actor, SceneActor data)
    {
        // The scene's OWN placement first. The embedded pose's absolute values
        // remain the fallback for files written before placements were stated,
        // and only there does the codec's unset marker (BoneData.Identity —
        // zero position, identity rotation, ZERO scale) have to be guessed at.
        System.Numerics.Vector3 position;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 scale;
        if (data.ModelTransform is { } stated)
        {
            position = stated.Position;
            rotation = stated.Rotation;
            scale = stated.Scale;
        }
        else
        {
            var absolute = data.Pose!.ModelAbsoluteValues;
            bool unset = absolute.Position == System.Numerics.Vector3.Zero &&
                absolute.Rotation == System.Numerics.Quaternion.Identity &&
                absolute.Scale == System.Numerics.Vector3.Zero;
            if (unset)
                return null;
            position = absolute.Position;
            rotation = absolute.Rotation;
            scale = absolute.Scale;
        }

        if (rotation.LengthSquared() < SceneFileLimits.MinQuaternionLengthSquared)
            return "The saved actor placement carries a degenerate rotation.";

        var placement = new Transform(
            position,
            System.Numerics.Quaternion.Normalize(rotation),
            scale == System.Numerics.Vector3.Zero
                ? System.Numerics.Vector3.One
                : scale);
        var target = (IActor)actor;
        _posing.SetTransformOverride(target, placement);

        // The override setter REFUSES silently — outside GPose, on an actor
        // the live set does not yet carry, on a value it cannot sanitize —
        // and a scene reporting a placement it never made is exactly the
        // failure the user sees as "it did not restore where they stood".
        // Ask whether it landed.
        if (_posing.GetTransformOverride(target) is null)
            return "The actor's placement was refused by the transform owner.";
        return null;
    }

    /// <summary>
    /// Replays the saved animation in the one order that survives its own
    /// dependencies: stance and weapon FIRST (a stance transition cancels the
    /// container's timelines, so anything played before it would be taken
    /// down), then the base timeline, then the held expression (which pins the
    /// facial layer itself), then lips, then the explicit slot pins and armed
    /// loops, then the overall speed LAST — a pause written before the plays
    /// would be lifted by the very sequencer calls that follow it.
    /// </summary>
    public string? ApplyActorAnimation(object actor, SceneActor data)
    {
        if (data.Animation is not { } saved)
            return null;
        if (_bindings.GetActorId((IActor)actor) is not { } id)
            return "The actor has no stable identity to own animation state.";

        var failures = new List<string>();
        void Try(AnimationResult result)
        {
            if (!result.Success && result.Detail is { } detail)
                failures.Add(detail);
        }

        if (saved.WeaponDrawn)
            Try(_animation.SetWeaponDrawn(id, true));
        if (_animation.SupportsStance &&
            (saved.Stance != AnimationStance.Idle || saved.Pose != 0))
            Try(_animation.SetStance(id, saved.Stance, saved.Pose));

        if (saved.BaseTimeline != 0)
            Try(_animation.PlayBase(id, saved.BaseTimeline));
        if (saved.HeldExpression != 0)
            Try(_animation.HoldExpression(id, saved.HeldExpression));
        if (saved.Lips != 0)
            Try(_animation.SetLips(id, saved.Lips));

        foreach (var slot in saved.Slots)
        {
            if (slot.Loop != 0)
                Try(_animation.SetSlotLoop(id, slot.Slot, slot.Loop, true));
            // The facial pin belongs to the held expression and is re-applied
            // by it; re-writing it here would double the ownership.
            if (slot.Speed is { } speed &&
                !(slot.Slot == AnimationSlot.Facial && saved.HeldExpression != 0))
                Try(_animation.SetSlotSpeed(id, slot.Slot, speed));
        }

        if (saved.PositionLock)
            Try(_animation.SetPositionLock(id, true));
        if (saved.Speed != 1f)
            Try(_animation.SetSpeed(id, saved.Speed));

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>
    /// Restores the saved gaze in the order the service's own transitions
    /// require: the mode first (entering a mode with no parts enables all
    /// three), then the exact participation mask, then the anchor and each
    /// part's own point, then the locks — a lock freezes a part at the target
    /// it currently holds, so it must land after that target is written.
    /// </summary>
    public string? ApplyActorGaze(object actor, SceneActor data, object? target)
    {
        if (data.Gaze is not { } saved || saved.Mode == GazeTargetMode.None)
            return null;
        if (!_gaze.IsAvailable)
            return _gaze.UnavailableDetail ?? "Gaze control is unavailable.";

        var source = (IActor)actor;

        // Entity mode IS its target: SetGazeTarget both chooses the actor and
        // enters the mode. A saved Entity gaze whose target the file does not
        // name has nothing to follow, and is refused by name rather than left
        // pointing at whatever the mode transition would pick.
        if (saved.Mode == GazeTargetMode.Entity)
        {
            if (target is not IActor followed)
                return "The saved gaze followed an actor the scene does not carry.";
            var chosen = _gaze.SetGazeTarget(source, followed);
            if (!chosen.Success)
                return chosen.Detail ?? "The gaze target was refused.";
        }
        else
        {
            var mode = _gaze.SetGazeMode(source, saved.Mode);
            if (!mode.Success)
                return mode.Detail ?? "The gaze mode was refused.";
        }

        var parts = _gaze.SetGazeParts(source, saved.Parts);
        if (!parts.Success)
            return parts.Detail ?? "The gaze parts were refused.";

        if (saved.Mode == GazeTargetMode.Position)
        {
            _gaze.SetGazePosition(source, saved.Position);
            _gaze.SetPartPosition(source, GazeTargetType.Eyes, saved.EyesPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Head, saved.HeadPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Body, saved.BodyPosition);
        }

        foreach (var part in new[]
                 {
                     GazeTargetType.Body, GazeTargetType.Head, GazeTargetType.Eyes,
                 })
        {
            if (saved.LockedParts.HasFlag(part))
                _gaze.SetPartLock(source, part, true);
        }
        return null;
    }

    public void SetActorVisibility(object actor, bool visible) =>
        _spawns.SetVisibility((IActor)actor, visible);

    // ── props ────────────────────────────────────────────────────────────

    public object? SpawnProp(SceneProp data, out string? detail)
    {
        var handle = _props.SpawnProp(new PropModel(
            data.Name, data.Model, data.Submodel, data.Variant, string.Empty));
        if (handle is null)
        {
            detail = "The prop spawn failed.";
            return null;
        }
        handle.Transform = data.Transform;
        handle.Visible = data.Visible;
        detail = null;
        return handle;
    }

    // ── lights ───────────────────────────────────────────────────────────

    public object? SpawnLight(
        SceneLight data, object? attachmentOwner, out string? detail)
    {
        var document = data.Light!;

        // Resolve the exact attachment bone BEFORE any native spawn — a
        // light whose stated parent bone is missing is refused whole, never
        // spawned detached into world space.
        IBone? bone = null;
        if (data.Attachment is { } attachment)
        {
            if (attachmentOwner is not IActor owner)
            {
                detail = "The attachment owner was not restored.";
                return null;
            }
            var skeleton = _skeletons.GetSkeletons(owner)
                .FirstOrDefault(candidate => candidate.Slot == attachment.Slot);
            bone = skeleton?.Bones.FirstOrDefault(candidate =>
                candidate.PartialId == attachment.PartialId &&
                string.Equals(
                    candidate.BoneName, attachment.BoneName,
                    StringComparison.Ordinal));
            if (bone is null)
            {
                detail = $"The attachment bone '{attachment.BoneName}' " +
                    $"({attachment.Slot}/{attachment.PartialId}) does not exist " +
                    "on the restored actor.";
                return null;
            }
        }

        var light = _lighting.SpawnLight(document.Kind);
        if (light is null)
        {
            detail = "The light spawn failed.";
            return null;
        }

        LightFileService.Apply(document, light);
        if (bone is not null)
            light.AttachedBone = bone;

        // A gobo the running client no longer ships degrades with a named
        // detail; the light itself is restored.
        detail = null;
        if (!string.IsNullOrEmpty(document.Gobo))
        {
            var gobo = _lighting.Gobos.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Path, document.Gobo,
                    StringComparison.OrdinalIgnoreCase));
            if (gobo == default)
                detail = $"The saved gobo '{document.Gobo}' is not in the library.";
            else if (!_lighting.ApplyGobo(light, gobo))
                detail = $"The gobo '{gobo.Name}' could not be applied.";
        }
        return light;
    }

    // ── cameras ──────────────────────────────────────────────────────────

    private IVirtualCamera? DefaultCamera =>
        _cameras.Cameras.FirstOrDefault(camera => camera.IsDefault);

    public CameraFile CaptureDefaultCameraState() =>
        DefaultCamera is { } camera
            ? CameraFileService.CreateCameraFile(camera)
            : new CameraFile();

    public string? ApplyDefaultCamera(SceneCamera data)
    {
        if (DefaultCamera is not { } camera)
            return "The session has no default camera.";
        CameraFileService.Apply(data.Camera!, camera);
        return null;
    }

    public object? CreateCamera(SceneCamera data, out string? detail)
    {
        var camera = _cameras.CreateCamera(data.Camera!.Kind);
        if (camera is null)
        {
            detail = "The camera could not be created.";
            return null;
        }
        CameraFileService.Apply(data.Camera!, camera);
        detail = null;
        return camera;
    }

    public string? SetCameraTarget(
        object? camera, object targetActor, string displayName)
    {
        var target = camera as IVirtualCamera ?? DefaultCamera;
        if (target is null)
            return "The session has no default camera.";
        return _cameras.SetTargetActor(target, (IActor)targetActor, displayName)
            ? null
            : "The target actor has no draw object.";
    }

    public string? SetLiveCamera(object? camera)
    {
        var target = camera as IVirtualCamera ?? DefaultCamera;
        if (target is null)
            return "The session has no default camera.";
        _cameras.SetLive(target);
        return null;
    }

    public void RestoreDefaultCamera(CameraFile baseline)
    {
        if (DefaultCamera is { } camera)
            CameraFileService.Apply(baseline, camera);
    }

    // ── environment ──────────────────────────────────────────────────────

    public SceneEnvironment CaptureEnvironmentState() =>
        _capture.CaptureEnvironment();

    public void ApplyEnvironment(SceneEnvironment target)
    {
        // Writing the clock forces the freeze on; releasing it afterwards is
        // the deliberate order for a scene saved with a running clock.
        _environment.MinuteOfDay = target.MinuteOfDay;
        _environment.DayOfMonth = target.DayOfMonth;
        _environment.IsTimeFrozen = target.IsTimeFrozen;

        _environment.TransitionTime = target.TransitionTime;
        if (target.IsWeatherOverrideEnabled)
            _environment.SetWeather(target.WeatherId, target.TransitionTime);
        else
            _environment.IsWeatherOverrideEnabled = false;

        // Stamp all eight sections: a held section takes its saved values
        // (the setters imply the hold), an unheld one releases to the game.
        foreach (var section in Enum.GetValues<EnvSection>())
        {
            bool held = target.HeldSections.Contains(section);
            if (!held)
            {
                _environment.SetSectionHeld(section, false);
                continue;
            }
            switch (section)
            {
                case EnvSection.Sky when target.Sky is { } sky:
                    _environment.Sky = sky;
                    break;
                case EnvSection.Clouds when target.Clouds is { } clouds:
                    _environment.Clouds = clouds;
                    break;
                case EnvSection.Lighting when target.Lighting is { } lighting:
                    _environment.Lighting = lighting;
                    break;
                case EnvSection.Fog when target.Fog is { } fog:
                    _environment.Fog = fog;
                    break;
                case EnvSection.Rain when target.Rain is { } rain:
                    _environment.Rain = rain;
                    break;
                case EnvSection.Particles when target.Particles is { } particles:
                    _environment.Particles = particles;
                    break;
                case EnvSection.Stars when target.Stars is { } stars:
                    _environment.Stars = stars;
                    break;
                case EnvSection.Wind when target.Wind is { } wind:
                    _environment.Wind = wind;
                    break;
            }
        }
    }

    // ── rollback ─────────────────────────────────────────────────────────

    public void DestroyActor(object actor) => _spawns.DestroyActor((IActor)actor);

    public void DestroyProp(object prop) => _props.Destroy((PropHandle)prop);

    public void DestroyLight(object light) => _lighting.DestroyLight((ILight)light);

    public void DestroyCamera(object camera) =>
        _cameras.DestroyCamera((IVirtualCamera)camera);
}
