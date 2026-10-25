using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>
/// One undoable action. Transform entries restore captured state; lifecycle
/// entries run the inverse action through the service that owns the entity.
/// </summary>
public abstract record HistoryEntry(string Description)
{
    // A redraw can replace the saved bone IDs while an undo is running.
    // Keep the same step ID so undo can commit the updated entry.
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>External file required to repeat the operation, when any.</summary>
    public string? RequiredAsset { get; init; }
    /// <summary>Entities changed by this step, used to find edits for selected-entity undo.</summary>
    public IReadOnlyList<SelectionId>? AffectedEntities { get; init; }
    /// <summary>Finds the current entities after undo or redo recreates them.</summary>
    public Func<IReadOnlyList<SelectionId>?>? ResolveAffectedEntities { get; init; }
}

public sealed record TransformPatch(
    string Description,
    IReadOnlyList<TransformTargetState> Before,
    IReadOnlyList<TransformTargetState> After) : HistoryEntry(Description)
{
    /// <summary>Waits for replacement transform targets to load before this step can be replayed.</summary>
    public bool AwaitingTargets { get; init; }

    /// <summary>Group state changed by the gesture, restored together with its transforms.</summary>
    public GroupTransformHistoryChange? GroupState { get; init; }
}

/// <summary>What happens to a step after undo or redo fails.</summary>
public enum RefusalAction
{
    /// <summary>Keep the step so the user can try undo or redo again.</summary>
    Keep,
    /// <summary>Keep the step after its first failure; remove it if the next attempt fails.</summary>
    DropOnRepeat,
    /// <summary>Report the failure and remove this step so earlier edits can be undone.</summary>
    DropNow,
}

/// <summary>
/// Runs undo and redo actions that report whether they succeeded.
/// RefusalPolicy decides whether a failed step stays available for another attempt.
/// </summary>
public abstract record InverseEntry(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : HistoryEntry(Description)
{
    /// <summary>Explains why undo or redo failed.</summary>
    public Func<string?>? FailureDetail { get; init; }

    /// <summary>Decides whether to keep a failed step. Called after the failure;
    /// leaving it unset uses the entry's default policy.</summary>
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
/// Undoes or repeats an entity change, such as creating or deleting an actor.
/// Failed attempts stay available by default so loading can finish before retrying.
/// </summary>
public sealed record SceneLifecyclePatch(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : InverseEntry(Description, Undo, Redo)
{
    protected internal override RefusalAction DefaultRefusal => RefusalAction.Keep;
}
