using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>One IK chain that was turned on, as it was when the snapshot was
/// taken.</summary>
public readonly record struct IkChainSnapshot(BoneId Endpoint, IkChainConfig Config);

/// <summary>
/// An actor's whole pose at one moment: the pose, which only the code in
/// Poser.Game that took it can read, and the IK chains that were turned on.
/// </summary>
public sealed record ActorSnapshot(
    Guid Lineage,
    object Pose,
    IReadOnlyList<IkChainSnapshot> IkChains)
{
    /// <summary>The actor the snapshot was taken from, when it is put back on
    /// another actor. Saved IK targets on the first actor's own bones are
    /// matched by this.</summary>
    public Guid? TakenFrom { get; init; }

    /// <summary>The same snapshot, put back on the actor with this lasting id.
    /// Undo uses this when it brought the actor back as a new game object.</summary>
    public ActorSnapshot For(Guid lineage) =>
        lineage == Lineage ? this : this with { Lineage = lineage, TakenFrom = TakenFrom ?? Lineage };
}

/// <summary>
/// An undo step that carries its own undo and redo code, for changes that are
/// not a plain move and not adding or removing a thing. When it fails twice in
/// a row it is dropped, unless <see cref="InverseEntry.OnRefusal"/> keeps it or
/// drops it at the first failure.
/// </summary>
public sealed record JournalStep(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : InverseEntry(Description, Undo, Redo)
{
    /// <summary>For a step that takes several frames to undo or redo, such as
    /// putting back a redrawn actor: runs after <c>Undo</c> or <c>Redo</c> and
    /// calls back on the game's main thread when it is done. The step stays in
    /// its list until then.</summary>
    public Action<bool, Func<bool>, CancellationToken, Action<GestureResult>>? CompleteReplay { get; init; }

    /// <summary>The value before and after, when the step is a value
    /// change — read by the action recorder, never by undo.</summary>
    public object? BeforeValue { get; init; }
    public object? AfterValue { get; init; }
}

/// <summary>Captures and restores an actor's whole pose. A restore is an
/// import and completes later; the callback says whether it landed.</summary>
public interface IPoseSnapshotPort
{
    ActorSnapshot? Capture(Guid lineage);

    /// <summary>Captures a posed actor before changing its appearance; an unposed actor needs no restore.</summary>
    ActorSnapshot? CaptureForAppearanceChange(Guid lineage) => Capture(lineage);

    /// <summary>Owned pose layers only, excluding evaluated animation and expression layers.</summary>
    ActorSnapshot? CaptureAuthored(Guid lineage) => Capture(lineage);

    /// <summary>Starts the restore. False when it could not start; the
    /// callback then never fires.</summary>
    bool Restore(ActorSnapshot snapshot, Action<bool> finished);

    /// <summary>Starts the restore only if the undo or redo that asked for it
    /// is still the one running, since a restore can start frames later.</summary>
    bool Restore(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<bool> finished) =>
        stillCurrent() && Restore(snapshot, finished);
}
