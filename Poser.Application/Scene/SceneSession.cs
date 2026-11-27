using System.Threading;
using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>What happened to one new picture of the scene.</summary>
public enum SceneRefreshOutcome
{
    /// <summary>The new picture became the current one.</summary>
    Applied,

    /// <summary>The new picture became the current one, but code listening for
    /// the change failed.</summary>
    AppliedWithNotificationFailures,

    /// <summary>The new picture has the same entities as the current one.</summary>
    NoChange,

    /// <summary>The new picture's revision is older than the current one's.</summary>
    RejectedOlderRevision,

    /// <summary>The new picture failed <see cref="SceneSnapshotValidator"/>:
    /// an entry is incomplete or names something that is not in it.</summary>
    RejectedInvalidCandidate,

    /// <summary>A new picture was offered from inside the code listening for the
    /// last change.</summary>
    RejectedReentrant,

    // Short alias for callers that prefer the concise outcome name.
    RejectedInvalid = RejectedInvalidCandidate,
}

/// <summary>
/// The result of offering a new picture of the scene. When it was refused,
/// nothing changed: not the picture, the ids, the remembered generations, the
/// selection, and no event was sent. Failures in code listening for the change
/// are reported after the new picture is already current.
/// </summary>
public sealed record SceneRefreshResult
{
    public SceneRefreshResult(
        SceneRefreshOutcome outcome,
        string? detail = null,
        IReadOnlyList<string>? notificationFailures = null)
    {
        Outcome = outcome;
        Detail = detail;
        NotificationFailures = Array.AsReadOnly(
            (notificationFailures ?? Array.Empty<string>()).ToArray());
    }

    public SceneRefreshOutcome Outcome { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> NotificationFailures { get; }

    /// <summary>Whether the new picture was taken, or matched the current one.</summary>
    public bool Accepted => Outcome is
        SceneRefreshOutcome.Applied or
        SceneRefreshOutcome.AppliedWithNotificationFailures or
        SceneRefreshOutcome.NoChange;

    /// <summary>Whether the current picture changed.</summary>
    public bool StateChanged => Outcome is
        SceneRefreshOutcome.Applied or
        SceneRefreshOutcome.AppliedWithNotificationFailures;

    /// <summary>Whether the new picture was refused, which changes nothing.</summary>
    public bool Rejected => !Accepted;

    /// <summary>
    /// Compatibility conversion for existing boolean admission checks. New
    /// callers should inspect <see cref="Outcome"/> so that NoChange and
    /// post-commit notification failures remain distinguishable.
    /// </summary>
    public static implicit operator bool(SceneRefreshResult result) =>
        result.Accepted;
}

/// <summary>
/// Holds Poser's current picture of the scene: which actors, skeletons,
/// objects, lights and cameras there are, by id. It checks each new picture
/// before taking it and keeps the selection to what is still there. It holds
/// ids, never game memory. Call it on the game's main thread; it refuses a call
/// made from inside its own events, but does not check the thread.
/// </summary>
public sealed class SceneSession
{
    private SceneSnapshot _snapshot = SceneSnapshot.Empty;
    private readonly EntityIndex<ActorId, ActorDescriptor> _actors =
        new("Actor", static actor => actor.Id, static id => (id.LogicalId, id.Generation));
    private readonly EntityIndex<LightId, LightDescriptor> _lights =
        new("light", static light => light.Id, static id => (id.LogicalId, id.Generation));
    private readonly EntityIndex<CameraId, CameraDescriptor> _cameras =
        new("camera", static camera => camera.Id, static id => (id.LogicalId, id.Generation));
    private readonly EntityIndex<PropId, PropDescriptor> _props =
        new("object", static prop => prop.Id, static id => (id.LogicalId, id.Generation));
    private readonly EntityIndex<WorldObjectId, WorldObjectDescriptor> _worldObjects =
        new("world object", static worldObject => worldObject.Id, static id => (id.LogicalId, id.Generation));
    private readonly EntityIndex<OverlayId, OverlayDescriptor> _overlays =
        new("overlay", static overlay => overlay.Id, static id => (id.LogicalId, id.Generation));
    private Dictionary<BoneId, BoneDescriptor> _bones = new();
    private Dictionary<ActorId, GazeDescriptor> _gazes = new();

    // The newest skeleton generation seen for each actor generation and slot.
    // Like the generations in each EntityIndex, they are kept for the whole
    // GPose session and cleared for a new one.
    private readonly Dictionary<(Guid Actor, uint ActorGeneration, PoseSlot Slot), uint>
        _skeletonGenerationFloors = new();
    private readonly ISessionGenerationSource? _sessions;
    private SessionGeneration? _floorSession;
    private int _refreshGate;

    public SceneSession(
        SelectionSession selection,
        ISessionGenerationSource? sessions = null)
    {
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _sessions = sessions;
        _floorSession = sessions?.ActiveSessionGeneration;
    }

    public event Action<SceneSnapshot>? SceneChanged;

    public SelectionSession Selection { get; }
    public SceneSnapshot Snapshot => _snapshot;
    public ulong Revision => _snapshot.Revision;

    /// <summary>What can be done to a selected thing: whether it can be shown or
    /// hidden and how it is removed. Null when that exact thing is not in the
    /// scene; an id from before a redraw is not matched to its new id here.</summary>
    public CurrentSelectionEntity? ReadCurrent(SelectionId id) =>
        SelectionEntityCapabilities.Read(_snapshot, id);

    /// <summary>
    /// Offers a new picture and ignores the result. Callers that need to know
    /// whether it was taken use <see cref="TryRefresh"/>.
    /// </summary>
    public void Refresh(SceneSnapshot? snapshot) => _ = TryRefresh(snapshot);

    /// <summary>
    /// Checks a new picture of the scene and takes it whole, or not at all. Its
    /// revision number never goes down. The same revision with the same
    /// entities is <see cref="SceneRefreshOutcome.NoChange"/>; the same revision
    /// with changed details, such as one equipment slot, is taken.
    /// </summary>
    public SceneRefreshResult TryRefresh(SceneSnapshot? snapshot)
    {
        if (Interlocked.Exchange(ref _refreshGate, 1) != 0)
            return new(
                SceneRefreshOutcome.RejectedReentrant,
                "Scene refresh is already validating or notifying observers.");

        try
        {
            if (snapshot is null)
                return Invalid("A scene snapshot is required.");

            if (snapshot.Revision < Revision)
                return new(
                    SceneRefreshOutcome.RejectedOlderRevision,
                    $"Revision {snapshot.Revision} is older than committed revision {Revision}.");

            var candidate = new SceneSnapshotValidator.Candidate(
                _actors.Stage(), new(), _lights.Stage(), _cameras.Stage(), _props.Stage(),
                _worldObjects.Stage(), _overlays.Stage(), new());
            if (!SceneSnapshotValidator.TryBuildIndexes(snapshot, candidate, out var validationError))
                return Invalid(validationError!);

            var session = _sessions?.ActiveSessionGeneration;
            var newSession = session != _floorSession;
            if (!newSession
                && !TryValidateGenerationFloors(snapshot, out var floorError))
                return Invalid(floorError!);

            if (snapshot.ContentEquals(_snapshot))
                return new(SceneRefreshOutcome.NoChange);

            // Nothing above changed the current picture. From here on the new
            // picture, its lists and the remembered generations are taken
            // together.
            _actors.Commit(candidate.Actors, newSession);
            _lights.Commit(candidate.Lights, newSession);
            _cameras.Commit(candidate.Cameras, newSession);
            _props.Commit(candidate.Props, newSession);
            _worldObjects.Commit(candidate.WorldObjects, newSession);
            _overlays.Commit(candidate.Overlays, newSession);
            _bones = candidate.Bones;
            _gazes = candidate.Gazes;
            _snapshot = snapshot;
            if (newSession)
            {
                _skeletonGenerationFloors.Clear();
                _floorSession = session;
            }
            RecordSkeletonFloors(snapshot);

            var failures = new List<string>();
            try
            {
                Selection.Reconcile(Resolve);
            }
            catch (Exception exception)
            {
                failures.Add(DescribeFailure("Selection reconciliation", exception));
            }

            failures.AddRange(PublishSceneChanged(snapshot));
            return failures.Count == 0
                ? new(SceneRefreshOutcome.Applied)
                : new(
                    SceneRefreshOutcome.AppliedWithNotificationFailures,
                    "Scene committed; one or more post-commit observers failed.",
                    failures);
        }
        finally
        {
            Volatile.Write(ref _refreshGate, 0);
        }
    }

    /// <summary>
    /// Reconciles a selection id to the current exact generation. A bone
    /// selection survives only while its exact BoneId is present; a missing
    /// bone may fall back to its current actor, never another bone. A
    /// GazeTarget survives only for the current actor's Position-mode gaze
    /// descriptor and an enabled selected part.
    /// </summary>
    public SelectionId? Resolve(SelectionId id)
    {
        if (id.Kind == SceneEntityKind.Actor && id.Actor is { } actor)
            return _actors.TryFind(actor.LogicalId, out var currentActor)
                ? SelectionId.ForActor(currentActor.Id)
                : null;

        if (id.Kind == SceneEntityKind.GazeTarget && id.Actor is { } gazeActor)
        {
            if (!_actors.TryFind(gazeActor.LogicalId, out var gazeOwner) ||
                !_gazes.TryGetValue(gazeOwner.Id, out var gaze) ||
                gaze.Mode != GazeMode.Position)
                return null;

            var part = id.Gaze ?? GazePart.Anchor;
            return IsValidGazePart(gaze, part)
                ? SelectionId.ForGazeTarget(gazeOwner.Id, part)
                : null;
        }

        if (id.Kind == SceneEntityKind.Bone)
        {
            if (id.Bone is { } bone)
            {
                if (_bones.TryGetValue(bone, out var exact))
                    return SelectionId.ForBone(exact.Id);

                // A missing bone may keep its actor selection, but never a
                // same-named bone from another generation, slot, or actor.
                return _actors.TryFind(bone.Skeleton.Actor.LogicalId, out var owner)
                    ? SelectionId.ForActor(owner.Id)
                    : null;
            }

            if (id.OwnerActorLineage is { } groupOwner &&
                id.ExternalId is { } groupId &&
                _actors.TryFind(groupOwner, out var currentGroupOwner))
                return SelectionId.ForBoneGroup(currentGroupOwner.Id, groupId);

            return null;
        }

        if (id.Kind == SceneEntityKind.Light && id.Light is { } light)
            return _lights.TryFind(light.LogicalId, out var currentLight)
                ? SelectionId.ForLight(currentLight.Id)
                : null;

        if (id.Kind == SceneEntityKind.Camera && id.Camera is { } camera)
            return _cameras.TryFind(camera.LogicalId, out var currentCamera)
                ? SelectionId.ForCamera(currentCamera.Id)
                : null;

        if (id.Kind == SceneEntityKind.Prop && id.Prop is { } prop)
            return _props.TryFind(prop.LogicalId, out var currentProp)
                ? SelectionId.ForProp(currentProp.Id)
                : null;

        if (id.Kind == SceneEntityKind.WorldObject && id.WorldObject is { } worldObject)
            return _worldObjects.TryFind(worldObject.LogicalId, out var currentWorldObject)
                ? SelectionId.ForWorldObject(currentWorldObject.Id)
                : null;

        if (id.Kind == SceneEntityKind.Overlay && id.Overlay is { } overlay)
            return _overlays.TryFind(overlay.LogicalId, out var currentOverlay)
                ? SelectionId.ForOverlay(currentOverlay.Id)
                : null;

        // Environment is the scene singleton and carries no generation. Its
        // descriptor is read state, not a second selectable entity.
        if (id.Kind == SceneEntityKind.Environment)
            return id;

        return null;
    }

    /// <summary>Checks exact transform-target presence without lineage repair.</summary>
    public bool Contains(TransformTargetId target) =>
        target.Kind switch
        {
            TransformTargetKind.Collider => target.Collider is { } collider &&
                _overlays.TryGet(collider, out var overlay) && overlay.Kind == OverlayNodeKind.Collider,
            TransformTargetKind.Actor =>
                target.Actor is { } actor && _actors.Contains(actor),
            TransformTargetKind.Bone =>
                target.Bone is { } bone && _bones.ContainsKey(bone),
            TransformTargetKind.Light =>
                target.Light is { } light && _lights.Contains(light),
            TransformTargetKind.Prop =>
                target.Prop is { } prop && _props.Contains(prop),
            TransformTargetKind.WorldObject =>
                target.WorldObject is { } worldObject &&
                _worldObjects.Contains(worldObject),
            _ => false,
        };

    /// <summary>Resolves retained history/group metadata against the committed
    /// scene after a redraw or replacement. This never authorizes a stale
    /// public edit; those must still pass exact-id presence and native binding
    /// checks. Unlike selection reconciliation, a missing bone cannot fall
    /// back to its actor.</summary>
    public TransformTargetId? CurrentTransformTarget(TransformTargetId target)
    {
        if (Contains(target)) return target;
        if (target.Bone is { } bone)
        {
            if (!_actors.TryFind(bone.Skeleton.Actor.LogicalId, out var owner)
                || owner.GetSkeleton(bone.Slot) is not { } skeleton)
                return null;
            var current = bone with { Skeleton = skeleton.Id };
            return _bones.ContainsKey(current) ? TransformTargetId.ForBone(current) : null;
        }

        return target.Kind switch
        {
            TransformTargetKind.Actor when target.Actor is { } actor &&
                _actors.TryFind(actor.LogicalId, out var current) => TransformTargetId.ForActor(current.Id),
            TransformTargetKind.Light when target.Light is { } light &&
                _lights.TryFind(light.LogicalId, out var current) => TransformTargetId.ForLight(current.Id),
            TransformTargetKind.Prop when target.Prop is { } prop &&
                _props.TryFind(prop.LogicalId, out var current) => TransformTargetId.ForProp(current.Id),
            TransformTargetKind.WorldObject when target.WorldObject is { } world &&
                _worldObjects.TryFind(world.LogicalId, out var current) => TransformTargetId.ForWorldObject(current.Id),
            TransformTargetKind.Collider when target.Collider is { } collider &&
                _overlays.TryFind(collider.LogicalId, out var current) &&
                current.Kind == OverlayNodeKind.Collider => TransformTargetId.ForCollider(current.Id),
            _ => null,
        };
    }

    /// <summary>A whole-actor redraw publishes a skeleton-less interval.
    /// History waits for its character skeleton before deciding whether a
    /// retained slot/bone was removed. A missing weapon on an otherwise ready
    /// actor is not kept pending indefinitely.
    /// This never makes the old target available for a native write.</summary>
    public bool IsTransformTargetPending(TransformTargetId target) =>
        target.Bone is { } bone
        && _actors.TryFind(bone.Skeleton.Actor.LogicalId, out var owner)
        && owner.CharacterSkeleton is null
        && owner.GetSkeleton(bone.Slot) is null;

    private static SceneRefreshResult Invalid(string detail) =>
        new(SceneRefreshOutcome.RejectedInvalidCandidate, detail);

    private bool TryValidateGenerationFloors(
        SceneSnapshot snapshot,
        out string? validationError)
    {
        foreach (var actor in snapshot.Actors)
        {
            if (_actors.FloorViolation(actor) is { } regressed)
                return SceneSnapshotValidator.Fail(regressed, out validationError);

            foreach (var skeleton in actor.Skeletons)
            {
                var key = (
                    skeleton.Id.Actor.LogicalId,
                    skeleton.Id.Actor.Generation,
                    skeleton.Id.Slot);
                if (_skeletonGenerationFloors.TryGetValue(key, out var floor) &&
                    skeleton.Id.Generation < floor)
                {
                    validationError =
                        $"Skeleton {skeleton.Id} regressed from generation {floor} to {skeleton.Id.Generation}.";
                    return false;
                }
            }
        }

        validationError = _lights.FloorViolation(snapshot.Lights)
            ?? _cameras.FloorViolation(snapshot.Cameras)
            ?? _props.FloorViolation(snapshot.Props)
            ?? _worldObjects.FloorViolation(snapshot.WorldObjects)
            ?? _overlays.FloorViolation(snapshot.Overlays);
        return validationError is null;
    }

    private void RecordSkeletonFloors(SceneSnapshot snapshot)
    {
        foreach (var actor in snapshot.Actors)
        {
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
    }

    private IReadOnlyList<string> PublishSceneChanged(SceneSnapshot snapshot)
    {
        var handlers = SceneChanged?.GetInvocationList();
        if (handlers is null || handlers.Length == 0)
            return Array.Empty<string>();

        var failures = new List<string>();
        foreach (var handler in handlers)
        {
            try
            {
                ((Action<SceneSnapshot>)handler)(snapshot);
            }
            catch (Exception exception)
            {
                failures.Add(DescribeFailure("SceneChanged observer", exception));
            }
        }

        return failures.AsReadOnly();
    }

    private static string DescribeFailure(string source, Exception exception) =>
        $"{source} {exception.GetType().Name}: {exception.Message}";

    private static bool IsValidGazePart(GazeDescriptor gaze, GazePart part) =>
        part switch
        {
            // The anchor is the shared Position-mode point and has no
            // corresponding per-part flag in the current service contract.
            GazePart.Anchor => true,
            GazePart.Eyes => (gaze.Parts & GazeParts.Eyes) != GazeParts.None,
            GazePart.Head => (gaze.Parts & GazeParts.Head) != GazeParts.None,
            GazePart.Body => (gaze.Parts & GazeParts.Body) != GazeParts.None,
            _ => false,
        };
}
