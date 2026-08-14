using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
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
        IEnvironmentService environment)
    {
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

    public SceneCaptureOutcome CaptureScene(Guid sceneId, string? description) =>
        _capture.Capture(sceneId, description);

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
        var absolute = data.Pose!.ModelAbsoluteValues;
        // BoneData.Identity (zero position, identity rotation, ZERO scale)
        // is the codec's unset marker — a real capture always carries the
        // actor's true scale.
        bool unset = absolute.Position == System.Numerics.Vector3.Zero &&
            absolute.Rotation == System.Numerics.Quaternion.Identity &&
            absolute.Scale == System.Numerics.Vector3.Zero;
        if (unset)
            return null;
        if (absolute.Rotation.LengthSquared() <
            SceneFileLimits.MinQuaternionLengthSquared)
            return "The saved actor placement carries a degenerate rotation.";

        _posing.SetTransformOverride((IActor)actor, new Transform(
            absolute.Position,
            System.Numerics.Quaternion.Normalize(absolute.Rotation),
            absolute.Scale == System.Numerics.Vector3.Zero
                ? System.Numerics.Vector3.One
                : absolute.Scale));
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
        // the deliberate order for a shot saved with a running clock.
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
