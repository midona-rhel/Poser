using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>
/// Undo steps for the appearance commands that can redraw an actor: Penumbra
/// collection, Glamourer design, Customize+ body profile and Redraw. Undo puts
/// back the actor's whole state except animation, also after the actor was
/// removed and brought back by undo.
/// </summary>
public sealed class DisruptiveSteps(
    EditHistory history, IActorStateSnapshots snapshots,
    ValueJournal values, IActorReplacements replacements)
{
    public IntegrationResult Run(ActorId actor, string description, Func<IntegrationResult> verb)
    {
        values.Seal();
        var captured = snapshots.Capture(actor);
        if (!captured.Success || captured.Value is not { } before)
            return IntegrationResult.Fail(captured.Detail ?? "The actor's state could not be captured.");
        var now = replacements.Track(actor);
        var result = verb();
        if (!result.Success) return result;

        // A redraw/import may still be pending. Capture the redo state on the
        // first undo, after later entries have been undone, not from the old body
        // immediately after requesting a redraw. A failed capture does not mutate.
        ActorStateSnapshot? after = null;
        string? failure = null;
        bool PrepareUndo()
        {
            if (now() is not { } live)
            {
                failure = "The actor is no longer available.";
                return false;
            }
            if (after != null) return true;
            var current = snapshots.Capture(live);
            if (!current.Success || current.Value is not { } state)
            {
                failure = current.Detail ?? "The actor is not ready to capture its current state.";
                return false;
            }
            if (state.Session != before.Session)
            {
                failure = "The GPose session changed.";
                return false;
            }
            after = state;
            failure = null;
            return true;
        }
        history.Append(new JournalStep(description, PrepareUndo, () => after != null)
        {
            AffectedEntities = new[] { SelectionId.ForActor(actor) },
            OnRefusal = () => RefusalAction.Keep,
            FailureDetail = () => failure,
            CompleteReplay = (undo, current, cancellation, completed) =>
            {
                if (now() is not { } live)
                {
                    completed(GestureResult.Fail("The actor is no longer available."));
                    return;
                }
                snapshots.Restore((undo ? before : after!).For(live), current, cancellation, completed);
            },
        });
        return result;
    }
}
