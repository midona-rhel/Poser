using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>
/// Owns the application scene read model, exact-id indexes, selection
/// reconciliation, and monotonic refresh policy. It does not create snapshots,
/// own native handles, or duplicate Game's binding registry.
/// </summary>
public sealed class SceneSession
{
    private SceneSnapshot _snapshot = SceneSnapshot.Empty;
    private Dictionary<ActorId, ActorDescriptor> _actors = new();
    private Dictionary<BoneId, BoneDescriptor> _bones = new();
    private Dictionary<LightId, LightDescriptor> _lights = new();
    private Dictionary<CameraId, CameraDescriptor> _cameras = new();
    private Dictionary<PropId, PropDescriptor> _props = new();

    // A producer must never make an accepted scene generation go backwards,
    // even when an entity disappeared from an intervening snapshot.
    private readonly Dictionary<Guid, uint> _actorGenerationFloors = new();
    private readonly Dictionary<(Guid Actor, uint ActorGeneration, PoseSlot Slot), uint>
        _skeletonGenerationFloors = new();
    private readonly Dictionary<Guid, uint> _lightGenerationFloors = new();
    private readonly Dictionary<Guid, uint> _cameraGenerationFloors = new();
    private readonly Dictionary<Guid, uint> _propGenerationFloors = new();

    public SceneSession(SelectionSession selection)
    {
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public event Action<SceneSnapshot>? SceneChanged;

    public SelectionSession Selection { get; }
    public SceneSnapshot Snapshot => _snapshot;
    public ulong Revision => _snapshot.Revision;

    /// <summary>
    /// Compatibility entry point for existing producers. Rejected stale or
    /// regressing snapshots leave every session-owned value unchanged; callers
    /// that need the decision use <see cref="TryRefresh"/>.
    /// </summary>
    public void Refresh(SceneSnapshot snapshot) => _ = TryRefresh(snapshot);

    /// <summary>
    /// Publishes one non-older, generation-monotonic snapshot. The
    /// candidate is fully indexed before the current scene is replaced, so a
    /// malformed or regressing refresh cannot partially mutate the session.
    /// </summary>
    public bool TryRefresh(SceneSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Revision < Revision)
            return false;

        BuildIndexes(
            snapshot,
            out var actors,
            out var bones,
            out var lights,
            out var cameras,
            out var props);

        if (!HasMonotonicGenerations(snapshot))
            return false;

        _actors = actors;
        _bones = bones;
        _lights = lights;
        _cameras = cameras;
        _props = props;
        _snapshot = snapshot;
        RecordGenerationFloors(snapshot);

        Selection.Reconcile(Resolve);
        SceneChanged?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Reconciles a stable selection id to the current exact generation. A
    /// bone selection survives only while its exact BoneId is present; a
    /// missing bone may fall back to its current actor, never another bone.
    /// </summary>
    public SelectionId? Resolve(SelectionId id)
    {
        if (id.Kind == SceneEntityKind.Actor && id.Actor is { } actor)
            return TryFindActor(actor.LogicalId, out var currentActor)
                ? SelectionId.ForActor(currentActor.Id)
                : null;

        if (id.Kind == SceneEntityKind.GazeTarget && id.Actor is { } gazeActor)
            return TryFindActor(gazeActor.LogicalId, out var gazeOwner)
                ? SelectionId.ForGazeTarget(
                    gazeOwner.Id,
                    id.Gaze ?? GazePart.Anchor)
                : null;

        if (id.Kind == SceneEntityKind.Bone)
        {
            if (id.Bone is { } bone)
            {
                if (_bones.TryGetValue(bone, out var exact))
                    return SelectionId.ForBone(exact.Id);

                // A missing bone may keep its actor selection, but never a
                // same-named bone from another generation, slot, or actor.
                return TryFindActor(bone.Skeleton.Actor.LogicalId, out var owner)
                    ? SelectionId.ForActor(owner.Id)
                    : null;
            }

            if (id.OwnerActorLineage is { } groupOwner &&
                id.ExternalId is { } groupId &&
                TryFindActor(groupOwner, out var currentGroupOwner))
                return SelectionId.ForBoneGroup(currentGroupOwner.Id, groupId);

            return null;
        }

        if (id.Kind == SceneEntityKind.Light && id.Light is { } light)
            return TryFindLight(light.LogicalId, out var currentLight)
                ? SelectionId.ForLight(currentLight.Id)
                : null;

        if (id.Kind == SceneEntityKind.Camera && id.Camera is { } camera)
            return TryFindCamera(camera.LogicalId, out var currentCamera)
                ? SelectionId.ForCamera(currentCamera.Id)
                : null;

        if (id.Kind == SceneEntityKind.Prop && id.Prop is { } prop)
            return TryFindProp(prop.LogicalId, out var currentProp)
                ? SelectionId.ForProp(currentProp.Id)
                : null;

        // Environment is the scene singleton and carries no generation. Its
        // optional descriptor is read state, not a second selectable entity.
        if (id.Kind == SceneEntityKind.Environment)
            return id;

        return null;
    }

    /// <summary>Checks exact transform-target presence without lineage repair.</summary>
    public bool Contains(TransformTargetId target) =>
        target.Kind switch
        {
            TransformTargetKind.Actor =>
                target.Actor is { } actor && _actors.ContainsKey(actor),
            TransformTargetKind.Bone =>
                target.Bone is { } bone && _bones.ContainsKey(bone),
            TransformTargetKind.Light =>
                target.Light is { } light && _lights.ContainsKey(light),
            TransformTargetKind.Prop =>
                target.Prop is { } prop && _props.ContainsKey(prop),
            _ => false,
        };

    private static void BuildIndexes(
        SceneSnapshot snapshot,
        out Dictionary<ActorId, ActorDescriptor> actors,
        out Dictionary<BoneId, BoneDescriptor> bones,
        out Dictionary<LightId, LightDescriptor> lights,
        out Dictionary<CameraId, CameraDescriptor> cameras,
        out Dictionary<PropId, PropDescriptor> props)
    {
        actors = new();
        bones = new();
        lights = new();
        cameras = new();
        props = new();

        var actorLineages = new HashSet<Guid>();
        foreach (var actor in snapshot.Actors)
        {
            if (!actorLineages.Add(actor.Id.LogicalId))
                throw new ArgumentException(
                    $"Scene contains more than one actor generation for {actor.Id.LogicalId:N}.",
                    nameof(snapshot));
            if (!actors.TryAdd(actor.Id, actor))
                throw new ArgumentException(
                    $"Scene contains duplicate actor {actor.Id}.",
                    nameof(snapshot));

            var slots = new HashSet<PoseSlot>();
            foreach (var skeleton in actor.Skeletons)
            {
                if (skeleton.Id.Actor != actor.Id)
                    throw new ArgumentException(
                        $"Skeleton {skeleton.Id} is not owned by actor {actor.Id}.",
                        nameof(snapshot));
                if (!slots.Add(skeleton.Id.Slot))
                    throw new ArgumentException(
                        $"Scene contains duplicate {skeleton.Id.Slot} skeletons for {actor.Id}.",
                        nameof(snapshot));

                foreach (var bone in skeleton.Bones)
                {
                    if (bone.Id.Skeleton != skeleton.Id)
                        throw new ArgumentException(
                            $"Bone {bone.Id} is not owned by skeleton {skeleton.Id}.",
                            nameof(snapshot));
                    if (bone.Parent is { } parent && parent.Skeleton != skeleton.Id)
                        throw new ArgumentException(
                            $"Bone {bone.Id} has a parent from another skeleton.",
                            nameof(snapshot));
                    if (!bones.TryAdd(bone.Id, bone))
                        throw new ArgumentException(
                            $"Scene contains duplicate bone {bone.Id}.",
                            nameof(snapshot));
                }
            }
        }

        AddLightIndexes(snapshot, lights);
        AddCameraIndexes(snapshot, cameras);
        AddPropIndexes(snapshot, props);

        var gazeActors = new HashSet<ActorId>();
        foreach (var gaze in snapshot.GazeStates)
        {
            if (!gazeActors.Add(gaze.Actor))
                throw new ArgumentException(
                    $"Scene contains duplicate gaze state for {gaze.Actor}.",
                    nameof(snapshot));
        }
    }

    private static void AddLightIndexes(
        SceneSnapshot snapshot,
        Dictionary<LightId, LightDescriptor> lights)
    {
        var lineages = new HashSet<Guid>();
        foreach (var light in snapshot.Lights)
        {
            if (!lineages.Add(light.Id.LogicalId) || !lights.TryAdd(light.Id, light))
                throw new ArgumentException(
                    $"Scene contains duplicate light {light.Id.LogicalId:N}.",
                    nameof(snapshot));
        }
    }

    private static void AddCameraIndexes(
        SceneSnapshot snapshot,
        Dictionary<CameraId, CameraDescriptor> cameras)
    {
        var lineages = new HashSet<Guid>();
        foreach (var camera in snapshot.Cameras)
        {
            if (!lineages.Add(camera.Id.LogicalId) || !cameras.TryAdd(camera.Id, camera))
                throw new ArgumentException(
                    $"Scene contains duplicate camera {camera.Id.LogicalId:N}.",
                    nameof(snapshot));
        }
    }

    private static void AddPropIndexes(
        SceneSnapshot snapshot,
        Dictionary<PropId, PropDescriptor> props)
    {
        var lineages = new HashSet<Guid>();
        foreach (var prop in snapshot.Props)
        {
            if (!lineages.Add(prop.Id.LogicalId) || !props.TryAdd(prop.Id, prop))
                throw new ArgumentException(
                    $"Scene contains duplicate prop {prop.Id.LogicalId:N}.",
                    nameof(snapshot));
        }
    }

    private bool HasMonotonicGenerations(SceneSnapshot snapshot)
    {
        foreach (var actor in snapshot.Actors)
        {
            if (_actorGenerationFloors.TryGetValue(
                    actor.Id.LogicalId,
                    out var actorFloor) &&
                actor.Id.Generation < actorFloor)
                return false;

            foreach (var skeleton in actor.Skeletons)
            {
                var key = (
                    skeleton.Id.Actor.LogicalId,
                    skeleton.Id.Actor.Generation,
                    skeleton.Id.Slot);
                if (_skeletonGenerationFloors.TryGetValue(key, out var floor) &&
                    skeleton.Id.Generation < floor)
                    return false;
            }
        }

        return HasMonotonicObjectGenerations(
                   snapshot.Lights,
                   _lightGenerationFloors,
                   static light => (light.Id.LogicalId, light.Id.Generation)) &&
               HasMonotonicObjectGenerations(
                   snapshot.Cameras,
                   _cameraGenerationFloors,
                   static camera => (camera.Id.LogicalId, camera.Id.Generation)) &&
               HasMonotonicObjectGenerations(
                   snapshot.Props,
                   _propGenerationFloors,
                   static prop => (prop.Id.LogicalId, prop.Id.Generation));
    }

    private static bool HasMonotonicObjectGenerations<T>(
        IReadOnlyList<T> values,
        Dictionary<Guid, uint> floors,
        Func<T, (Guid LogicalId, uint Generation)> identity)
    {
        foreach (var value in values)
        {
            var (logicalId, generation) = identity(value);
            if (floors.TryGetValue(logicalId, out var floor) &&
                generation < floor)
                return false;
        }

        return true;
    }

    private void RecordGenerationFloors(SceneSnapshot snapshot)
    {
        foreach (var actor in snapshot.Actors)
        {
            RaiseFloor(_actorGenerationFloors, actor.Id.LogicalId, actor.Id.Generation);
            foreach (var skeleton in actor.Skeletons)
            {
                var key = (
                    skeleton.Id.Actor.LogicalId,
                    skeleton.Id.Actor.Generation,
                    skeleton.Id.Slot);
                if (!_skeletonGenerationFloors.TryGetValue(key, out var floor) ||
                    skeleton.Id.Generation > floor)
                    _skeletonGenerationFloors[key] = skeleton.Id.Generation;
            }
        }

        foreach (var light in snapshot.Lights)
            RaiseFloor(_lightGenerationFloors, light.Id.LogicalId, light.Id.Generation);
        foreach (var camera in snapshot.Cameras)
            RaiseFloor(_cameraGenerationFloors, camera.Id.LogicalId, camera.Id.Generation);
        foreach (var prop in snapshot.Props)
            RaiseFloor(_propGenerationFloors, prop.Id.LogicalId, prop.Id.Generation);
    }

    private static void RaiseFloor(
        Dictionary<Guid, uint> floors,
        Guid logicalId,
        uint generation)
    {
        if (!floors.TryGetValue(logicalId, out var floor) || generation > floor)
            floors[logicalId] = generation;
    }

    private bool TryFindActor(Guid logicalId, out ActorDescriptor actor)
    {
        foreach (var candidate in _actors.Values)
        {
            if (candidate.Id.LogicalId == logicalId)
            {
                actor = candidate;
                return true;
            }
        }

        actor = null!;
        return false;
    }

    private bool TryFindLight(Guid logicalId, out LightDescriptor light)
    {
        foreach (var candidate in _lights.Values)
        {
            if (candidate.Id.LogicalId == logicalId)
            {
                light = candidate;
                return true;
            }
        }

        light = null!;
        return false;
    }

    private bool TryFindCamera(Guid logicalId, out CameraDescriptor camera)
    {
        foreach (var candidate in _cameras.Values)
        {
            if (candidate.Id.LogicalId == logicalId)
            {
                camera = candidate;
                return true;
            }
        }

        camera = null!;
        return false;
    }

    private bool TryFindProp(Guid logicalId, out PropDescriptor prop)
    {
        foreach (var candidate in _props.Values)
        {
            if (candidate.Id.LogicalId == logicalId)
            {
                prop = candidate;
                return true;
            }
        }

        prop = null!;
        return false;
    }
}
