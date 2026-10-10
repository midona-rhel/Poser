using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>
/// Everything a load restores onto an actor it has spawned: appearance,
/// companion, the one atomic pose import, placement, name, freeze, gaze and
/// visibility. Framework thread unless a member says otherwise.
/// </summary>
public interface IActorRestorePort
{
    /// <summary>Whether the spawned actor has slot skeletons AND its exact
    /// current generation is published to the binding registry. Both are
    /// required before the pose-import admission can succeed.</summary>
    bool ActorReady(SceneEntityHandle actor);

    /// <summary>Whether the attached companion's own skeleton exists yet — a
    /// companion body builds several frames after the attachment lands, and a
    /// companion pose cannot be imported before it does.</summary>
    bool CompanionReady(SceneEntityHandle actor);

    /// <summary>Assigns the saved Penumbra collection when the actor states
    /// one and no character file. Null on success or when nothing is stated.
    /// Runs from the workflow task and marshals its own framework work.</summary>
    Task<string?> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation);

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
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Attaches the saved companion; null on success.</summary>
    string? AttachCompanion(SceneEntityHandle actor, SceneActor data);

    /// <summary>Whether the single-flight pose slot would refuse or supersede
    /// an import right now: one is armed or applying, an IK bake is applying,
    /// or a transform gesture is open. A load waits this out instead of
    /// spending an actor's one attempt on it.</summary>
    bool PoseImportBusy { get; }

    /// <summary>Holds the pose slot for the running load: until released, an
    /// import that is not the load's own refuses instead of superseding the
    /// load's.</summary>
    void HoldPoseImports(bool held);

    /// <summary>Cancels the load's own pose import by the operation id its
    /// Pending receipt carried, when it is still the one armed — a wait that
    /// timed out or was cancelled never leaves its child armed.</summary>
    void CancelPoseImport(Guid operationId);

    /// <summary>Arms the ONE atomic pose import for this actor. Returns the
    /// refusal detail, or null when armed — the terminal
    /// <see cref="OperationReceipt"/> arrives through the callback.</summary>
    string? ArmPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Arms the pose import for the actor's attached COMPANION,
    /// through the same single-flight engine an actor pose uses. Returns the
    /// refusal detail, or null when armed.</summary>
    string? ArmCompanionPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Places the actor at the scene's stated placement, falling back
    /// to the pose document's absolute model transform for files written
    /// before placements were stated. Null on success or when neither carries
    /// one; a placement that did not LAND is a named refusal, never a silent
    /// no-op.</summary>
    string? PlaceActor(SceneEntityHandle actor, SceneActor data);

    /// <summary>Restores the attached body's own model placement after its pose.</summary>
    string? PlaceCompanion(SceneEntityHandle actor, SceneActor data);

    /// <summary>Restores the display name after binding, independently of pose/placement success.</summary>
    string? RestoreActorName(SceneEntityHandle actor, SceneActor data);

    /// <summary>Stops the actor so its pose lands on a held frame. Scenes
    /// carry no animation — a timeline id means something different on every
    /// client — so a restored actor is always frozen and the picture is always
    /// the same one. Null on success, else the refusal detail.</summary>
    string? FreezeActor(SceneEntityHandle actor);

    /// <summary>Restores the actor's saved gaze. <paramref name="target"/> is
    /// the restored actor the saved Entity key resolved to, or null when the
    /// file names none. Null on success, else the refusal detail.</summary>
    string? ApplyActorGaze(SceneEntityHandle actor, SceneActor data, SceneEntityHandle? target);

    void SetActorVisibility(SceneEntityHandle actor, bool visible);
}
