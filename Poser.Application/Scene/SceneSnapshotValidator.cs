using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using TransformMath = Poser.Domain.Transforms.TransformMath;

namespace Poser.Application.Scene;

/// <summary>
/// Schema and topology validation for one candidate scene snapshot. It fills
/// the staged indexes it is given and reports the first violation; it reads
/// no committed state, so a rejected candidate leaves the session untouched.
/// Generation floors are committed state and stay with <see cref="SceneSession"/>.
/// </summary>
internal static class SceneSnapshotValidator
{
    /// <summary>One admission's candidate indexes; nothing reads them until
    /// the commit swaps them in.</summary>
    internal sealed record Candidate(
        EntityIndex<ActorId, ActorDescriptor>.Staged Actors,
        Dictionary<BoneId, BoneDescriptor> Bones,
        EntityIndex<LightId, LightDescriptor>.Staged Lights,
        EntityIndex<CameraId, CameraDescriptor>.Staged Cameras,
        EntityIndex<PropId, PropDescriptor>.Staged Props,
        EntityIndex<WorldObjectId, WorldObjectDescriptor>.Staged WorldObjects,
        EntityIndex<OverlayId, OverlayDescriptor>.Staged Overlays,
        Dictionary<ActorId, GazeDescriptor> Gazes);

    /// <summary>Validates <paramref name="snapshot"/> and fills the staged
    /// indexes of <paramref name="candidate"/>.</summary>
    public static bool TryBuildIndexes(
        SceneSnapshot snapshot,
        Candidate candidate,
        out string? validationError)
    {
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

    internal static bool Fail(string detail, out string? validationError)
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

}
