using Dalamud.Plugin.Services;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Game.Overlays;
using Poser.Application.Lifecycle;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Domain.Identity;

using Poser.Application.Viewport;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>Shared creation policy for sidebar, spawn search, and library.</summary>
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
    private readonly IParentingRuntime _transforms;
    private readonly IVirtualCameraService _cameras;
    private readonly ILightingService _lighting;
    private readonly IOverlayNodeService _overlays;
    private readonly Application.Integration.IntegrationSelectors _integration;
    private readonly IPluginLog _log;
    private readonly Application.Transforms.TransformParenting _parenting;

    public SceneCreation(IFramework framework, ISessionGenerationSource sessions,
        IActorManager actors, IActorSpawnService spawn, ISceneLifecycleHistory lifecycle,
        IEntityBindings bindings, ISkeletonService skeletons, IPlacementAnchorSource anchors, Application.Settings.ConfigurationService configuration, IParentingRuntime transforms, IVirtualCameraService cameras, ILightingService lighting, IOverlayNodeService overlays,
        Application.Integration.IntegrationSelectors integration, IPluginLog log,
        Application.Transforms.TransformParenting parenting)
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
        _transforms = transforms;
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
        // New entities have no saved anchor, so they land on the current one,
        // just like a file without a saved anchor.
        position = anchor;
        return true;
    }

    private SceneCreationResult Finish(SceneEntityKind kind, object? entity, Vector3? position,
        SelectionId? source = null)
    {
        var result = Track(kind, entity);
        if (result.Handle is not { } handle) return result;
        void Apply()
        {
            if (Resolve(handle) is not { } target) return;
            if (position is { } point)
            {
                if (entity is Cameras.VirtualCamera camera)
                {
                    if (camera.Kind == CameraKind.Free) camera.Position = point;
                    else if (camera.FixedPosition != null)
                        camera.FixedPosition = point - camera.PositionOffset - camera.TargetOffset;
                    else camera.PendingOrbitPosition = point;
                }
                else if (_transforms.Read(target) is not { } transform
                    || !_transforms.Write(target, transform with { Position = point }))
                    _log.Warning("The new entity could not be placed.");
            }
            if (source is { } original && !_parenting.Copy(original, target, preserveWorld: position != null))
                _log.Warning("The duplicated entity could not inherit its source's parent.");
        }
        // Native spawning sets the actor's position. Place it and copy its
        // attachment after that finishes, so the attachment uses the new position.
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
        var transforms = sources.ToDictionary(id => id, ReadTransform);
        var placed = transforms.Values.OfType<PoseTransform>().ToArray();
        Vector3? anchor = null;
        if (placed.Length > 0 && !TryPlacement(out anchor, out var refusal))
        {
            foreach (var source in sources) results[source] = new(null, refusal);
            return results;
        }
        var offset = anchor is { } point
            ? point - GroupTransformBaseline.Centroid(placed)
            : (Vector3?)null;
        // Capture the anchor once so creating a camera cannot move later copies.
        foreach (var source in sources)
            results[source] = DuplicateAt(source, withPose,
                offset is { } delta && transforms[source] is { } transform ? transform.Position + delta : null);
        return results;
    }

    private PoseTransform? ReadTransform(SelectionId source) => source.Camera is { } id
        ? _bindings.Resolve(id).Value is { } camera
            ? PoseTransform.Identity with { Position = camera.WorldPosition } : null
        : _transforms.Read(source);

    private SceneCreationResult DuplicateAt(SelectionId source, bool withPose, Vector3? position)
    {
        object? copy = source.Kind switch
        {
            SceneEntityKind.Actor when source.Actor is { } id && _bindings.Resolve(id).Value is { } actor =>
                DuplicateActor(actor, withPose, position),
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
        return Finish(source.Kind, copy, position, source);
    }

    private IActor? DuplicateActor(IActor source, bool withPose, Vector3? position)
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
                // Copy custom colours and opacity through their appearance provider.
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
        return _lifecycle.SpawnActorWithPose($"Duplicate actor '{name}' with pose", Clone, source, position);
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
