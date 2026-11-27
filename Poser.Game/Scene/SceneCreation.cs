using Dalamud.Plugin.Services;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Game.Overlays;
using Poser.Application.Lifecycle;
using Poser.Game.Transforms;
using Poser.Application.Scene;
using Poser.Domain.Identity;

using Poser.Application.Viewport;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>Adds new things to the scene, and duplicates, the same way whether they come
/// from the sidebar, the spawn search or the library.</summary>
public sealed class SceneCreation : ISceneCreation
{
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private readonly IActorManager _actors;
    private readonly IActorSpawnService _spawn;
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly IEntityBindings _bindings;
    private readonly ISkeletonService _skeletons;
    private readonly SceneRuntimeHandles _handles;
    private readonly IPlacementAnchorSource _anchors;
    private readonly Application.Settings.ConfigurationService _configuration;
    private readonly IVirtualCameraService _cameras;
    private readonly ILightingService _lighting;
    private readonly IOverlayNodeService _overlays;
    private readonly Application.Integration.IntegrationSelectors _integration;
    private readonly IPluginLog _log;
    private readonly Transforms.TransformParenting _parenting;

    public SceneCreation(IFramework framework, ISessionGenerationSource sessions,
        IActorManager actors, IActorSpawnService spawn, ISceneLifecycleHistory lifecycle,
        IEntityBindings bindings, ISkeletonService skeletons, IPlacementAnchorSource anchors, Application.Settings.ConfigurationService configuration, IVirtualCameraService cameras, ILightingService lighting, IOverlayNodeService overlays,
        Application.Integration.IntegrationSelectors integration, IPluginLog log,
        Transforms.TransformParenting parenting)
    {
        _framework = framework;
        _sessions = sessions;
        _actors = actors;
        _spawn = spawn;
        _lifecycle = lifecycle;
        _bindings = bindings;
        _skeletons = skeletons;
        _anchors = anchors;
        _configuration = configuration;
        _cameras = cameras;
        _lighting = lighting;
        _overlays = overlays;
        _integration = integration;
        _log = log;
        _parenting = parenting;
        _handles = new(() => sessions.ActiveSessionGeneration);
    }

    private bool CanCreate => _framework.IsInFrameworkUpdateThread && _sessions.ActiveSessionGeneration.HasValue;

    public bool IsAvailable(SceneEntityKind kind) => kind switch
    {
        SceneEntityKind.Light => _lighting.IsAvailable,
        SceneEntityKind.Camera => _cameras.IsAvailable,
        SceneEntityKind.Overlay => _overlays.IsAvailable,
        _ => CanCreate,
    };

    public SceneCreationResult CreateActor(ActorCreationRequest request)
    {
        var catalog = request.Catalog;
        return Create(SceneEntityKind.Actor, () => _lifecycle.SpawnActor(
            catalog is { } item ? $"Add {item.Name}" : request.ReserveCompanionSlot
                ? "Add actor with companion slot" : "Add actor",
            () => catalog is { } entry ? _spawn.SpawnCatalogActor(entry)
                : _spawn.SpawnNewActor(request.ReserveCompanionSlot, request.ModelCharaId),
            name: catalog?.Name));
    }

    public SceneCreationResult CreateLight(LightKind kind) =>
        Create(SceneEntityKind.Light, () => _lifecycle.SpawnLight(kind));

    public SceneCreationResult CreateCamera(CameraKind kind) =>
        Create(SceneEntityKind.Camera, () => _lifecycle.CreateCamera(kind));

    public SceneCreationResult CreateProp(PropModel? model = null) =>
        Create(SceneEntityKind.Prop, () => model is { } value
            ? _lifecycle.SpawnProp(value) : _lifecycle.SpawnProp());

    public SceneCreationResult CreateOverlay(OverlayNodeKind kind) =>
        Create(SceneEntityKind.Overlay, () => _lifecycle.SpawnOverlay(OverlayNodeService.DefaultState(kind)), spatial: false);

    public SceneCreationResult CreateCollider(IkColliderShape shape) =>
        Create(SceneEntityKind.Overlay, () => _lifecycle.SpawnOverlay(new OverlayNodeState
        {
            Kind = OverlayNodeKind.Collider, Alpha = .2f,
            Collider = new IkCollider
            {
                Shape = shape,
                Transform = PoseTransform.Identity with
                {
                    Scale = shape == IkColliderShape.Capsule ? new Vector3(.5f, 1, .5f) : Vector3.One,
                },
            },
        }), unsavedDefault: ObjectPlacementMode.InFrontOfCamera);

    public SceneCreationResult CreateWorldObject(string path) =>
        Create(SceneEntityKind.WorldObject, () => _lifecycle.SpawnWorldObject(path, Transform.Identity, true));

    private SceneCreationResult Create(SceneEntityKind kind, Func<object?> create, bool spatial = true, ObjectPlacementMode? unsavedDefault = null)
    {
        if (!CanCreate) return new(null, "Creation requires an active GPose session.");
        Vector3? position = null;
        if (spatial && !TryPlacement(out position, out var refusal, unsavedDefault)) return new(null, refusal);
        return Finish(kind, create(), position);
    }

    private bool TryPlacement(out Vector3? position, out string? refusal, ObjectPlacementMode? unsavedDefault = null)
    {
        position = null;
        refusal = null;
        var mode = _configuration.Config.DefaultSpawnPlacement;
        if (mode == ObjectPlacementMode.AsSaved)
        {
            if (unsavedDefault is not { } fallback) return true;
            mode = fallback;
        }
        if (!_anchors.TryCurrentFor(mode, out var anchor, out _, out refusal)) return false;
        // Something new has no saved position, so it goes at the current
        // camera or actor, as a library file with no saved position does.
        position = anchor;
        return true;
    }

    /// <summary>Places a new entity at <paramref name="position"/>. A duplicate
    /// instead stands exactly where its source stands: it gets the source's
    /// place, rotation and scale, and the same parent with the same offset.</summary>
    private SceneCreationResult Finish(SceneEntityKind kind, object? entity, Vector3? position,
        SelectionId? source = null)
    {
        var result = Track(kind, entity);
        if (result.Handle is not { } handle) return result;
        // Read now: by the time an actor can be placed, its source may have moved.
        var sourcePlace = source is { Camera: null } original ? _parenting.ReadWorld(original) : null;
        void Apply()
        {
            if (sourcePlace is { } place && !_parenting.WriteEntityWorld(entity, place))
                _log.Warning("The duplicate could not be put where its source is.");
            if (position is { } point)
            {
                if (entity is Cameras.VirtualCamera camera)
                {
                    if (camera.Kind == CameraKind.Free) camera.Position = point;
                    else if (camera.FixedPosition != null)
                        camera.FixedPosition = point - camera.PositionOffset - camera.TargetOffset;
                    else camera.PendingOrbitPosition = point;
                }
                else if (_parenting.ReadEntityWorld(entity) is not { } transform
                    || !_parenting.WriteEntityWorld(entity, transform with { Position = point }))
                    _log.Warning("The new entity could not be placed.");
            }
            if (source is { } copied && Resolve(handle) is { } target && !_parenting.Copy(copied, target))
                _log.Warning("The duplicated entity could not inherit its source's parent.");
        }
        // The game sets a new actor's position while spawning it. Place it and
        // copy its parent after that, so the parent link uses the new position.
        if (entity is IActor actor) _lifecycle.WhenPosable(actor, _ => Apply());
        else Apply();
        return result;
    }

    public SceneCreationResult Duplicate(SelectionId source, bool withPose = false) =>
        DuplicateMany([source], withPose)[source];

    public IReadOnlyDictionary<SelectionId, SceneCreationResult> DuplicateMany(
        IReadOnlyList<SelectionId> sources, bool withPose = false)
    {
        var results = new Dictionary<SelectionId, SceneCreationResult>();
        if (!CanCreate)
        {
            foreach (var source in sources) results[source] = new(null, "Duplication requires an active GPose session.");
            return results;
        }
        foreach (var source in sources)
            results[source] = DuplicateInPlace(source, withPose);
        return results;
    }

    private SceneCreationResult DuplicateInPlace(SelectionId source, bool withPose)
    {
        object? copy = source.Kind switch
        {
            SceneEntityKind.Actor when source.Actor is { } id && _bindings.Resolve(id).Value is { } actor =>
                DuplicateActor(actor, withPose),
            SceneEntityKind.Light when source.Light is { } id && _bindings.Resolve(id).Value is { } light =>
                _lifecycle.CloneLight(light),
            SceneEntityKind.Camera when source.Camera is { } id && _bindings.Resolve(id).Value is { } camera =>
                _lifecycle.CloneCamera(camera),
            SceneEntityKind.Prop when source.Prop is { } id && _bindings.Resolve(id).Value is { } prop =>
                _lifecycle.CloneProp(prop),
            SceneEntityKind.Overlay when source.Overlay is { } id && _bindings.Resolve(id).Value is { } overlay =>
                _lifecycle.CloneOverlay(overlay),
            SceneEntityKind.WorldObject when source.WorldObject is { } id && _bindings.Resolve(id).Value is { } world
                && world.Path.Contains('/') => _lifecycle.CloneWorldObject(world),
            _ => null,
        };
        return Finish(source.Kind, copy, null, source);
    }

    private IActor? DuplicateActor(IActor source, bool withPose)
    {
        string? parameters = null;
        if (_bindings.GetActorId(source) is { } sourceId
            && _integration.GetStateJson(sourceId) is { Success: true, Value: { } appearance })
        {
            try
            {
                var request = Documents.Appearance.CustomizeRequest.ParametersOnly(
                    Newtonsoft.Json.Linq.JObject.Parse(appearance));
                parameters = request.Value?.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Newtonsoft.Json.JsonException exception)
            {
                _log.Warning(exception, "Could not capture duplicate appearance parameters.");
            }
        }
        IActor? Clone()
        {
            var clone = _spawn.CloneActor(source);
            if (clone is null) return null;
            _lifecycle.WhenPosable(clone, target =>
            {
                if (!_actors.IsAvailable(source) || !_actors.IsAvailable(target)) return;
                _spawn.CopyDrawnAppearance(source, target);
                _spawn.CopyEquipmentVisibility(source, target);
                // Copy custom colours and opacity through Glamourer.
                if (parameters != null && _bindings.GetActorId(target) is { } targetId)
                {
                    var applied = _integration.ApplyStateJson(targetId, parameters);
                    if (!applied.Success) _log.Warning($"Duplicate appearance parameters: {applied.Detail}");
                }
            });
            return clone;
        }
        string name = Application.Settings.ConfigurationService.StripObjectIndex(source.Name);
        if (!withPose || _skeletons.GetSkeleton(source) is null)
            return _lifecycle.SpawnActor($"Duplicate actor '{name}'", Clone, source: source);
        return _lifecycle.SpawnActorWithPose($"Duplicate actor '{name}' with pose", Clone, source);
    }

    private SceneCreationResult Track(SceneEntityKind kind, object? entity) => entity is null
        ? new(null, "The entity could not be created.") : new(_handles.Track(kind, entity));

    public SelectionId? Resolve(SceneEntityHandle handle, bool requirePose = false)
    {
        if (!_framework.IsInFrameworkUpdateThread) return null;
        return _handles.Resolve(handle) switch
        {
            IActor actor when _actors.IsAvailable(actor) && (!requirePose || _skeletons.GetSkeleton(actor) is not null)
                && _bindings.GetActorId(actor) is { } id => SelectionId.ForActor(id),
            ILight light when light.IsValid && _bindings.GetLightId(light) is { } id => SelectionId.ForLight(id),
            IVirtualCamera camera when camera.IsValid && _bindings.GetCameraId(camera) is { } id => SelectionId.ForCamera(id),
            IPropHandle prop when prop.IsValid && _bindings.GetPropId(prop) is { } id => SelectionId.ForProp(id),
            IOverlayNode overlay when overlay.IsValid && _bindings.GetOverlayId(overlay) is { } id => SelectionId.ForOverlay(id),
            IWorldObject world when world.IsValid && _bindings.GetWorldObjectId(world) is { } id => SelectionId.ForWorldObject(id),
            _ => null,
        };
    }
}
