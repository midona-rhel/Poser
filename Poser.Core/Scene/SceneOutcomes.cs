using System;
using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Files;

namespace Poser.Scene;

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
