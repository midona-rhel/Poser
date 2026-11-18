using Poser.Application.Scene;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Documents.Files;
using Poser.Domain;

namespace Poser.Application.Scene;

/// <summary>
/// Everything a scene load puts back onto an actor it spawned: appearance,
/// companion, pose, placement, name, freeze, gaze and visibility. Members run on
/// the game's main thread unless they say otherwise.
/// </summary>
public interface IActorRestorePort
{
    /// <summary>Whether the spawned actor's skeletons exist and Poser's list of
    /// actors already points at the actor as it is now. A pose can only be
    /// imported once both are true.</summary>
    bool ActorReady(SceneEntityHandle actor);

    /// <summary>Whether the companion's own skeleton exists yet. A companion's
    /// body is built several frames after it is attached, and its pose cannot be
    /// imported before then.</summary>
    bool CompanionReady(SceneEntityHandle actor);

    Task<string?> RestoreClothingOnly(SceneEntityHandle actor, System.Threading.CancellationToken cancellation);

    /// <summary>Assigns the saved Penumbra collection when the actor has one and
    /// no MCDF. If that collection does not exist on this computer, the
    /// step still succeeds with a note, and the actor keeps the collection it
    /// spawned with. A failure in Penumbra or in the redraw is still an error.
    /// Runs on the load's background task and moves its own game work to the
    /// main thread.</summary>
    Task<Outcome> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>
    /// Applies the actor's saved MCDF again, in exactly the way
    /// a person's own MCDF import does: the same checks, the same wait for the
    /// redraw, the same undo on failure. So when the actor is removed, its
    /// appearance is unlocked and put back just as for an MCDF imported by hand.
    /// The result says whether the appearance went on, with a detail when there
    /// is something to report. A file that has changed since the scene was saved
    /// is still applied, with a note.
    ///
    /// <para>Runs on the load's background task, not on the game's main thread:
    /// it moves its own game work to the main thread and waits for the import to
    /// finish.
    /// </para>
    /// </summary>
    Task<SceneMcdfOutcome> ImportMcdf(
        string scenePath,
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Puts the actor's scene appearance back and leaves it editable. See
    /// <see cref="SceneAppearanceRule"/>.</summary>
    Task<SceneMcdfOutcome> ApplySceneAppearance(
        string scenePath,
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation);

    /// <summary>Attaches the saved companion; null on success.</summary>
    string? AttachCompanion(SceneEntityHandle actor, SceneActor data);

    /// <summary>Whether a pose import would be turned down or replaced right now.
    /// Only one pose import runs at a time, and one is already waiting or being
    /// applied, or an IK bake is being applied. A load waits for this to clear
    /// instead of using up an actor's one try.</summary>
    bool PoseImportBusy { get; }

    /// <summary>Keeps pose imports for the running load. Until released, any
    /// other pose import is turned down instead of replacing the load's.</summary>
    void HoldPoseImports(bool held);

    /// <summary>Cancels the load's own pose import, found by its operation id,
    /// if it is still waiting to apply. A load that timed out or was cancelled
    /// never leaves its pose import waiting.</summary>
    void CancelPoseImport(Guid operationId);

    /// <summary>Queues the pose import for this actor; the whole pose is applied
    /// in one go. Returns why it was turned down, or null when queued. The final
    /// <see cref="OperationReceipt"/> arrives through the callback.</summary>
    string? ArmPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Queues the pose import for the actor's companion, in the same
    /// one-at-a-time queue an actor's pose uses. Returns why it was turned down,
    /// or null when queued.</summary>
    string? ArmCompanionPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt);

    /// <summary>Places the actor where the scene saved it. Older files that did
    /// not save a placement use the actor position stored in the pose instead.
    /// Returns null on success or when neither has one. A placement that did not
    /// take effect returns why, and is never skipped silently.</summary>
    string? PlaceActor(SceneEntityHandle actor, SceneActor data);

    /// <summary>Puts the companion back where it was saved, after its pose is applied.</summary>
    string? PlaceCompanion(SceneEntityHandle actor, SceneActor data);

    /// <summary>Restores the actor's name once the actor is found, whether or not its pose and placement worked.</summary>
    string? RestoreActorName(SceneEntityHandle actor, SceneActor data);

    /// <summary>Stops the actor so its pose is applied on a still frame. A scene
    /// saved without its animation has none to play, because an animation id can
    /// mean something different on every computer. So the actor is frozen, and the
    /// picture is the same every time. Returns null on success, otherwise why it
    /// failed.</summary>
    string? FreezeActor(SceneEntityHandle actor);

    /// <summary>For a scene saved with its animation: the actor, or its
    /// <paramref name="companion"/>, plays the saved animation again instead of
    /// being frozen. Paused animations go to their saved frame; playing ones start
    /// from the beginning. Returns null on success, otherwise why it failed.</summary>
    string? RestoreAnimation(SceneEntityHandle actor, SceneActor data, bool companion);

    /// <summary>For a scene saved with its animation: the bone edits made in Poser
    /// to the actor, or to its <paramref name="companion"/>, go back on top of the
    /// animation, instead of importing the whole saved pose. Returns null on
    /// success, otherwise why it failed.</summary>
    string? RestorePoseEdits(SceneEntityHandle actor, SceneActor data, bool companion);

    /// <summary>Restores the actor's saved gaze. <paramref name="target"/> is
    /// the loaded actor the gaze was saved looking at, or null when it looked at
    /// no actor. Returns null on success, otherwise why it failed.</summary>
    string? ApplyActorGaze(SceneEntityHandle actor, SceneActor data, SceneEntityHandle? target);

    void SetActorVisibility(SceneEntityHandle actor, bool visible);
}
