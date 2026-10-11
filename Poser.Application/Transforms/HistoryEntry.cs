using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>
/// One undoable action. Transform entries restore captured state; lifecycle
/// entries run the inverse action through the service that owns the entity.
/// </summary>
public abstract record HistoryEntry(string Description)
{
    // Re-keying replaces the immutable patch while an async restore may
    // still hold the old object. The operation identity survives that copy.
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>External file required to repeat the operation, when any.</summary>
    public string? RequiredAsset { get; init; }
    /// <summary>Complete entity footprint for scoped replay. Null is a scene-wide
    /// ordering barrier, not permission to skip an unknown operation.</summary>
    public IReadOnlyList<SelectionId>? AffectedEntities { get; init; }
    /// <summary>Lifecycle entries may bind their new identity on the next scene publication.</summary>
    public Func<IReadOnlyList<SelectionId>?>? ResolveAffectedEntities { get; init; }
}

public sealed record TransformPatch(
    string Description,
    IReadOnlyList<TransformTargetState> Before,
    IReadOnlyList<TransformTargetState> After) : HistoryEntry(Description)
{
    /// <summary>Optional named/anonymous group presentation state changed by
    /// the same gesture. It is restored with the target snapshots, never by
    /// a post-commit observer.</summary>
    public GroupTransformHistoryChange? GroupState { get; init; }
}

/// <summary>What history does with an entry whose inverse was refused.</summary>
public enum RefusalAction
{
    /// <summary>Retain the entry for retry; the cursor does not move.</summary>
    Keep,
    /// <summary>Retain it once; the same entry refused again is discarded.</summary>
    DropOnRepeat,
    /// <summary>The refusal is permanent: report it and discard the entry so
    /// unrelated earlier history can proceed.</summary>
    DropNow,
}

/// <summary>
/// An entry that runs its own undo and redo delegates, each reporting
/// whether the direction landed. Refusal handling is one value per entry,
/// read through <see cref="RefusalPolicy"/>.
/// </summary>
public abstract record InverseEntry(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : HistoryEntry(Description)
{
    /// <summary>Optional reason for a refused direction.</summary>
    public Func<string?>? FailureDetail { get; init; }

    /// <summary>Decided after a refusal, when it is known; null uses the
    /// entry kind's default.</summary>
    public Func<RefusalAction>? OnRefusal { get; init; }

    protected internal virtual RefusalAction DefaultRefusal => RefusalAction.DropOnRepeat;
}

/// <summary>The single refusal decision read by the undo journal and by
/// lifecycle batches.</summary>
public static class RefusalPolicy
{
    public static RefusalAction Decide(HistoryEntry entry) =>
        entry is InverseEntry inverse
            ? inverse.OnRefusal?.Invoke() ?? inverse.DefaultRefusal
            : RefusalAction.Keep;
}

/// <summary>
/// A scene-lifecycle action. Its undo and redo delegates report whether the
/// action landed and resolve the entity again when it is recreated. A
/// refusal is retained unless the entry says it is permanent.
/// </summary>
public sealed record SceneLifecyclePatch(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : InverseEntry(Description, Undo, Redo)
{
    protected internal override RefusalAction DefaultRefusal => RefusalAction.Keep;
}
