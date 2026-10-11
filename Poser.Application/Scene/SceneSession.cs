using System.Threading;
using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>The outcome of one candidate scene admission.</summary>
public enum SceneRefreshOutcome
{
    /// <summary>The candidate became the committed scene.</summary>
    Applied,

    /// <summary>The candidate committed, but a post-commit observer failed.</summary>
    AppliedWithNotificationFailures,

    /// <summary>The candidate was an exact structural replay.</summary>
    NoChange,

    /// <summary>The producer revision was older than the committed revision.</summary>
    RejectedOlderRevision,

    /// <summary>The candidate failed scene schema or topology validation.</summary>
    RejectedInvalidCandidate,

    /// <summary>An admission was attempted from a reentrant observer call.</summary>
    RejectedReentrant,

    // Short alias for callers that prefer the concise outcome name.
    RejectedInvalid = RejectedInvalidCandidate,
}

/// <summary>
/// Feature-specific result for scene admission. A rejected result means that
/// the committed snapshot, indexes, generation floors, selection, and scene
/// event were left unchanged. Notification failures are reported after the
/// scene has already committed.
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

    /// <summary>Whether the candidate was accepted or was an exact replay.</summary>
    public bool Accepted => Outcome is
        SceneRefreshOutcome.Applied or
        SceneRefreshOutcome.AppliedWithNotificationFailures or
        SceneRefreshOutcome.NoChange;

    /// <summary>Whether the committed scene state changed.</summary>
    public bool StateChanged => Outcome is
        SceneRefreshOutcome.Applied or
        SceneRefreshOutcome.AppliedWithNotificationFailures;

    /// <summary>Whether the candidate was rejected without scene mutation.</summary>
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
/// Owns the committed Application scene read model, exact-id indexes,
/// selection reconciliation, and producer-revision admission policy. It does
/// not create snapshots, own native handles, or replace Game's transitional
/// candidate/binding producer. Refresh and event delivery are
/// required to stay on the owning application/framework thread; this class
/// does not guess that host affinity without a host dependency.
/// </summary>
public sealed class SceneSession : ICurrentSelectionEntityReads
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

    // Skeleton floors are keyed by their owning actor generation and slot, so
    // they stay beside the actor index rather than in it. Like the index
    // floors they live for one GPose session and are dropped only when the
    // admitted snapshot belongs to a different session generation.
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

    /// <summary>Reads current verb capabilities for an exact selection id.
    /// Old generations remain stale here even where ordinary selection
    /// reconciliation can promote them to a current lineage.</summary>
    public CurrentSelectionEntity? ReadCurrent(SelectionId id) =>
        SelectionEntityCapabilities.Read(_snapshot, id);

    /// <summary>
    /// Compatibility entry point for existing producers. It intentionally
    /// keeps the historical void signature and discards the typed result;
    /// callers that need to know whether admission succeeded must use
    /// <see cref="TryRefresh"/>. It never claims that a candidate committed.
    /// </summary>
    public void Refresh(SceneSnapshot? snapshot) => _ = TryRefresh(snapshot);

    /// <summary>
    /// Validates and transactionally admits one producer snapshot. Revisions
    /// are producer-supplied and non-decreasing. An equal-revision structural
    /// replay is <see cref="SceneRefreshOutcome.NoChange"/>; equal revision
    /// content changes are admitted for independent slot/object updates.
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

            // Everything above is candidate-local. The following swap is the
            // single commit point for snapshot, indexes, and generation floors.
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
