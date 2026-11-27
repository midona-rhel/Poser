using System;
using System.Collections.Generic;
using Poser.Domain;
using Poser.Domain.Operations;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>Whether a progress report and result belong to a save or a load.</summary>
public enum SceneOperationKind
{
    Save,
    Load,
}

/// <summary>The steps of a scene save or load, in the order they run.</summary>
public enum ScenePhase
{
    /// <summary>The save's first step: every skeleton gets one update so the
    /// bone positions the save reads are from this frame. Without it, only
    /// skeletons the game updated on its own this frame would be current.</summary>
    RefreshingPoses,
    Capturing,
    Writing,
    Reading,
    SpawningEntities,
    AwaitingActors,
    /// <summary>Applying saved appearances. It runs before anything that depends
    /// on an actor's body, because applying an MCDF redraws the actor and
    /// replaces its body and skeletons.
    /// </summary>
    ApplyingAppearance,
    ApplyingRelationships,
    /// <summary>Restoring saved animation, or freezing actors before applying baked poses.</summary>
    FreezingActors,
    ApplyingPose,
    ApplyingPresentation,
    ApplyingCameras,
    ApplyingLights,
    ApplyingEnvironment,
    Committing,
    RollingBack,
    /// <summary>Cancel was pressed. The operation waits, for a limited time,
    /// for the MCDF imports or exports it started to stop before it reports
    /// Cancelled.</summary>
    Cancelling,
    Completed,
    RolledBack,
    Failed,
    Cancelled,
}

/// <summary>One row of Last result: whether one entity came back. Something
/// that did not come back, such as a missing parent or file, gets its own row
/// with the reason; it is never left out without a word.
///
/// <para><see cref="Detail"/> says what happened and <see cref="Remedy"/> what
/// to do about it. A row that did not come back always has both: the workflow
/// fills the remedy in from <see cref="SceneOutcomeKinds.Remedy"/> when it
/// publishes the result, so Last result and the log say the same.</para>
///
/// <para>A row that came back with a detail came back with a caveat, such as a
/// gobo the game no longer has, a changed MCDF or a model still loading. It is
/// shown and logged like a failure's reason, not hidden among the
/// successes.</para></summary>
public sealed record SceneEntityOutcome(
    SceneOutcomeKind Kind,
    string Name,
    bool Restored,
    string? Detail = null,
    string? Remedy = null)
{
    public bool Degraded => Restored && Detail != null;
}

/// <summary>
/// The final result of one scene save or load. Its state is the same
/// <see cref="OperationReceiptState"/> the operation's
/// <see cref="OperationReceipt"/> has. For a save: <c>Applied</c> when the file
/// was written, <c>Cancelled</c> when Cancel was pressed or GPose ended, and
/// <c>Failed</c> otherwise. For a load: <c>Applied</c> when everything came
/// back, <c>RolledBack</c> when the load stopped and took back what it had
/// done, <c>Cancelled</c> as for a save, and <c>Failed</c> when some parts did
/// not come back but the rest was kept, or when taking a load back left
/// something behind.
/// </summary>
public sealed record SceneOutcome(
    OperationReceiptState State,
    string Detail,
    IReadOnlyList<SceneEntityOutcome> Entities,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> RecoveryEvidencePaths)
{
    public bool Success => State == OperationReceiptState.Applied;

    /// <summary>Whether the final state may have left created entities behind.
    /// This checks the outcome state, not the live entity count.</summary>
    public bool LeftEntitiesBehind => State is not (
        OperationReceiptState.RolledBack or OperationReceiptState.Cancelled);

    /// <summary>The load cleared the session before restoring. A rollback
    /// then leaves nothing of the load behind, but the session it cleared is
    /// gone too — "nothing was left behind" would read as "nothing changed".
    /// </summary>
    public bool SessionCleared { get; init; }
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
    /// wrapper or the GPose primary — and it is never silent:
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
        // "was" for one removed entity, "were" for more.
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
