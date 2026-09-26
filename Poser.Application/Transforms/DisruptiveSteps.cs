using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Transforms;

/// <summary>Appearance-changing commands share the complete non-animation actor inverse.</summary>
public sealed class DisruptiveSteps(
    TransformHistory history, IActorStateSnapshots snapshots,
    ValueJournal values)
{
    public IntegrationResult Run(ActorId actor, string description, Func<IntegrationResult> verb)
    {
        values.Seal();
        var captured = snapshots.Capture(actor);
        if (!captured.Success || captured.Value is not { } before)
            return IntegrationResult.Fail(captured.Detail ?? "The actor's state could not be captured.");
        var result = verb();
        if (!result.Success) return result;

        // A redraw/import may still be pending. Capture the redo state on the
        // first undo, after later entries have been undone, not from the old body
        // immediately after requesting a redraw. A failed capture does not mutate.
        ActorStateSnapshot? after = null;
        string? failure = null;
        bool PrepareUndo()
        {
            if (after != null) return true;
            var current = snapshots.Capture(actor);
            if (!current.Success || current.Value is not { } state)
            {
                failure = current.Detail ?? "The actor is not ready to capture its current state.";
                return false;
            }
            if (state.Actor != before.Actor || state.Session != before.Session)
            {
                failure = "The actor or GPose session changed.";
                return false;
            }
            after = state;
            failure = null;
            return true;
        }
        history.Append(new JournalStep(description, PrepareUndo, () => after != null)
        {
            RetainOnFailure = true,
            FailureDetail = () => failure,
            CompleteReplay = (undo, current, cancellation, completed) =>
                snapshots.Restore(undo ? before : after!, current, cancellation, completed),
        });
        return result;
    }
}
