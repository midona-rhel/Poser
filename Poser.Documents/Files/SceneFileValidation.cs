using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Files;

public enum SceneFileValidationFailureKind
{
    Document,
    /// <summary>The file's FileVersion is above what this build understands.</summary>
    FutureVersion,
    Identity,
    CollectionSize,
    Name,
    Relationship,
    NonFiniteNumeric,
    DegenerateQuaternion,
    Range,
    /// <summary>An embedded pose document failed the ordinary pose codec's
    /// validation; the pose failure detail is carried through.</summary>
    EmbeddedPose,
}

public sealed class SceneFileValidationFailure
{
    public SceneFileValidationFailureKind Kind { get; }
    public string Detail { get; }

    private SceneFileValidationFailure(
        SceneFileValidationFailureKind kind, string detail)
    {
        Kind = kind;
        Detail = detail;
    }

    internal static SceneFileValidationFailure Create(
        SceneFileValidationFailureKind kind, string detail) => new(kind, detail);
}

public sealed class SceneFileValidationOutcome
{
    public bool Succeeded { get; }
    public SceneFileValidationFailure? Failure { get; }

    private SceneFileValidationOutcome(
        bool succeeded, SceneFileValidationFailure? failure)
    {
        Succeeded = succeeded;
        Failure = failure;
    }

    internal static SceneFileValidationOutcome Ok() => new(true, null);

    internal static SceneFileValidationOutcome Fail(
        SceneFileValidationFailureKind kind, string detail) =>
        new(false, SceneFileValidationFailure.Create(kind, detail));
}

/// <summary>One entity a load leaves out because its own data is invalid:
/// named, with the reason, beside everything that did restore.</summary>
public sealed record SceneEntityRefusal(SceneOutcomeKind Kind, string Name, string Detail);

/// <summary>
/// Complete-document scene validation: version, identity, bounds, finite
/// numerics, nondegenerate rotations, embedded pose/light/camera documents,
/// and every explicit relationship reference.
///
/// <para>Two strengths over the same checks. <see cref="Validate"/> is
/// strict — any failure refuses — and guards every write and capture.
/// <see cref="ValidateForLoad"/> splits them: DOCUMENT-level failures
/// (version, identity, collection caps, document text, cameras' live/default
/// rules, the group and parent graph) still refuse the file, but an ENTITY
/// whose own data is invalid is removed from the document and named, so one
/// bad light never costs the rest of the scene. Either way nothing that
/// passes can throw during a load's commit.</para>
/// </summary>
public static class SceneFileValidation
{
    public static SceneFileValidationOutcome Validate(SceneFile? scene) =>
        Check(scene, null);

    /// <summary>
    /// The load's validation. Refused entities are REMOVED from
    /// <paramref name="scene"/> (a refused character file, gaze or environment
    /// only drops that part). A reference to a removed entity stays valid —
    /// relationships are checked against every key the file declares — and
    /// the load names it as "not restored", as it does for any category left
    /// out.
    /// </summary>
    public static SceneFileValidationOutcome ValidateForLoad(
        SceneFile? scene, out IReadOnlyList<SceneEntityRefusal> refusals)
    {
        var refused = new List<SceneEntityRefusal>();
        var outcome = Check(scene, refused);
        refusals = refused;
        return outcome;
    }

    private static SceneFileValidationOutcome Check(
        SceneFile? scene, List<SceneEntityRefusal>? refusals)
    {
        if (scene is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene document is missing.");

        if (scene.FileVersion > SceneFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.FutureVersion,
                $"The scene was saved by a newer Poser (file version {scene.FileVersion}, " +
                $"this build reads up to {SceneFile.CurrentVersion}).");
        // The floor is the CURRENT version, not 1. `.xivs` has only ever been
        // written at version 2, so anything lower can only be a development
        // `.poserscene` document that was renamed — and nothing reads those.
        // It takes the ordinary invalid-document refusal; there is no
        // migration shim and no legacy-specific message.
        if (scene.FileVersion < SceneFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.Document,
                $"The scene file version {scene.FileVersion} is invalid.");

        if (scene.SceneId == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "The scene has no scene identity.");

        if (scene.Actors is null || scene.Props is null ||
            scene.Lights is null || scene.Cameras is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "A scene entity collection is missing.");

        if (scene.Actors.Count > SceneFileLimits.MaxActors)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Actors.Count} actors (limit {SceneFileLimits.MaxActors}).");
        if (scene.Props.Count > SceneFileLimits.MaxProps)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Props.Count} objects (limit {SceneFileLimits.MaxProps}).");
        if (scene.Lights.Count > SceneFileLimits.MaxLights)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Lights.Count} lights (limit {SceneFileLimits.MaxLights}).");
        if (scene.Cameras.Count > SceneFileLimits.MaxCameras)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {scene.Cameras.Count} cameras (limit {SceneFileLimits.MaxCameras}).");
        // The overlay list is optional: absent is a scene with no staged
        // nodes, which is every file written before they existed.
        if (scene.Overlays is { } overlayList &&
            overlayList.Count > SceneFileLimits.MaxOverlays)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {overlayList.Count} overlays (limit {SceneFileLimits.MaxOverlays}).");

        // The borrowed-object list is optional for the same reason the overlay
        // list is: absent is a scene that borrowed nothing.
        if (scene.WorldObjects is { } worldObjectList &&
            worldObjectList.Count > SceneFileLimits.MaxWorldObjects)
            return Fail(SceneFileValidationFailureKind.CollectionSize,
                $"The scene contains {worldObjectList.Count} world objects (limit {SceneFileLimits.MaxWorldObjects}).");

        if (!ValidateText(scene.Author, "Author", out var textFailure) ||
            !ValidateText(scene.Description, "Description", out textFailure,
                SceneFileLimits.MaxDescriptionCharacters) ||
            !ValidateText(scene.PlaceName, "PlaceName", out textFailure))
            return textFailure!;

        // The relative-load anchor is a world position like any other, so it
        // takes the same finite check every stated position takes.
        if (scene.Origin is { } origin && !IsFinite(origin))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                "The scene origin is not finite.");

        // Removals are applied at the END: the structure and parent graph is
        // checked against the whole document as the file states it.
        var removals = new List<Action>();
        var refused = new HashSet<object>(ReferenceEqualityComparer.Instance);
        SceneFileValidationOutcome? Entity(
            SceneFileValidationOutcome? failure, SceneOutcomeKind kind,
            string? name, object? entry, Action remove)
        {
            if (failure is null || refusals is null)
                return failure;
            refusals.Add(new(kind,
                string.IsNullOrWhiteSpace(name) ? $"Unnamed {kind.Label().ToLowerInvariant()}" : name,
                failure.Failure!.Detail));
            removals.Add(remove);
            if (entry is not null)
                refused.Add(entry);
            return null;
        }

        var actorKeys = new HashSet<Guid>();
        foreach (var actor in scene.Actors)
        {
            if (Entity(ValidateActor(actor, actorKeys), SceneOutcomeKind.Actor,
                    actor?.Name, actor, () => scene.Actors.Remove(actor!)) is { } failure)
                return failure;
        }

        // There is deliberately NO whole-document appearance cap. Payloads are
        // streamed container entries, so ten actors cost ten files' worth of
        // disk and nothing else; a total cap here would refuse a save the user
        // asked for in order to protect a memory budget that no longer exists.

        // Gaze references another ACTOR, so it can only be checked once every
        // actor key is known — a forward reference is as valid as a backward
        // one. A character file and a gaze are PARTS of an actor: refusing
        // one drops that part and keeps the actor.
        foreach (var actor in scene.Actors)
        {
            if (actor is null || refused.Contains(actor))
                continue;
            if (actor.Mcdf is { } mcdf &&
                Entity(ValidateMcdf(mcdf, $"Actor '{actor.Name}' character file"),
                    SceneOutcomeKind.CharacterFile, actor.Name, null,
                    () => actor.Mcdf = null) is { } mcdfFailure)
                return mcdfFailure;
            if (actor.Gaze is { } gaze &&
                Entity(ValidateGaze(gaze, actorKeys, $"Actor '{actor.Name}' gaze"),
                    SceneOutcomeKind.Gaze, actor.Name, null,
                    () => actor.Gaze = null) is { } gazeFailure)
                return gazeFailure;
        }

        var keys = new HashSet<Guid>();
        foreach (var prop in scene.Props)
        {
            if (Entity(ValidateProp(prop, keys), SceneOutcomeKind.Object,
                    prop?.Name, prop, () => scene.Props.Remove(prop!)) is { } failure)
                return failure;
        }

        if (scene.Overlays is { } overlays)
        {
            keys.Clear();
            foreach (var overlay in overlays)
            {
                if (Entity(ValidateOverlay(overlay, keys), SceneOutcomeKind.Overlay,
                        overlay?.Node?.Name, overlay, () => overlays.Remove(overlay!)) is { } failure)
                    return failure;
            }
        }

        if (scene.WorldObjects is { } worldObjects)
        {
            keys.Clear();
            foreach (var worldObject in worldObjects)
            {
                string? name = worldObject is { Name.Length: > 0 } ? worldObject.Name : worldObject?.Path;
                if (Entity(ValidateWorldObject(worldObject, keys), SceneOutcomeKind.WorldObject,
                        name, worldObject, () => worldObjects.Remove(worldObject!)) is { } failure)
                    return failure;
            }
        }

        keys.Clear();
        foreach (var light in scene.Lights)
        {
            if (Entity(ValidateLight(light, keys, actorKeys), SceneOutcomeKind.Light,
                    light?.Light?.Name, light, () => scene.Lights.Remove(light!)) is { } failure)
                return failure;
        }

        keys.Clear();
        var liveCount = 0;
        var defaultCount = 0;
        bool cameraRefused = false;
        SceneCamera? defaultCamera = null;
        foreach (var camera in scene.Cameras)
        {
            if (Entity(ValidateCamera(camera, keys, actorKeys), SceneOutcomeKind.Camera,
                    camera?.Camera?.Name, camera, () => scene.Cameras.Remove(camera!)) is { } failure)
                return failure;
            if (camera is null || refused.Contains(camera))
            {
                cameraRefused = true;
                continue;
            }
            if (camera.IsLive)
                liveCount++;
            if (camera.IsDefault)
            {
                defaultCount++;
                defaultCamera = camera;
            }
        }

        if (scene.Cameras.Count > 0)
        {
            // A refused live camera leaves none live: the load then keeps the
            // default camera live rather than refusing the file for it.
            if (liveCount > 1 || liveCount == 0 && !cameraRefused)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "A scene with cameras must mark exactly one camera live.");
            // A whole scene carries the session's default camera; a camera
            // entry carries a created one and has none — its load creates
            // the camera rather than overwriting the default.
            if (defaultCount > 1)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "A scene must not mark more than one camera as the default.");
            if (defaultCamera is not null && defaultCamera.Camera!.Kind != CameraKind.Game)
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "The default camera must use the Game camera kind.");
        }

        if (scene.Environment is { } environment &&
            Entity(ValidateEnvironment(environment), SceneOutcomeKind.Environment,
                "Environment", null, () => scene.Environment = null) is { } environmentFailure)
            return environmentFailure;

        if (ValidateStructure(scene) is { } structureFailure)
            return structureFailure;

        foreach (var remove in removals)
            remove();
        return SceneFileValidationOutcome.Ok();
    }

    /// <summary>
    /// The sidebar structure, unconditionally: every group, member and root
    /// slot the load's structure restore dereferences is present, group keys
    /// are unique, and nesting names existing groups without a cycle. A
    /// member or slot of an unknown KIND is legal — a future
    /// kind reads and is skipped — but a missing one is not. Then the
    /// optional transform state and the parent links.
    /// </summary>
    private static SceneFileValidationOutcome? ValidateStructure(SceneFile scene)
    {
        var groups = scene.Groups ?? [];
        var groupKeys = new HashSet<Guid>();
        foreach (var group in groups)
        {
            if (group is null)
                return Fail(SceneFileValidationFailureKind.Document,
                    "The scene contains a null group entry.");
            if (group.Key == Guid.Empty || !groupKeys.Add(group.Key))
                return Fail(SceneFileValidationFailureKind.Identity,
                    $"Group '{group.Name}' has a missing or duplicate key.");
            if (group.Name is null || group.Members is null || group.Members.Any(member => member?.Kind is null))
                return Fail(SceneFileValidationFailureKind.Document,
                    $"Group '{group.Name}' has a missing member.");
        }
        foreach (var group in groups)
        {
            var visited = new HashSet<Guid>();
            for (var current = group; current.Parent is { } parent;
                 current = groups.First(candidate => candidate.Key == parent))
            {
                if (!groupKeys.Contains(parent))
                    return Fail(SceneFileValidationFailureKind.Relationship,
                        $"Group '{group.Name}' nests in a group the scene does not have.");
                if (!visited.Add(current.Key))
                    return Fail(SceneFileValidationFailureKind.Relationship,
                        $"Group '{group.Name}' is nested inside itself.");
            }
        }
        foreach (var slot in scene.RootOrder ?? [])
        {
            if (slot?.Kind is null)
                return Fail(SceneFileValidationFailureKind.Document,
                    "The scene's sidebar order has a missing entry.");
            if (slot.Kind == "group" && !groupKeys.Contains(slot.Key))
                return Fail(SceneFileValidationFailureKind.Relationship,
                    "The scene's sidebar order names a group the scene does not have.");
        }

        if (SceneGroupTransformCodec.Validate(scene) is { } groupFailure)
            return Fail(SceneFileValidationFailureKind.Relationship, groupFailure);
        if (SceneParenting.Validate(scene) is { } parentingFailure)
            return Fail(SceneFileValidationFailureKind.Relationship, parentingFailure);
        foreach (var group in groups)
        {
            if (group.InitialFrameRotation is { } frame &&
                (!IsFinite(frame) ||
                 frame.LengthSquared() < SceneFileLimits.MinQuaternionLengthSquared))
                return Fail(SceneFileValidationFailureKind.DegenerateQuaternion,
                    $"Group '{group.Name}' has an invalid initial frame rotation.");
            if (group.Transform is { } transform)
            {
                if (!IsFinite(transform.FrameOrigin) ||
                    !IsFinite(transform.Position) ||
                    !IsFinite(transform.FrameRotation) ||
                    !IsFinite(transform.Rotation) ||
                    !IsFinite(transform.SpacingScale) ||
                    !IsFinite(transform.OwnScale) ||
                    !TransformMath.IsValidRotation(transform.FrameRotation) ||
                    !TransformMath.IsValidRotation(transform.Rotation))
                    return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                        $"Group '{group.Name}' has an invalid transform state.");
                foreach (var member in transform.Members)
                    if (!member.Initial.IsValid || !member.Expected.IsValid)
                        return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                            $"Group '{group.Name}' has an invalid member transform.");
            }
        }
        return null;
    }

    private static SceneFileValidationOutcome? ValidateActor(
        SceneActor? actor, HashSet<Guid> keys)
    {
        if (actor is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null actor entry.");
        if (actor.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                $"Actor '{actor.Name}' has no key.");
        if (!keys.Add(actor.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate actor key {actor.Key:N}.");
        if (!ValidateRequiredName(actor.Name, $"Actor {actor.Key:N}", out var nameFailure))
            return nameFailure;
        if (actor.ModelCharaId < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Actor '{actor.Name}' has a negative model id.");
        if (actor.CompanionKind is { } companionKind && !Enum.IsDefined(companionKind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"Actor '{actor.Name}' has an unknown companion kind.");
        if (actor.CompanionKind is null && actor.CompanionId != 0)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"Actor '{actor.Name}' carries a companion id without a companion kind.");
        if (actor.CompanionKind is not null && !actor.HasCompanionSlot)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"Actor '{actor.Name}' has a companion attachment but no companion slot.");
        // A companion pose has nothing to land on without the attachment that
        // brings the body back.
        if (actor.CompanionPose is not null && actor.CompanionKind is null)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"Actor '{actor.Name}' carries a companion pose without a companion attachment.");

        if (actor.Pose is null)
            return Fail(SceneFileValidationFailureKind.EmbeddedPose,
                $"Actor '{actor.Name}' has no embedded pose document.");
        if (actor.Fabrik is { } chains && (chains.Count > 512 || chains.Any(chain => chain is null
            || chain.Config is null || chain.Config.Solver is not (Poser.Domain.Posing.IkSolver.Fabrik or Poser.Domain.Posing.IkSolver.Rope)
            || chain.Config.Fabrik == null || chain.Config.Validate() != null
            || string.IsNullOrWhiteSpace(chain.Endpoint) || chain.Partial < 0 || !Enum.IsDefined(chain.Slot))))
            return Fail(SceneFileValidationFailureKind.EmbeddedPose, $"Actor '{actor.Name}' has invalid FABRIK state.");
        var pose = PoseFileValidation.Validate(actor.Pose);
        if (!pose.Succeeded)
            return Fail(SceneFileValidationFailureKind.EmbeddedPose,
                $"Actor '{actor.Name}' pose: {pose.Failure!.Detail}");

        if (actor.CompanionPose is { } companionPose)
        {
            var companion = PoseFileValidation.Validate(companionPose);
            if (!companion.Succeeded)
                return Fail(SceneFileValidationFailureKind.EmbeddedPose,
                    $"Actor '{actor.Name}' companion pose: {companion.Failure!.Detail}");
        }

        if (actor.ModelTransform is { } placement &&
            ValidateTransform(placement, $"Actor '{actor.Name}' placement")
                is { } placementFailure)
            return placementFailure;

        return null;
    }

    /// <summary>
    /// A stated character file must be followable in exactly one of its two
    /// modes. A REFERENCE with no path names nothing. A PORTABLE payload
    /// without a digest cannot be checked against its own bytes, and an
    /// unchecked payload is one an actor would wear on trust — so the digest
    /// is required there, and the per-actor byte cap is enforced here rather
    /// than at the file cap, where the only thing a refusal could say is a
    /// number. A hash that is neither absent nor a SHA-256 digest could only
    /// mislead a staleness check.
    /// </summary>
    private static SceneFileValidationOutcome? ValidateMcdf(
        SceneActorMcdf mcdf, string label)
    {
        if (mcdf.IsPortable)
        {
            if (mcdf.PackageBytes < 0 ||
                mcdf.PackageBytes > SceneFileLimits.MaxEmbeddedAppearanceBytes)
                return Fail(SceneFileValidationFailureKind.Range,
                    $"{label} embeds {mcdf.PackageBytes:N0} bytes, over the " +
                    $"{SceneFileLimits.MaxEmbeddedAppearanceBytes:N0} byte " +
                    "limit for one actor.");
            if (mcdf.ContentHash is not { Length: SceneFileLimits.ContentHashCharacters } digest ||
                !digest.All(Uri.IsHexDigit))
                return Fail(SceneFileValidationFailureKind.Document,
                    $"{label} embeds a package with no SHA-256 digest to check " +
                    "it against.");
            // The entry is NAMED by the digest. Any other name could point the
            // import at another actor's payload, or at the document itself.
            if (!string.Equals(mcdf.PackageEntry,
                    SceneFileStore.AppearanceEntry(digest), StringComparison.Ordinal))
                return Fail(SceneFileValidationFailureKind.Document,
                    $"{label} payload entry is not the one its digest names.");
        }
        else if (string.IsNullOrWhiteSpace(mcdf.Path))
        {
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} states no path.");
        }

        if (mcdf.Path.Length > SceneFileLimits.MaxPathCharacters)
            return Fail(SceneFileValidationFailureKind.Name,
                $"{label} path exceeds {SceneFileLimits.MaxPathCharacters} characters.");
        if (!ValidateText(mcdf.FileName, $"{label} name", out var nameFailure))
            return nameFailure;
        if (mcdf.ContentHash.Length != 0 &&
            mcdf.ContentHash.Length != SceneFileLimits.ContentHashCharacters)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} content hash is not a SHA-256 digest.");
        return null;
    }

    private static SceneFileValidationOutcome? ValidateOverlay(
        SceneOverlay? overlay, HashSet<Guid> keys)
    {
        if (overlay is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null overlay entry.");
        if (overlay.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "An overlay has no key.");
        if (!keys.Add(overlay.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate overlay key {overlay.Key:N}.");
        if (overlay.Node is not { } node)
            return Fail(SceneFileValidationFailureKind.Document,
                $"Overlay {overlay.Key:N} has no node document.");
        if (!ValidateRequiredName(
                node.Name, $"Overlay {overlay.Key:N}", out var nameFailure))
            return nameFailure;
        if (!Enum.IsDefined(node.Kind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"Overlay '{node.Name}' names an unknown kind.");
        if (!Enum.IsDefined(node.TalkBackground) ||
            !Enum.IsDefined(node.TalkCursor) ||
            !Enum.IsDefined(node.BalloonChannel) ||
            !Enum.IsDefined(node.BalloonGradient) ||
            !Enum.IsDefined(node.StatusKind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"Overlay '{node.Name}' names an unknown style.");
        if (!float.IsFinite(node.Position.X) ||
            !float.IsFinite(node.Position.Y) ||
            !float.IsFinite(node.Scale) ||
            !float.IsFinite(node.Alpha) ||
            !float.IsFinite(node.ArrowX))
            return Fail(SceneFileValidationFailureKind.Range,
                $"Overlay '{node.Name}' carries a non-finite value.");
        if (node.Text.Length > OverlayNodeLimits.MaxTextCharacters)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Overlay '{node.Name}' carries {node.Text.Length} characters " +
                $"(limit {OverlayNodeLimits.MaxTextCharacters}).");
        return null;
    }

    private static SceneFileValidationOutcome? ValidateGaze(
        SceneActorGaze gaze, HashSet<Guid> actorKeys, string label)
    {
        if (!Enum.IsDefined(gaze.Mode))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} names an unknown mode.");
        if ((gaze.Parts & ~GazeTargetType.All) != 0 ||
            (gaze.LockedParts & ~GazeTargetType.All) != 0)
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} names an unknown part.");
        if (!IsFinite(gaze.Position) || !IsFinite(gaze.EyesPosition) ||
            !IsFinite(gaze.HeadPosition) || !IsFinite(gaze.BodyPosition))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} contains NaN or infinity.");
        if (gaze.TargetActorKey is { } target && !actorKeys.Contains(target))
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} follows missing actor {target:N}.");
        // A target belongs to Entity mode alone; any other mode carrying one
        // would restore a follow the file does not describe.
        if (gaze.TargetActorKey is not null && gaze.Mode != GazeTargetMode.Entity)
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} carries a target actor outside Entity mode.");
        return null;
    }

    private static SceneFileValidationOutcome? ValidateProp(
        SceneProp? prop, HashSet<Guid> keys)
    {
        if (prop is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null object entry.");
        if (prop.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                $"Object '{prop.Name}' has no key.");
        if (!keys.Add(prop.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate object key {prop.Key:N}.");
        if (!ValidateRequiredName(prop.Name, $"Object {prop.Key:N}", out var nameFailure))
            return nameFailure;
        if (ValidateTransform(prop.Transform, $"Object '{prop.Name}'") is { } failure)
            return failure;
        return null;
    }

    private static SceneFileValidationOutcome? ValidateWorldObject(
        SceneWorldObject? worldObject, HashSet<Guid> keys)
    {
        if (worldObject is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null world object entry.");
        if (worldObject.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "A world object has no key.");
        if (!keys.Add(worldObject.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate world object key {worldObject.Key:N}.");
        // The path is HALF THE IDENTITY, not a label: an entry without one
        // names nothing a load could ever match.
        if (!ValidateRequiredName(
                worldObject.Path,
                $"World object {worldObject.Key:N}",
                out var nameFailure))
            return nameFailure;
        if (!IsFinite(worldObject.MapPosition))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"World object '{worldObject.Path}' map position is not finite.");
        if (ValidateTransform(
                worldObject.Transform,
                $"World object '{worldObject.Path}'") is { } failure)
            return failure;
        // These reach native writes as they stand: a NaN or a negative speed
        // stops here, not in the renderer.
        if (!float.IsFinite(worldObject.Opacity) || worldObject.Opacity is < 0f or > 1f ||
            !float.IsFinite(worldObject.VfxSpeed) || worldObject.VfxSpeed is < 0f or > MaxVfxScale ||
            !float.IsFinite(worldObject.VfxIntensity) || worldObject.VfxIntensity is < 0f or > MaxVfxScale ||
            worldObject.Tint is { } tint && !IsFinite(tint))
            return Fail(SceneFileValidationFailureKind.Range,
                $"World object '{worldObject.Path}' has an invalid opacity, tint, speed or intensity.");
        if (worldObject.FurnitureLights is null ||
            worldObject.FurnitureLights.Any(light => light.Key is null))
            return Fail(SceneFileValidationFailureKind.Document,
                $"World object '{worldObject.Path}' has a missing furniture light.");
        return null;
    }

    private static SceneFileValidationOutcome? ValidateLight(
        SceneLight? light, HashSet<Guid> keys, HashSet<Guid> actorKeys)
    {
        if (light is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null light entry.");
        if (light.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "A scene light has no key.");
        if (!keys.Add(light.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate light key {light.Key:N}.");
        if (light.Light is not { } document)
            return Fail(SceneFileValidationFailureKind.Document,
                $"Light {light.Key:N} has no embedded light document.");

        var label = $"Light '{document.Name}'";
        if (!ValidateRequiredName(document.Name, label, out var nameFailure))
            return nameFailure;
        if (document.FileVersion is < 0 or > LightFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has an unsupported light file version {document.FileVersion}.");
        if (!Enum.IsDefined(document.Kind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown light kind.");
        if (!Enum.IsDefined(document.FalloffType))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown falloff type.");
        if (document.Transform is null)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has no transform.");
        if (ValidateTransform(document.Transform, label) is { } transformFailure)
            return transformFailure;
        if (!IsFinite(document.Color) ||
            !AllFinite(document.Intensity, document.Range, document.Falloff,
                document.SpotAngle, document.FalloffAngle,
                document.CharacterShadowRange, document.ShadowPlaneNear,
                document.ShadowPlaneFar) ||
            !IsFinite(document.AreaAngle))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} contains NaN or infinity.");
        if (!ValidateText(document.Gobo, $"{label} gobo path", out var goboFailure))
            return goboFailure;

        if (light.Attachment is { } attachment)
        {
            if (ValidateAttachment(attachment, actorKeys, label) is { } failure)
                return failure;
        }

        return null;
    }

    private static SceneFileValidationOutcome? ValidateAttachment(
        SceneBoneAttachment attachment, HashSet<Guid> actorKeys, string label)
    {
        if (!actorKeys.Contains(attachment.ActorKey))
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} is attached to missing actor {attachment.ActorKey:N}.");
        if (!Enum.IsDefined(attachment.Slot) || attachment.Slot == PoseSlot.Unknown)
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} attachment has an unknown slot.");
        if (attachment.PartialId < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} attachment has a negative partial id.");
        if (!ValidateRequiredName(
                attachment.BoneName, $"{label} attachment bone", out var failure))
            return failure;
        return null;
    }

    private static SceneFileValidationOutcome? ValidateCamera(
        SceneCamera? camera, HashSet<Guid> keys, HashSet<Guid> actorKeys)
    {
        if (camera is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The scene contains a null camera entry.");
        if (camera.Key == Guid.Empty)
            return Fail(SceneFileValidationFailureKind.Identity,
                "A scene camera has no key.");
        if (!keys.Add(camera.Key))
            return Fail(SceneFileValidationFailureKind.Identity,
                $"The scene contains duplicate camera key {camera.Key:N}.");
        if (camera.Camera is not { } document)
            return Fail(SceneFileValidationFailureKind.Document,
                $"Camera {camera.Key:N} has no embedded camera document.");

        var label = $"Camera '{document.Name}'";
        if (!ValidateRequiredName(document.Name, label, out var nameFailure))
            return nameFailure;
        if (document.FileVersion is < 0 or > CameraFile.CurrentVersion)
            return Fail(SceneFileValidationFailureKind.Document,
                $"{label} has an unsupported camera file version {document.FileVersion}.");
        if (!Enum.IsDefined(document.Kind))
            return Fail(SceneFileValidationFailureKind.Range,
                $"{label} has an unknown camera kind.");
        if (!IsFinite(document.Angle) || !IsFinite(document.Pan) ||
            !AllFinite(document.Roll, document.Zoom, document.FoV,
                document.MovementSpeed, document.MouseSensitivity,
                document.OrthographicZoom) ||
            !IsFinite(document.PositionOffset) ||
            // Absent is legal — an unpinned camera has no fixed position at
            // all — but a present one is held to the same finiteness as every
            // other coordinate in the document.
            (document.FixedPosition is { } pinned && !IsFinite(pinned)) ||
            !IsFinite(document.Position) ||
            !IsFinite(document.Rotation))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} contains NaN or infinity.");

        if (!IsFinite(camera.TargetOffset))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} target offset contains NaN or infinity.");
        if (!ValidateText(camera.TargetActorName, $"{label} target name", out var targetNameFailure))
            return targetNameFailure;
        if (camera.TargetActorKey is { } target)
        {
            if (!actorKeys.Contains(target))
                return Fail(SceneFileValidationFailureKind.Relationship,
                    $"{label} follows missing actor {target:N}.");
        }
        else if (camera.TargetOffset != Vector3.Zero ||
                 !string.IsNullOrEmpty(camera.TargetActorName) ||
                 camera.IsTargetLocked)
        {
            return Fail(SceneFileValidationFailureKind.Relationship,
                $"{label} carries target state without a target actor.");
        }

        return null;
    }

    private static SceneFileValidationOutcome? ValidateEnvironment(
        SceneEnvironment environment)
    {
        if (environment.MinuteOfDay is < 0 or > 1439)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Environment minute {environment.MinuteOfDay} is outside 0..1439.");
        if (environment.DayOfMonth is < 1 or > 31)
            return Fail(SceneFileValidationFailureKind.Range,
                $"Environment day {environment.DayOfMonth} is outside 1..31.");
        if (!float.IsFinite(environment.TransitionTime) ||
            environment.TransitionTime < 0)
            return Fail(SceneFileValidationFailureKind.Range,
                "The environment weather transition time is invalid.");

        if (environment.HeldSections is null)
            return Fail(SceneFileValidationFailureKind.Document,
                "The environment held-section list is missing.");
        var held = new HashSet<EnvSection>();
        foreach (var section in environment.HeldSections)
        {
            if (!Enum.IsDefined(section))
                return Fail(SceneFileValidationFailureKind.Range,
                    "The environment holds an unknown section.");
            if (!held.Add(section))
                return Fail(SceneFileValidationFailureKind.Document,
                    $"The environment holds section {section} twice.");
        }

        // A held section carries its values; an unheld one carries nothing.
        if (ValidateSectionPresence(held, EnvSection.Sky,
                environment.Sky.HasValue) is { } presence)
            return presence;
        if (ValidateSectionPresence(held, EnvSection.Clouds,
                environment.Clouds.HasValue) is { } clouds)
            return clouds;
        if (ValidateSectionPresence(held, EnvSection.Lighting,
                environment.Lighting.HasValue) is { } lighting)
            return lighting;
        if (ValidateSectionPresence(held, EnvSection.Fog,
                environment.Fog.HasValue) is { } fog)
            return fog;
        if (ValidateSectionPresence(held, EnvSection.Rain,
                environment.Rain.HasValue) is { } rain)
            return rain;
        if (ValidateSectionPresence(held, EnvSection.Particles,
                environment.Particles.HasValue) is { } particles)
            return particles;
        if (ValidateSectionPresence(held, EnvSection.Stars,
                environment.Stars.HasValue) is { } stars)
            return stars;
        if (ValidateSectionPresence(held, EnvSection.Wind,
                environment.Wind.HasValue) is { } wind)
            return wind;

        if (!SectionValuesFinite(environment))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                "An environment section contains NaN or infinity.");

        return null;
    }

    private static SceneFileValidationOutcome? ValidateSectionPresence(
        HashSet<EnvSection> held, EnvSection section, bool hasValues)
    {
        if (held.Contains(section) == hasValues)
            return null;
        return hasValues
            ? Fail(SceneFileValidationFailureKind.Document,
                $"Environment section {section} carries values without being held.")
            : Fail(SceneFileValidationFailureKind.Document,
                $"Held environment section {section} carries no values.");
    }

    private static bool SectionValuesFinite(SceneEnvironment environment)
    {
        if (environment.Sky is { } sky && !float.IsFinite(sky.SunVisibility))
            return false;
        if (environment.Clouds is { } clouds &&
            (!IsFinite(clouds.CloudColor1) || !IsFinite(clouds.CloudColor2) ||
             !AllFinite(clouds.ShadowStop, clouds.CloudHeight)))
            return false;
        if (environment.Lighting is { } lighting &&
            (!IsFinite(lighting.SunlightColor) ||
             !IsFinite(lighting.MoonlightColor) ||
             !IsFinite(lighting.AmbientColor) ||
             !AllFinite(lighting.Unknown1, lighting.AmbientSaturation,
                 lighting.AmbientTemperature, lighting.Unknown2,
                 lighting.LightDistance, lighting.Unknown4)))
            return false;
        if (environment.Fog is { } fog &&
            (!IsFinite(fog.Color) ||
             !AllFinite(fog.Distance, fog.Thickness, fog.SkySmoothness,
                 fog.SkyOpacity, fog.FogOpacity, fog.SunVisibility)))
            return false;
        if (environment.Rain is { } rain &&
            (!IsFinite(rain.Color) ||
             !AllFinite(rain.Raindrops, rain.Intensity, rain.Weight,
                 rain.Scatter, rain.Unknown1, rain.Size, rain.Unknown2,
                 rain.Unknown3)))
            return false;
        if (environment.Particles is { } particles &&
            (!IsFinite(particles.Color) ||
             !AllFinite(particles.Unknown1, particles.Intensity,
                 particles.Weight, particles.Spread, particles.Speed,
                 particles.Size, particles.Glow, particles.Spin)))
            return false;
        if (environment.Stars is { } stars &&
            (!IsFinite(stars.MoonColor) ||
             !AllFinite(stars.ConstellationIntensity, stars.ConstellationCount,
                 stars.StarCount, stars.GalaxyIntensity, stars.StarIntensity,
                 stars.MoonBrightness)))
            return false;
        if (environment.Wind is { } wind &&
            !AllFinite(wind.Direction, wind.Angle, wind.Speed))
            return false;
        return true;
    }

    private static SceneFileValidationOutcome? ValidateTransform(
        LightFile.TransformData transform, string label)
    {
        if (!IsFinite(transform.Position) || !IsFinite(transform.Scale) ||
            !IsFinite(transform.Rotation))
            return Fail(SceneFileValidationFailureKind.NonFiniteNumeric,
                $"{label} transform contains NaN or infinity.");
        if (transform.Rotation.LengthSquared() <
            SceneFileLimits.MinQuaternionLengthSquared)
            return Fail(SceneFileValidationFailureKind.DegenerateQuaternion,
                $"{label} rotation is degenerate.");
        return null;
    }

    private static bool ValidateRequiredName(
        string? name, string label, out SceneFileValidationOutcome? failure)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            failure = Fail(SceneFileValidationFailureKind.Name,
                $"{label} has no name.");
            return false;
        }
        return ValidateText(name, label, out failure);
    }

    /// <summary>A spawned effect's speed and intensity: far beyond what the
    /// sliders reach, short of anything that is not a deliberate value.</summary>
    private const float MaxVfxScale = 100f;

    private static bool ValidateText(
        string? text, string label, out SceneFileValidationOutcome? failure,
        int limit = SceneFileLimits.MaxNameCharacters)
    {
        if (text is not null && text.Length > limit)
        {
            failure = Fail(SceneFileValidationFailureKind.Name,
                $"{label} exceeds {limit} characters.");
            return false;
        }
        failure = null;
        return true;
    }

    private static bool AllFinite(params float[] values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                return false;
        }
        return true;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static SceneFileValidationOutcome Fail(
        SceneFileValidationFailureKind kind, string detail) =>
        SceneFileValidationOutcome.Fail(kind, detail);
}
