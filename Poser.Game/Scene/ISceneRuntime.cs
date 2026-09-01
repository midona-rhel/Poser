using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>Which whole-scene operation a progress/receipt pair describes.</summary>
public enum SceneOperationKind
{
    Save,
    Load,
}

/// <summary>Phases of the single scene transaction, in execution order.</summary>
public enum ScenePhase
{
    /// <summary>The save's FIRST phase: the raw bone-transform caches every
    /// actor's pose is read out of are re-armed and given the update pass they
    /// need. It exists because the caches are only current for skeletons the
    /// per-frame rebuild qualified — see
    /// <see cref="Posing.PoseExportCapture"/>.</summary>
    RefreshingPoses,
    Capturing,
    Writing,
    Reading,
    SpawningEntities,
    AwaitingActors,
    /// <summary>Re-importing saved character files. It runs BEFORE everything
    /// that hangs off an actor's body, because an MCDF import redraws the
    /// actor and takes its draw object — and every skeleton — with it.
    /// </summary>
    ApplyingAppearance,
    ApplyingRelationships,
    /// <summary>Stopping every actor so its pose lands on a held frame.
    /// Scenes restore a picture, not a performance.</summary>
    FreezingActors,
    ApplyingPose,
    ApplyingPresentation,
    ApplyingCameras,
    ApplyingLights,
    ApplyingEnvironment,
    Committing,
    RollingBack,
    Completed,
    RolledBack,
    Failed,
    Cancelled,
}

/// <summary>
/// What sealing left behind: the per-actor notes, and every TEMPORARY package
/// it created that the writer still has to stream into the container. The
/// caller deletes them once the write is done — deleting them here would
/// delete the bytes the save is about to store.
/// </summary>
public sealed record SceneSealOutcome(
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> TemporaryFiles);

/// <summary>One entity's typed restore outcome. A missing parent or
/// resource is a named, explained refusal here — never a silent detach or a
/// silently skipped row.
///
/// <para><see cref="Detail"/> is what happened; <see cref="Remedy"/> is what
/// the user can do about it. A refused entity carries BOTH — a row that only
/// restates the entity's own name is the defect issue #41 reported — and the
/// workflow fills the remedy in from <c>SceneEntityRemedy</c> at the terminal
/// publication, so the result list and the operation log say the same thing.
/// </para></summary>
public sealed record SceneEntityOutcome(
    string Kind,
    string Name,
    bool Restored,
    string? Detail = null,
    string? Remedy = null);

/// <summary>
/// Immutable terminal outcome of one scene operation. The state is the SAME
/// <see cref="OperationReceiptState"/> the published <see cref="OperationReceipt"/>
/// carries — there is one terminal vocabulary, not a parallel scene one:
/// <c>Applied</c> everything restored, <c>RolledBack</c> a structural refusal
/// whose rollback fully undid this operation, <c>Cancelled</c> the user or a
/// session replacement stopped it, <c>Failed</c> entities were restored but
/// named refusals remain (typed partial recovery) or a rollback left leftovers.
/// </summary>
public sealed record SceneOutcome(
    OperationReceiptState State,
    string Detail,
    IReadOnlyList<SceneEntityOutcome> Entities,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> RecoveryEvidencePaths)
{
    public bool Success => State == OperationReceiptState.Applied;

    /// <summary>Whether anything this operation created still exists. Only a
    /// completed rollback and a clean cancel leave nothing behind.</summary>
    public bool LeftEntitiesBehind => State is not (
        OperationReceiptState.RolledBack or OperationReceiptState.Cancelled);
}

/// <summary>Immutable snapshot of the single running (or last finished)
/// scene operation.</summary>
public sealed record SceneProgress(
    SceneOperationKind Kind,
    string FileName,
    ScenePhase Phase,
    int EntitiesDone,
    int EntitiesTotal,
    bool Cancellable,
    SceneOutcome? Outcome);

/// <summary>
/// How one actor's saved character-file restore ended. Restored WITH a detail
/// is the deliberate middle state: the package still exists but its bytes
/// changed since the save, so the actor gets the appearance and the load says
/// the file is not the one it was saved against. Nothing here is ever silent —
/// a missing package is a refusal by name, never a skipped actor.
/// </summary>
public readonly record struct SceneMcdfOutcome(bool Restored, string? Detail)
{
    /// <summary>The actor states no character file; nothing happened and
    /// there is nothing to report.</summary>
    public static SceneMcdfOutcome Silent => new(true, null);

    public static SceneMcdfOutcome Ok(string? detail = null) => new(true, detail);

    public static SceneMcdfOutcome Refused(string detail) => new(false, detail);
}

/// <summary>Typed admission result for starting a scene operation.</summary>
public readonly record struct SceneActionResult(bool Success, string? Detail = null)
{
    public static SceneActionResult Ok() => new(true);
    public static SceneActionResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// What a destroy-first clear actually removed, per kind. It is counted rather
/// than assumed because the clear is the ONE part of a load that cannot be
/// rolled back, so the outcome has to be able to say exactly what it cost.
/// </summary>
public readonly record struct SceneClearOutcome(
    int Actors, int Props, int Overlays, int Lights, int Cameras,
    int WorldObjects = 0,
    IReadOnlyList<string>? UnclearableActors = null)
{
    public int Total =>
        Actors + Props + Overlays + Lights + Cameras + WorldObjects;

    /// <summary>Actors the clear could not remove, BY NAME. The clear takes
    /// everything the session holds, so this is the exception path — a stale
    /// wrapper, a companion body, the GPose primary — and it is never silent:
    /// the user asked for an empty session and has to know what is left.
    /// </summary>
    public IReadOnlyList<string> Refused =>
        UnclearableActors ?? Array.Empty<string>();

    /// <summary>The clear in the user's words, or null when it removed
    /// nothing AND left nothing behind — an empty session needs no sentence
    /// about being emptied, but a session that could not be emptied always
    /// needs one.
    ///
    /// <para>Borrowed map objects are counted but spoken of SEPARATELY, and
    /// never as destroyed: a clear gives them back to the map exactly where it
    /// had them, which is the one part of a clear that costs the user
    /// nothing.</para>
    /// </summary>
    public string? Summary()
    {
        if (Total == 0 && Refused.Count == 0)
            return null;
        var parts = new List<string>(5);
        void Part(int count, string singular, string plural)
        {
            if (count > 0)
                parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }
        Part(Actors, "actor", "actors");
        Part(Props, "object", "objects");
        Part(Overlays, "overlay", "overlays");
        Part(Lights, "light", "lights");
        Part(Cameras, "camera", "cameras");

        string borrowed = WorldObjects == 0
            ? string.Empty
            : $" {WorldObjects} borrowed map " +
                $"{(WorldObjects == 1 ? "object was" : "objects were")} put back.";
        if (parts.Count == 0)
            return $"Cleared the session first:{borrowed}{Left()}".TrimEnd();
        // The verb agrees with what was actually destroyed, the way the
        // borrowed-object line below already does. "1 actor were destroyed" is
        // the only place the two disagreed.
        int destroyed = Actors + Props + Overlays + Lights + Cameras;
        return $"Cleared the session first: {string.Join(", ", parts)} " +
            (destroyed == 1 ? "was" : "were") +
            " destroyed. Undoing the load does not bring " +
            (destroyed == 1 ? "it" : "them") +
            $" back.{borrowed}{Left()}";
    }

    /// <summary>What the clear could not take, named. Empty when it took
    /// everything.</summary>
    private string Left()
    {
        if (Refused.Count == 0)
            return string.Empty;
        return $" {string.Join(", ", Refused)} " +
            (Refused.Count == 1 ? "is" : "are") +
            " still in the scene: the removal was refused. Remove " +
            (Refused.Count == 1 ? "it" : "them") +
            " through GPose before loading, or the scene will load on top.";
    }
}

/// <summary>
/// The native/persistence seam under <see cref="SceneWorkflow"/>. The
/// workflow owns the transaction — admission, phases, guards, rollback,
/// publication — while this seam owns every native materialization and file
/// operation. Entity tokens are opaque to the workflow: it holds them only to
/// hand back for later phases and rollback, never to dereference. Members
/// documented as framework-thread run inside the workflow's
/// <see cref="OnFramework{T}"/> dispatch.
/// </summary>
internal interface ISceneRuntime
{
    /// <summary>The exact active GPose session identity; null outside one.</summary>
    SessionGeneration? ActiveSession { get; }

    Task<T> OnFramework<T>(Func<T> func);

    // ── files (any thread) ───────────────────────────────────────────────

    SceneReadOutcome ReadScene(string path);
    SceneWriteOutcome WriteScene(SceneFile scene, string path);

    /// <summary>
    /// Stamps every stated character-file reference with its package's content
    /// hash, in place, and answers the notes for the ones it could not read.
    /// Off the framework thread deliberately: hashing tens of megabytes is
    /// file work, and the capture that produced the references may not spend
    /// a frame on it.
    /// </summary>
    IReadOnlyList<string> StampMcdfHashes(SceneFile scene);

    /// <summary>
    /// Turns every actor's appearance into a PORTABLE payload, in place, and
    /// answers one note per actor it could not. Only a save that was asked for
    /// modded appearance runs it.
    ///
    /// <para>Two sources, in this order: the package Poser already owns for the
    /// actor (its bytes are read from the recorded path), and — when the actor
    /// wears no imported package — a package created NOW from the actor's live
    /// Glamourer, Penumbra and Customize+ state through the existing exporter.
    /// Either way the document ends up holding bytes. A temporary collection
    /// id, an actor address or a source path is not a portable save, so an
    /// actor whose payload cannot be produced or does not fit the cap keeps
    /// NOTHING and is named in a note.</para>
    ///
    /// <para>Runs from the workflow task, not a framework action: it marshals
    /// its own framework work, waits on the export transaction's own receipt,
    /// and does file work off the frame.</para>
    /// </summary>
    /// <summary>
    /// What the appearance payloads would add to a save RIGHT NOW, in bytes:
    /// the real size of every package the actors in the session are currently
    /// wearing. It is a sum of file lengths, not a guess — the container
    /// stores payloads raw, so what it measures is what the scene will cost.
    /// Zero when nobody is wearing one.
    /// </summary>
    long EstimateAppearanceBytes();

    /// <summary>Drops one temporary file the seal created, after the write has
    /// streamed it into the container. Never fails a save.</summary>
    void DeleteTemporary(string path);

    Task<SceneSealOutcome> SealAppearance(
        SceneFile scene,
        IReadOnlyDictionary<Guid, ActorId> identities,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    // ── capture (framework thread) ───────────────────────────────────────

    /// <summary>
    /// ARMS the whole-scene capture: the bone-transform caches are re-armed
    /// now, and the capture itself runs — and answers through
    /// <paramref name="onCaptured"/> — once the update pass that refreshes
    /// them has run, several ticks later. Returns the refusal detail, or null
    /// when armed. A capture that read the caches synchronously would write a
    /// never-posed actor's SKELETON-BUILD-TIME bones rather than the pose on
    /// screen, which is why this is armed rather than called.
    /// </summary>
    string? ArmSceneCapture(
        Guid sceneId, string? description, Action<SceneCaptureOutcome> onCaptured);

    // ── load-side materialization (framework thread) ─────────────────────

    /// <summary>
    /// Where the user is standing NOW — the anchor a relative load rebases a
    /// scene onto, read from the same local player the capture recorded its
    /// <see cref="SceneFile.Origin"/> from. Null outside a session that has
    /// one, which refuses a relative load rather than rebasing onto zero.
    /// Framework thread.
    /// </summary>
    System.Numerics.Vector3? CurrentOrigin();

    /// <summary>
    /// Which zone the session is in NOW. A borrowed map object means something
    /// only where it was taken, so this is what a load compares
    /// <see cref="SceneFile.TerritoryId"/> against before it tries to take one
    /// again. Zero when there is no territory to read, which refuses every
    /// borrowed entry rather than guessing. Framework thread.
    /// </summary>
    uint CurrentTerritoryId();

    /// <summary>
    /// Destroys everything the session is holding — spawned actors, props,
    /// overlay nodes, spawned lights and additional cameras — before a
    /// destroy-first load restores anything. Borrowed entities (a captured
    /// world light, the session's own default camera) are left alone: they were
    /// never this session's to destroy. Framework thread.
    ///
    /// <para>A borrowed MAP object is neither destroyed nor left alone: it is
    /// RELEASED, which writes back the placement and flags it was claimed with.
    /// Clearing a scene is one of the four ways a claim ends, and the count
    /// comes back so the outcome can say so in its own words.</para>
    /// </summary>
    SceneClearOutcome ClearScene();

    /// <summary>Spawns one actor and applies its model id. Null with a detail
    /// on failure.</summary>
    object? SpawnActor(SceneActor data, out string? detail);

    /// <summary>Whether the spawned actor has slot skeletons AND its exact
    /// current generation is published to the binding registry. Both are
    /// required before the pose-import admission can succeed.</summary>
    bool ActorReady(object actor);

    /// <summary>
    /// Re-imports the actor's saved character file through the EXISTING MCDF
    /// transaction — the same admission, phases, redraw barrier, rollback and
    /// ownership registration a hand-driven import runs, so its by-name
    /// unlock-and-restore teardown holds for a scene-restored actor exactly as
    /// it does for one the user imported. Returns null when the actor states
    /// no character file or the import succeeded, else the refusal detail.
    /// A file that has changed since the save is RESTORED with a detail.
    ///
    /// <para>Runs from the workflow task, not a framework action: it marshals
    /// its own framework work and waits on the transaction's own progress.
    /// </para>
    /// </summary>
    Task<SceneMcdfOutcome> ImportMcdf(
        string scenePath,
        object actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Attaches the saved companion; null on success.</summary>
    string? AttachCompanion(object actor, SceneActor data);

    /// <summary>Whether the attached companion's own skeleton exists yet — a
    /// companion body builds several frames after the attachment lands, and a
    /// companion pose cannot be imported before it does.</summary>
    bool CompanionReady(object actor);

    /// <summary>Arms the pose import for the actor's attached COMPANION,
    /// through the same single-flight engine an actor pose uses. Returns the
    /// refusal detail, or null when armed.</summary>
    string? ArmCompanionPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Arms the ONE atomic pose import for this actor. Returns the
    /// refusal detail, or null when armed — the terminal
    /// <see cref="OperationReceipt"/> arrives through the callback.</summary>
    string? ArmPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Places the actor at the scene's stated placement, falling back
    /// to the pose document's absolute model transform for files written
    /// before placements were stated. Null on success or when neither carries
    /// one; a placement that did not LAND is a named refusal, never a silent
    /// no-op.</summary>
    string? PlaceActor(object actor, SceneActor data);

    /// <summary>Stops the actor so its pose lands on a held frame. Scenes
    /// carry no animation — a timeline id means something different on every
    /// client — so a restored actor is always frozen and the picture is always
    /// the same one. Null on success, else the refusal detail.</summary>
    string? FreezeActor(object actor);

    /// <summary>Restores the actor's saved gaze. <paramref name="target"/> is
    /// the restored actor the saved Entity key resolved to, or null when the
    /// file names none. Null on success, else the refusal detail.</summary>
    string? ApplyActorGaze(object actor, SceneActor data, object? target);

    void SetActorVisibility(object actor, bool visible);

    /// <summary>Spawns one prop with its transform and visibility.</summary>
    object? SpawnProp(SceneProp data, out string? detail);

    /// <summary>Stages one overlay node from its saved document.</summary>
    object? SpawnOverlay(SceneOverlay data, out string? detail);

    /// <summary>
    /// Takes back one of the map's own objects that the scene had borrowed,
    /// matching it by the identity the file states (model path plus the point
    /// the map stands it at) and applying the placement the file recorded.
    ///
    /// <para>Nothing is created: the object was already there. Null with a
    /// stated detail when this map has no such object standing where the file
    /// says — a borrowed entry is refused BY NAME rather than applied to
    /// whatever else happens to share its path.</para>
    /// </summary>
    object? AdoptWorldObject(SceneWorldObject data, out string? detail);

    /// <summary>Gives one borrowed map object back — the rollback verb, and the
    /// exact inverse of <see cref="AdoptWorldObject"/>. It RESTORES rather than
    /// destroys, which is why it is not named with the others.</summary>
    void ReleaseWorldObject(object token);

    /// <summary>Spawns one light with its complete document, gobo, and — when
    /// an attachment is stated — the exact resolved bone on the restored
    /// owner. An unresolvable attachment returns null with a detail and
    /// spawns NOTHING: a light is never silently detached into world space.
    /// </summary>
    object? SpawnLight(SceneLight data, object? attachmentOwner, out string? detail);

    /// <summary>Snapshot of the session default camera for rollback.</summary>
    CameraFile CaptureDefaultCameraState();

    /// <summary>Applies a scene camera document onto the session default
    /// camera; null on success.</summary>
    string? ApplyDefaultCamera(SceneCamera data);

    /// <summary>The session default camera as a structure token, so a
    /// saved group that held the Main Camera re-seats it on load.</summary>
    object? DefaultCameraToken();

    /// <summary>Creates one additional camera from its document.</summary>
    object? CreateCamera(SceneCamera data, out string? detail);

    /// <summary>Sets a camera's followed actor and saved identity-lock state
    /// (null camera = the default camera); null on success.</summary>
    string? SetCameraTarget(
        object? camera, object targetActor, string displayName,
        bool targetLocked);

    /// <summary>Makes a camera live (null = the default camera).</summary>
    string? SetLiveCamera(object? camera);

    /// <summary>Snapshot of the current environment for rollback.</summary>
    SceneEnvironment CaptureEnvironmentState();

    /// <summary>Snapshot of the session-wide render/simulation toggles for
    /// rollback.</summary>
    SceneWorld CaptureWorldState();

    /// <summary>Stamps the session-wide toggles: a scene that asks for neither
    /// RELEASES them. Null on success, else a named degradation.</summary>
    string? ApplyWorld(SceneWorld world);

    /// <summary>Stamps the complete environment: time, freeze, weather and
    /// all eight sections (held sections take their values, unheld sections
    /// release to the game).</summary>
    void ApplyEnvironment(SceneEnvironment target);

    // ── rollback (framework thread) ──────────────────────────────────────

    void DestroyActor(object actor);
    void DestroyProp(object prop);
    void DestroyOverlay(object overlay);
    void DestroyLight(object light);
    void DestroyCamera(object camera);
    void RestoreDefaultCamera(CameraFile baseline);
}
