using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public sealed partial class UndoJournal
{
    private Guid? _deferredToken;

    private GestureResult RunDeferred(JournalStep step, DeferredJournalWrite write, bool undo)
    {
        var token = Guid.NewGuid();
        var revision = step.Revision;
        var historyRevision = _history.MutationRevision;
        bool terminal = false;
        bool committed = false;
        ValueWriteResult? outcome = null;
        bool Current() => _deferredToken == token && ReferenceEquals(_restoring, step)
            && step.Revision == revision
            && _history.MutationRevision == historyRevision
            && ReferenceEquals(undo ? _history.PeekUndo() : _history.PeekRedo(), step);
        var started = _runner.RunDeferredTransition(() =>
        {
            _deferredToken = token;
            _restoring = step;
        });
        if (!started.Success) return started;

        ValueWriteResult Commit(Action mutation)
        {
            if (terminal || !Current()) return new(false, "History changed while the colour reset was pending.");
            var result = _runner.RunDeferredTransition(() =>
            {
                // Check again inside the transition, immediately before ownership
                // changes. A folded value retains entry identity but changes revision.
                if (!Current()) throw new InvalidOperationException("History changed before commit.");
                mutation();
                if (undo) _history.CommitUndo(step); else _history.CommitRedo(step);
                committed = true;
                terminal = true;
            });
            return new(result.Success, result.Detail);
        }

        void Complete(ValueWriteResult result)
        {
            if (outcome.HasValue) return;
            terminal = true;
            outcome = committed ? ValueWriteResult.Ok()
                : new(false, result.Detail ?? "The deferred history change did not commit.");
            // An old callback must never clear a newer operation after Clear().
            if (_deferredToken != token) return;
            _deferredToken = null;
            _restoring = null;
            if (!outcome.Value.Success) _notice(outcome.Value.Detail!);
        }

        // Start and commit use the same runner guard, but are separate transitions:
        // a provider may complete inline without reentering an active transition.
        try { write(Commit, Complete); }
        catch (Exception ex) { Complete(new(false, ex.Message)); }
        return outcome is { } finished
            ? finished.Success ? GestureResult.Ok() : GestureResult.Fail(finished.Detail!)
            : GestureResult.Pending("The colour reset is waiting for the redrawn actor.");
    }
}
