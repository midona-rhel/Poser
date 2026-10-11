using System.Numerics;
using System.Threading;
using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using TransformMath = Poser.Domain.Transforms.TransformMath;

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

            if (!TryBuildIndexes(snapshot, out var candidate, out var validationError))
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

    /// <summary>One admission's candidate indexes; nothing reads them until
    /// the commit swaps them in.</summary>
    private sealed record Candidate(
        EntityIndex<ActorId, ActorDescriptor>.Staged Actors,
        Dictionary<BoneId, BoneDescriptor> Bones,
        EntityIndex<LightId, LightDescriptor>.Staged Lights,
        EntityIndex<CameraId, CameraDescriptor>.Staged Cameras,
        EntityIndex<PropId, PropDescriptor>.Staged Props,
        EntityIndex<WorldObjectId, WorldObjectDescriptor>.Staged WorldObjects,
        EntityIndex<OverlayId, OverlayDescriptor>.Staged Overlays,
        Dictionary<ActorId, GazeDescriptor> Gazes);

    private bool TryBuildIndexes(
        SceneSnapshot snapshot,
        out Candidate candidate,
        out string? validationError)
    {
        candidate = new(
            _actors.Stage(), new(), _lights.Stage(), _cameras.Stage(), _props.Stage(),
            _worldObjects.Stage(), _overlays.Stage(), new());
        var actors = candidate.Actors;
        var bones = candidate.Bones;
        validationError = null;

        var skeletonIds = new HashSet<SkeletonId>();
        var boneLookup =
            new HashSet<(SkeletonId Skeleton, int PartialId, int BoneIndex)>();

        foreach (var actor in snapshot.Actors)
        {
            if (actor is null)
                return Fail("Scene contains a null actor descriptor.", out validationError);
            if (!IsValidActorId(actor.Id))
                return Fail($"Actor id {actor.Id} is invalid.", out validationError);
            if (!actors.TryAdd(actor))
                return Fail(
                    $"Scene contains more than one actor generation for {actor.Id.LogicalId:N}.",
                    out validationError);
            if (actor.Skeletons is null)
                return Fail($"Actor {actor.Id} has no skeleton collection.", out validationError);

            var slots = new HashSet<PoseSlot>();
            foreach (var skeleton in actor.Skeletons)
            {
                if (skeleton is null)
                    return Fail(
                        $"Actor {actor.Id} contains a null skeleton descriptor.",
                        out validationError);
                if (!IsValidSkeletonId(skeleton.Id))
                    return Fail(
                        $"Skeleton id {skeleton.Id} is invalid.",
                        out validationError);
                if (skeleton.Id.Actor != actor.Id)
                    return Fail(
                        $"Skeleton {skeleton.Id} is not owned by actor {actor.Id}.",
                        out validationError);
                if (!slots.Add(skeleton.Id.Slot))
                    return Fail(
                        $"Scene contains duplicate {skeleton.Id.Slot} skeletons for {actor.Id}.",
                        out validationError);
                if (!skeletonIds.Add(skeleton.Id))
                    return Fail(
                        $"Scene contains duplicate skeleton {skeleton.Id}.",
                        out validationError);
                if (skeleton.Bones is null)
                    return Fail(
                        $"Skeleton {skeleton.Id} has no bone collection.",
                        out validationError);

                foreach (var bone in skeleton.Bones)
                {
                    if (bone is null)
                        return Fail(
                            $"Skeleton {skeleton.Id} contains a null bone descriptor.",
                            out validationError);
                    if (!IsValidBoneId(bone.Id))
                        return Fail($"Bone id {bone.Id} is invalid.", out validationError);
                    if (bone.Id.Skeleton != skeleton.Id)
                        return Fail(
                            $"Bone {bone.Id} is not owned by skeleton {skeleton.Id}.",
                            out validationError);
                    if (!bones.TryAdd(bone.Id, bone))
                        return Fail(
                            $"Scene contains duplicate bone {bone.Id}.",
                            out validationError);

                    var lookup = (
                        bone.Id.Skeleton,
                        bone.Id.PartialId,
                        bone.Id.BoneIndex);
                    if (!boneLookup.Add(lookup))
                        return Fail(
                            $"Scene contains duplicate native bone lookup {bone.Id.Skeleton}/{bone.Id.PartialId}:{bone.Id.BoneIndex}; canonical names cannot disambiguate it.",
                            out validationError);
                }
            }
        }

        foreach (var actor in actors.Values)
        {
            if (actor.OwnerActor is not { } owner)
            {
                if (actor.AttachmentKind is not null)
                    return Fail(
                        $"Root actor {actor.Id} has an attachment kind.",
                        out validationError);
                continue;
            }
            if (!actor.IsCompanion)
                return Fail(
                    $"Actor {actor.Id} has OwnerActor but is not a companion.",
                    out validationError);
            if (actor.AttachmentKind is not { } attachmentKind
                || !Enum.IsDefined(attachmentKind))
                return Fail(
                    $"Attached actor {actor.Id} has no valid attachment kind.",
                    out validationError);
            if (owner == actor.Id)
                return Fail($"Actor {actor.Id} cannot own itself.", out validationError);
            if (!actors.TryGet(owner, out var ownerDescriptor))
                return Fail(
                    $"Companion {actor.Id} refers to missing owner {owner}.",
                    out validationError);
            if (ownerDescriptor.IsCompanion)
                return Fail(
                    $"Companion {actor.Id} refers to companion owner {owner}.",
                    out validationError);
        }

        foreach (var bone in bones.Values)
        {
            if (bone.Parent is not { } parent)
                continue;
            if (!IsValidBoneId(parent))
                return Fail(
                    $"Bone {bone.Id} has an invalid parent id {parent}.",
                    out validationError);
            if (parent == bone.Id)
                return Fail($"Bone {bone.Id} cannot parent itself.", out validationError);
            if (parent.Skeleton != bone.Id.Skeleton)
                return Fail(
                    $"Bone {bone.Id} has a parent from another skeleton.",
                    out validationError);
            if (!bones.ContainsKey(parent))
                return Fail(
                    $"Bone {bone.Id} refers to missing parent {parent}.",
                    out validationError);
        }

        if (!TryValidateBoneGraph(bones, out validationError))
            return false;

        if (!TryBuildLightIndexes(snapshot, bones, candidate.Lights, out validationError))
            return false;
        if (!TryBuildCameraIndexes(
                snapshot,
                actors,
                bones,
                candidate.Cameras,
                out validationError))
            return false;
        if (!TryBuildPropIndexes(snapshot, candidate.Props, out validationError))
            return false;
        if (!TryBuildWorldObjectIndexes(snapshot, candidate.WorldObjects, out validationError))
            return false;
        if (!TryBuildOverlayIndexes(snapshot, candidate.Overlays, out validationError))
            return false;

        var gazeActors = new HashSet<Guid>();
        foreach (var gaze in snapshot.GazeStates)
        {
            if (gaze is null)
                return Fail("Scene contains a null gaze descriptor.", out validationError);
            if (!actors.Contains(gaze.Actor))
                return Fail(
                    $"Gaze state refers to missing actor {gaze.Actor}.",
                    out validationError);
            if (!gazeActors.Add(gaze.Actor.LogicalId))
                return Fail(
                    $"Scene contains duplicate gaze state for {gaze.Actor.LogicalId:N}.",
                    out validationError);
            if (!Enum.IsDefined(typeof(GazeMode), gaze.Mode))
                return Fail(
                    $"Gaze state for {gaze.Actor} has unknown mode {gaze.Mode}.",
                    out validationError);
            if ((gaze.Parts & ~GazeParts.All) != GazeParts.None)
                return Fail(
                    $"Gaze state for {gaze.Actor} has unknown part bits.",
                    out validationError);
            if ((gaze.LockedParts & ~GazeParts.All) != GazeParts.None ||
                (gaze.LockedParts & ~gaze.Parts) != GazeParts.None)
                return Fail(
                    $"Gaze state for {gaze.Actor} has an invalid lock mask.",
                    out validationError);
            if (gaze.Parts == GazeParts.None &&
                (gaze.Mode != GazeMode.Off ||
                 gaze.LockedParts != GazeParts.None))
                return Fail(
                    $"Gaze state for {gaze.Actor} has parts disabled outside Off mode.",
                    out validationError);
            if (gaze.Mode != GazeMode.Off && gaze.Parts == GazeParts.None)
                return Fail(
                    $"Gaze state for {gaze.Actor} is active without participating parts.",
                    out validationError);
            if (gaze.Mode == GazeMode.Off &&
                gaze.LockedParts != GazeParts.None)
                return Fail(
                    $"Gaze state for {gaze.Actor} locks parts while Off.",
                    out validationError);
            if (gaze.Mode == GazeMode.Actor)
            {
                if (gaze.TargetActor is not { } target)
                    return Fail(
                        $"Actor gaze state for {gaze.Actor} has no target.",
                        out validationError);
                if (target == gaze.Actor)
                    return Fail(
                        $"Actor gaze state for {gaze.Actor} targets itself.",
                        out validationError);
                if (!actors.Contains(target))
                    return Fail(
                        $"Gaze state for {gaze.Actor} refers to missing target {target}.",
                        out validationError);
            }
            else if (gaze.TargetActor is not null)
            {
                return Fail(
                    $"Only Actor gaze mode may carry TargetActor for {gaze.Actor}.",
                    out validationError);
            }
            if (!TransformMath.IsFinite(gaze.Anchor) ||
                !TransformMath.IsFinite(gaze.EyesPosition) ||
                !TransformMath.IsFinite(gaze.HeadPosition) ||
                !TransformMath.IsFinite(gaze.BodyPosition))
                return Fail(
                    $"Gaze state for {gaze.Actor} contains a non-finite position.",
                    out validationError);
            candidate.Gazes.Add(gaze.Actor, gaze);
        }

        if (snapshot.Environment is { } environment)
        {
            if (environment.MinuteOfDay is < 0 or > 1439)
                return Fail(
                    $"Environment minute {environment.MinuteOfDay} is outside 0..1439.",
                    out validationError);
            if (environment.DayOfMonth is < 1 or > 31)
                return Fail(
                    $"Environment day {environment.DayOfMonth} is outside 1..31.",
                    out validationError);
            if ((environment.HeldSections & ~EnvironmentSection.All) !=
                EnvironmentSection.None)
                return Fail(
                    "Environment contains unknown held-section bits.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    private static bool TryBuildLightIndexes(
        SceneSnapshot snapshot,
        Dictionary<BoneId, BoneDescriptor> bones,
        EntityIndex<LightId, LightDescriptor>.Staged lights,
        out string? validationError)
    {
        foreach (var light in snapshot.Lights)
        {
            if (light is null)
                return Fail("Scene contains a null light descriptor.", out validationError);
            if (!IsValidLightId(light.Id))
                return Fail($"Light id {light.Id} is invalid.", out validationError);
            if (!lights.TryAdd(light))
                return Fail(
                    $"Scene contains duplicate light {light.Id.LogicalId:N}.",
                    out validationError);
            if (!Enum.IsDefined(typeof(LightKind), light.Kind) ||
                !Enum.IsDefined(typeof(LightOwnership), light.Ownership))
                return Fail(
                    $"Light {light.Id} has an unknown kind or ownership.",
                    out validationError);
            if (light.AttachedBone is { } bone && !bones.ContainsKey(bone))
                return Fail(
                    $"Light {light.Id} refers to missing attached bone {bone}.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    private static bool TryBuildCameraIndexes(
        SceneSnapshot snapshot,
        EntityIndex<ActorId, ActorDescriptor>.Staged actors,
        Dictionary<BoneId, BoneDescriptor> bones,
        EntityIndex<CameraId, CameraDescriptor>.Staged cameras,
        out string? validationError)
    {
        var liveCount = 0;
        var defaultCount = 0;
        CameraDescriptor? defaultCamera = null;
        foreach (var camera in snapshot.Cameras)
        {
            if (camera is null)
                return Fail("Scene contains a null camera descriptor.", out validationError);
            if (!IsValidCameraId(camera.Id))
                return Fail($"Camera id {camera.Id} is invalid.", out validationError);
            if (!cameras.TryAdd(camera))
                return Fail(
                    $"Scene contains duplicate camera {camera.Id.LogicalId:N}.",
                    out validationError);
            if (!Enum.IsDefined(typeof(CameraKind), camera.Kind))
                return Fail(
                    $"Camera {camera.Id} has an unknown kind.",
                    out validationError);
            if (camera.IsLive)
                liveCount++;
            if (camera.IsDefault)
            {
                defaultCount++;
                defaultCamera = camera;
            }
            if (!TransformMath.IsFinite(camera.TargetOffset))
                return Fail(
                    $"Camera {camera.Id} contains a non-finite target offset.",
                    out validationError);

            if (camera.TargetActor is null && camera.TargetBone is null)
            {
                if (camera.TargetOffset != Vector3.Zero)
                    return Fail(
                        $"Camera {camera.Id} has an offset without a target.",
                        out validationError);
            }
            else
            {
                if (camera.TargetActor is { } targetActor &&
                    !actors.Contains(targetActor))
                    return Fail(
                        $"Camera {camera.Id} refers to missing target actor {targetActor}.",
                        out validationError);
                if (camera.TargetBone is { } targetBone)
                {
                    if (!bones.ContainsKey(targetBone))
                        return Fail(
                            $"Camera {camera.Id} refers to missing target bone {targetBone}.",
                            out validationError);
                    if (camera.TargetActor is { } representedActor &&
                        targetBone.Skeleton.Actor != representedActor)
                        return Fail(
                            $"Camera {camera.Id} has contradictory actor and bone targets.",
                            out validationError);
                }
            }
        }

        if (snapshot.Cameras.Count > 0)
        {
            if (liveCount != 1)
                return Fail(
                    "A non-empty camera set must contain exactly one live camera.",
                    out validationError);
            if (defaultCount != 1)
                return Fail(
                    "A non-empty camera set must contain exactly one default camera.",
                    out validationError);
            if (defaultCamera!.Kind != CameraKind.Game)
                return Fail(
                    "The default camera must use the Game camera kind.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    private static bool TryBuildPropIndexes(
        SceneSnapshot snapshot,
        EntityIndex<PropId, PropDescriptor>.Staged props,
        out string? validationError)
    {
        foreach (var prop in snapshot.Props)
        {
            if (prop is null)
                return Fail("Scene contains a null object descriptor.", out validationError);
            if (!IsValidPropId(prop.Id))
                return Fail($"Object id {prop.Id} is invalid.", out validationError);
            if (!props.TryAdd(prop))
                return Fail(
                    $"Scene contains duplicate object {prop.Id.LogicalId:N}.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    /// <summary>Same shape as the prop index and for the same reason: one
    /// lineage per borrowed object, one descriptor per id. A borrowed map
    /// object is not spawned, but the scene addresses it exactly as it
    /// addresses a prop — through an id the session must be able to answer
    /// for, or every transform against it is refused as stale.</summary>
    private static bool TryBuildWorldObjectIndexes(
        SceneSnapshot snapshot,
        EntityIndex<WorldObjectId, WorldObjectDescriptor>.Staged worldObjects,
        out string? validationError)
    {
        foreach (var worldObject in snapshot.WorldObjects)
        {
            if (worldObject is null)
                return Fail(
                    "Scene contains a null world-object descriptor.",
                    out validationError);
            if (!IsValidWorldObjectId(worldObject.Id))
                return Fail(
                    $"World object id {worldObject.Id} is invalid.",
                    out validationError);
            if (!worldObjects.TryAdd(worldObject))
                return Fail(
                    $"Scene contains duplicate world object {worldObject.Id.LogicalId:N}.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    private static bool TryBuildOverlayIndexes(
        SceneSnapshot snapshot,
        EntityIndex<OverlayId, OverlayDescriptor>.Staged overlays,
        out string? validationError)
    {
        foreach (var overlay in snapshot.Overlays)
        {
            if (overlay is null)
                return Fail(
                    "Scene contains a null overlay descriptor.",
                    out validationError);
            if (!IsValidOverlayId(overlay.Id))
                return Fail(
                    $"Overlay id {overlay.Id} is invalid.", out validationError);
            if (!overlays.TryAdd(overlay))
                return Fail(
                    $"Scene contains duplicate overlay {overlay.Id.LogicalId:N}.",
                    out validationError);
            if (!Enum.IsDefined(typeof(OverlayNodeKind), overlay.Kind))
                return Fail(
                    $"Overlay {overlay.Id} has an unknown kind.",
                    out validationError);
        }

        validationError = null;
        return true;
    }

    private static bool TryValidateBoneGraph(
        Dictionary<BoneId, BoneDescriptor> bones,
        out string? validationError)
    {
        var visited = new HashSet<BoneId>();
        foreach (var bone in bones.Keys)
        {
            if (!VisitBone(bone, bones, visited, new HashSet<BoneId>(), out validationError))
                return false;
        }

        validationError = null;
        return true;
    }

    private static bool VisitBone(
        BoneId bone,
        Dictionary<BoneId, BoneDescriptor> bones,
        HashSet<BoneId> visited,
        HashSet<BoneId> visiting,
        out string? validationError)
    {
        if (visited.Contains(bone))
        {
            validationError = null;
            return true;
        }
        if (!visiting.Add(bone))
        {
            validationError = $"Bone parent graph contains a cycle at {bone}.";
            return false;
        }

        if (bones[bone].Parent is { } parent &&
            !VisitBone(parent, bones, visited, visiting, out validationError))
            return false;

        visiting.Remove(bone);
        visited.Add(bone);
        validationError = null;
        return true;
    }

    private bool TryValidateGenerationFloors(
        SceneSnapshot snapshot,
        out string? validationError)
    {
        foreach (var actor in snapshot.Actors)
        {
            if (_actors.FloorViolation(actor) is { } regressed)
                return Fail(regressed, out validationError);

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

    private static bool Fail(string detail, out string? validationError)
    {
        validationError = detail;
        return false;
    }

    private static bool IsValidActorId(ActorId id) => id.LogicalId != Guid.Empty;

    private static bool IsValidSkeletonId(SkeletonId id) =>
        IsValidActorId(id.Actor) &&
        Enum.IsDefined(typeof(PoseSlot), id.Slot) &&
        id.Slot != PoseSlot.Unknown;

    private static bool IsValidBoneId(BoneId id) =>
        IsValidSkeletonId(id.Skeleton) && id.IsValid;

    private static bool IsValidLightId(LightId id) => id.LogicalId != Guid.Empty;

    private static bool IsValidCameraId(CameraId id) => id.LogicalId != Guid.Empty;

    private static bool IsValidPropId(PropId id) => id.LogicalId != Guid.Empty;

    private static bool IsValidWorldObjectId(WorldObjectId id) =>
        id.LogicalId != Guid.Empty;

    private static bool IsValidOverlayId(OverlayId id) =>
        id.LogicalId != Guid.Empty;

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
