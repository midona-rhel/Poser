using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Application.Operations;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>Which whole-shot operation a progress/receipt pair describes.</summary>
public enum SceneOperationKind
{
    Save,
    Load,
}

/// <summary>Phases of the single scene transaction, in execution order.</summary>
public enum ScenePhase
{
    Capturing,
    Writing,
    Reading,
    SpawningEntities,
    AwaitingActors,
    ApplyingRelationships,
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

/// <summary>One entity's typed restore outcome. A missing parent or
/// resource is a named, explained refusal here — never a silent detach or a
/// silently skipped row.</summary>
public sealed record SceneEntityOutcome(
    string Kind,
    string Name,
    bool Restored,
    string? Detail = null);

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

/// <summary>Typed admission result for starting a scene operation.</summary>
public readonly record struct SceneActionResult(bool Success, string? Detail = null)
{
    public static SceneActionResult Ok() => new(true);
    public static SceneActionResult Fail(string detail) => new(false, detail);
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

    // ── capture (framework thread) ───────────────────────────────────────

    SceneCaptureOutcome CaptureScene(Guid sceneId, string? description);

    // ── load-side materialization (framework thread) ─────────────────────

    /// <summary>Spawns one actor and applies its model id. Null with a detail
    /// on failure.</summary>
    object? SpawnActor(SceneActor data, out string? detail);

    /// <summary>Whether the spawned actor's slot skeletons exist yet.</summary>
    bool ActorReady(object actor);

    /// <summary>Attaches the saved companion; null on success.</summary>
    string? AttachCompanion(object actor, SceneActor data);

    /// <summary>Arms the ONE atomic pose import for this actor. Returns the
    /// refusal detail, or null when armed — the terminal
    /// <see cref="OperationReceipt"/> arrives through the callback.</summary>
    string? ArmPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Places the actor at the pose document's absolute model
    /// transform; null on success or when the document carries none.</summary>
    string? PlaceActor(object actor, SceneActor data);

    void SetActorVisibility(object actor, bool visible);

    /// <summary>Spawns one prop with its transform and visibility.</summary>
    object? SpawnProp(SceneProp data, out string? detail);

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

    /// <summary>Creates one additional camera from its document.</summary>
    object? CreateCamera(SceneCamera data, out string? detail);

    /// <summary>Sets a camera's followed actor (null camera = the default
    /// camera); null on success.</summary>
    string? SetCameraTarget(object? camera, object targetActor, string displayName);

    /// <summary>Makes a camera live (null = the default camera).</summary>
    string? SetLiveCamera(object? camera);

    /// <summary>Snapshot of the current environment for rollback.</summary>
    SceneEnvironment CaptureEnvironmentState();

    /// <summary>Stamps the complete environment: time, freeze, weather and
    /// all eight sections (held sections take their values, unheld sections
    /// release to the game).</summary>
    void ApplyEnvironment(SceneEnvironment target);

    // ── rollback (framework thread) ──────────────────────────────────────

    void DestroyActor(object actor);
    void DestroyProp(object prop);
    void DestroyLight(object light);
    void DestroyCamera(object camera);
    void RestoreDefaultCamera(CameraFile baseline);
}
