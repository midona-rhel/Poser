using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>One armed IK chain as it was when the snapshot was taken.</summary>
public readonly record struct IkChainSnapshot(BoneId Endpoint, IkChainConfig Config);

/// <summary>
/// An actor's whole pose at one moment: the pose file (opaque here; the
/// runtime port that captured it reads it back) and the armed IK chains.
/// </summary>
public sealed record ActorSnapshot(
    Guid Lineage,
    object Pose,
    IReadOnlyList<IkChainSnapshot> IkChains);

/// <summary>
/// The third entry shape beside the transform patch and the lifecycle
/// patch: a step with its own inverse. It runs the way a lifecycle patch
/// does, with explicit completion when restoration spans frames.
/// </summary>
public sealed record JournalStep(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : HistoryEntry(Description)
{
    /// <summary>A group membership restore can wait for readable member poses;
    /// repeated temporary refusal must not discard its preserved authored state.</summary>
    public Func<bool>? HasDeferredGroupCapture { get; init; }

    /// <summary>Result-aware value edits retain refused inverses for retry.</summary>
    public bool RetainOnFailure { get; init; }
    public Func<string?>? FailureDetail { get; init; }

    /// <summary>Completion of a multi-frame replay, after its synchronous verb.
    /// Invokes the callback on the application thread; history stays put until then.</summary>
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

    /// <summary>Owned pose layers only, excluding evaluated animation and expression layers.</summary>
    ActorSnapshot? CaptureAuthored(Guid lineage) => Capture(lineage);

    /// <summary>Starts the restore. False when it could not start; the
    /// callback then never fires.</summary>
    bool Restore(ActorSnapshot snapshot, Action<bool> finished);

    /// <summary>A deferred restore must still belong to its initiating
    /// history operation before beginning native work.</summary>
    bool Restore(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<bool> finished) =>
        stillCurrent() && Restore(snapshot, finished);
}
