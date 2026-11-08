using Dalamud.Plugin.Services;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Game.Overlays;
using Poser.Application.Lifecycle;
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
    private readonly IPosingService _posing;
    private readonly IVirtualCameraService _cameras;
    private readonly ILightingService _lighting;
    private readonly IOverlayNodeService _overlays;
    private readonly Application.Integration.IntegrationSelectors _integration;
    private readonly IPluginLog _log;
    private readonly Application.Transforms.TransformParenting _parenting;

    public SceneCreation(IFramework framework, ISessionGenerationSource sessions,
        IActorManager actors, IActorSpawnService spawn, ISceneLifecycleHistory lifecycle,
        IEntityBindings bindings, ISkeletonService skeletons, IPlacementAnchorSource anchors, Application.Settings.ConfigurationService configuration, IPosingService posing, IVirtualCameraService cameras, ILightingService lighting, IOverlayNodeService overlays,
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
        _posing = posing;
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
        if (!CanCreate) return new(null, "Actor creation requires an active GPose session.");
        if (!TryPlacement(out var position, out var refusal)) return new(null, refusal);
        var catalog = request.Catalog;
        var actor = _lifecycle.SpawnActor(
            catalog is { } item ? $"Add {item.Name}" : request.ReserveCompanionSlot
                ? "Add actor with companion slot" : "Add actor",
            () => catalog is { } entry ? _spawn.SpawnCatalogActor(entry)
                : _spawn.SpawnNewActor(request.ReserveCompanionSlot, request.ModelCharaId),
            name: catalog?.Name);
        Place(actor, position);
        return Track(SceneEntityKind.Actor, actor);
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
        var entity = create();
        Place(entity, position);
        return Track(kind, entity);
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

    private void Place(object? entity, Vector3? position)
    {
        if (position is not { } point) return;
        switch (entity)
        {
            case IActor actor:
                // The spawn finishes positioning the native actor before this callback.
                _lifecycle.WhenPosable(actor, ready =>
                {
                    if (!_posing.SetTransformOverride(ready, _posing.GetEffectiveTransform(ready) with { Position = point }))
                        _log.Warning("The new actor could not be placed.");
                });
                break;
            case ILight light:
                light.Transform = light.Transform with { Position = point };
                break;
            case IPropHandle prop:
                prop.Transform = prop.Transform with { Position = point };
                break;
            case IWorldObject world:
                world.Transform = world.Transform with { Position = point };
                break;
            case Cameras.VirtualCamera camera:
                if (camera.Kind == CameraKind.Free) camera.Position = point;
                else if (camera.FixedPosition != null)
                    camera.FixedPosition = point - camera.PositionOffset - camera.TargetOffset;
                else camera.PendingOrbitPosition = point;
                break;
            case OverlayNodeHandle { State.Collider: { } collider } overlay:
                overlay.State = overlay.State with
                {
                    Collider = collider with { Transform = collider.Transform with { Position = point } },
                };
                break;
        }
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
        var positions = sources.ToDictionary(id => id, ReadPosition);
        var points = positions.Values.OfType<Vector3>().ToArray();
        Vector3? anchor = null;
        if (points.Length > 0 && !TryPlacement(out anchor, out var refusal))
        {
            foreach (var source in sources) results[source] = new(null, refusal);
            return results;
        }
        var offset = anchor is { } point && points.Length > 0
            ? point - points.Aggregate(Vector3.Zero, (sum, value) => sum + value) / points.Length
            : (Vector3?)null;
        // Capture the anchor once so creating a camera cannot move later copies.
        foreach (var source in sources)
            results[source] = DuplicateAt(source, withPose,
                offset is { } delta && positions[source] is { } position ? position + delta : null);
        return results;
    }

    private Vector3? ReadPosition(SelectionId source) => source switch
    {
        { Actor: { } id } when _bindings.Resolve(id).Value is { } actor => _posing.GetEffectiveTransform(actor).Position,
        { Light: { } id } => _bindings.Resolve(id).Value?.Transform.Position,
        { Camera: { } id } => _bindings.Resolve(id).Value?.WorldPosition,
        { Prop: { } id } => _bindings.Resolve(id).Value?.Transform.Position,
        { WorldObject: { } id } => _bindings.Resolve(id).Value?.Transform.Position,
        { Overlay: { } id } => _bindings.Resolve(id).Value?.State.Collider?.Transform.Position,
        _ => null,
    };

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
        Place(copy, position);
        var result = Track(source.Kind, copy);
        if (result.Handle is { } handle && Resolve(handle) is { } target && !_parenting.Copy(source, target, preserveWorld: position != null))
            _log.Warning("The duplicated entity could not inherit its source's parent.");
        return result;
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
                // Native customize bytes contain palette indices, not custom
                // shader colours/opacity. Copy those through their provider,
                // without reapplying equipment or requesting another redraw.
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
