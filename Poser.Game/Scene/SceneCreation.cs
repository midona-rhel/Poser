using Dalamud.Plugin.Services;
using System.Numerics;
using Poser.Domain.Scene;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;
using Poser.Game.Overlays;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

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
    private readonly AnimationSession _animation;
    private readonly SceneRuntimeHandles _handles;
    private readonly ICameraService _camera;

    public SceneCreation(IFramework framework, ISessionGenerationSource sessions,
        IActorManager actors, IActorSpawnService spawn, ISceneLifecycleHistory lifecycle,
        IEntityBindings bindings, ISkeletonService skeletons, AnimationSession animation, ICameraService camera)
    {
        _framework = framework;
        _sessions = sessions;
        _actors = actors;
        _spawn = spawn;
        _lifecycle = lifecycle;
        _bindings = bindings;
        _skeletons = skeletons;
        _animation = animation;
        _camera = camera;
        _handles = new(() => sessions.ActiveSessionGeneration);
    }

    private bool CanCreate => _framework.IsInFrameworkUpdateThread && _sessions.ActiveSessionGeneration.HasValue;

    public SceneCreationResult CreateActor(ActorCreationRequest request)
    {
        if (!CanCreate) return new(null, "Actor creation requires an active GPose session.");
        var catalog = request.Catalog;
        var actor = _lifecycle.SpawnActor(
            catalog is { } item ? $"Add {item.Name}" : request.ReserveCompanionSlot
                ? "Add actor with companion slot" : "Add actor",
            () => catalog is { } entry ? _spawn.SpawnCatalogActor(entry)
                : _spawn.SpawnNewActor(request.ReserveCompanionSlot, request.ModelCharaId),
            name: catalog?.Name);
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
        Create(SceneEntityKind.Overlay, () => _lifecycle.SpawnOverlay(OverlayNodeService.DefaultState(kind)));

    public SceneCreationResult CreateCollider(IkColliderShape shape) =>
        Create(SceneEntityKind.Overlay, () =>
        {
            Matrix4x4.Invert(_camera.GetViewMatrix(), out var view);
            var position = _camera.GetCameraPosition() - new Vector3(view.M31, view.M32, view.M33) * 2f;
            return _lifecycle.SpawnOverlay(new OverlayNodeState
            {
                Kind = OverlayNodeKind.Collider, Alpha = .2f,
                Collider = new IkCollider
                {
                    Shape = shape,
                    Transform = PoseTransform.Identity with
                    {
                        Position = position,
                        Scale = shape == IkColliderShape.Capsule ? new Vector3(.5f, 1, .5f) : Vector3.One,
                    },
                },
            });
        });

    public SceneCreationResult CreateWorldObject(string path, PoseTransform placement) =>
        Create(SceneEntityKind.WorldObject, () => _lifecycle.SpawnWorldObject(path, Transform.FromPose(placement), true));

    private SceneCreationResult Create(SceneEntityKind kind, Func<object?> create) =>
        CanCreate ? Track(kind, create()) : new(null, "Creation requires an active GPose session.");

    public SceneCreationResult Duplicate(SelectionId source, bool withPose = false)
    {
        if (!CanCreate) return new(null, "Duplication requires an active GPose session.");
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
        return Track(source.Kind, copy);
    }

    private IActor? DuplicateActor(IActor source, bool withPose)
    {
        IActor? Clone()
        {
            var clone = _spawn.CloneActor(source);
            if (clone is null) return null;
            _lifecycle.WhenPosable(clone, target =>
            {
                if (!_actors.IsAvailable(source) || !_actors.IsAvailable(target)) return;
                _spawn.CopyDrawnAppearance(source, target);
                _spawn.CopyEquipmentVisibility(source, target);
            });
            return clone;
        }
        string name = Config.ConfigurationService.StripObjectIndex(source.Name);
        if (!withPose || _skeletons.GetSkeleton(source) is null)
            return _lifecycle.SpawnActor($"Duplicate actor '{name}'", Clone, source: source);
        var posed = _lifecycle.SpawnActorWithPose($"Duplicate actor '{name}' with pose", Clone, source);
        if (posed is not null && _bindings.GetActorId(posed) is { } id) _animation.Pause(id);
        return posed;
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
