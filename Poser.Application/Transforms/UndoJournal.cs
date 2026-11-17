using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>The result of running one undo or redo step. Attempted is false when
/// the step never ran, for example because a drag is still in progress; such a
/// refusal does not count toward discarding the step.</summary>
public readonly record struct HistoryRunResult(GestureResult Result, bool Attempted);

/// <summary>Runs undo and redo steps for the journal. This is the transform
/// gesture service, so a step never runs in the middle of a drag.</summary>
public interface IUndoRunner
{
    /// <summary>Retries bones that an earlier cancelled drag or undo could not
    /// put back (usually because the actor was being redrawn). Returns null when
    /// nothing was waiting; otherwise that retry is this press's whole result.</summary>
    GestureResult? RecoverPending();

    /// <summary>Runs a step that finishes at once, and moves it to the other
    /// list (only within <paramref name="entity"/>'s steps when given) if it worked.</summary>
    HistoryRunResult Run(HistoryEntry entry, bool undo, SelectionId? entity);

    /// <summary>Starts a step that takes several frames, such as restoring an
    /// actor after a redraw. The journal moves it once it has finished.</summary>
    HistoryRunResult Replay(JournalStep step, bool before, SelectionId? entity);
}

/// <summary>
/// Undo and redo. Each press picks the newest step (or the newest step for the
/// selected entity), asks its owner to put it back, and decides what happens to
/// the step when the owner refuses. Runs on the game's main thread.
/// </summary>
public sealed class UndoJournal
{
    public const string AssetGone = "Cannot redo: the file is no longer there.";
    public const string RestoreFailed = "The pose could not be restored.";
    public const string Dropped = "The step was dropped: the history changed while restoring.";
    public const string TargetsPending = "Waiting for the actor's skeleton to become available.";

    private readonly EditHistory _history;
    private readonly IUndoRunner _runner;
    private readonly Func<string, bool> _assetExists;
    private readonly Presentation.IUserNotices _notices;
    private HistoryEntry? _restoring;
    private CancellationTokenSource? _replayCancellation;
    private ulong _historyRevision;

    public UndoJournal(
        EditHistory history,
        IUndoRunner runner,
        Func<string, bool> assetExists,
        Presentation.IUserNotices notices)
    {
        _history = history;
        _runner = runner;
        _assetExists = assetExists;
        _notices = notices;
        _history.Appended += _ =>
        {
            _historyRevision++;
            _replayCancellation?.Cancel();
        };
        _history.Cleared += () =>
        {
            _historyRevision++;
            _restoring = null;
            var cancellation = _replayCancellation;
            _replayCancellation = null;
            cancellation?.Cancel();
        };
    }

    /// <summary>True while an explicit restore is in flight; undo and redo
    /// wait for it.</summary>
    public bool IsRestoring => _restoring != null;

    public bool CanUndo => !IsRestoring && (_history.CanUndo || HasUnrecordedChange?.Invoke() == true);

    /// <summary>Answers whether there is a change that is not in the history yet,
    /// such as a camera move that has not come to rest. Undo is offered while
    /// there is one, even with an empty history.</summary>
    public Func<bool>? HasUnrecordedChange { get; set; }
    public bool CanRedo => !IsRestoring && _history.CanRedo;
    public string? UndoDescription => _history.UndoDescription;
    public string? RedoDescription => _history.RedoDescription;

    /// <summary>A global undo is about to pick its step. A change that is still
    /// settling, such as a camera move, is recorded now, so this undo takes it back.</summary>
    public event Action? Undoing;

    public GestureResult Undo() => UndoCore(null);
    public GestureResult Undo(SelectionId entity) => UndoCore(entity);

    private GestureResult UndoCore(SelectionId? entity)
    {
        if (_runner.RecoverPending() is { } recovered)
            return recovered;
        if (IsRestoring)
            return GestureResult.Fail("A restore is still applying.");
        if (entity is null) Undoing?.Invoke();
        var entry = _history.PeekUndo(entity);
        if (entry == null)
            return GestureResult.Fail(entity is null ? "Nothing to undo." : "No independent undo step for this entity. Creation, removal, shared or scene-wide steps require global undo.");
        if (entry is TransformPatch { AwaitingTargets: true })
            return GestureResult.Fail(TargetsPending);
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, true, entity);
        return GiveUpOnRepeat(entry, _runner.Run(entry, true, entity));
    }

    /// <summary>The last refused entry using DropOnRepeat. A second refusal
    /// of that same entry discards it; Keep entries remain retryable.</summary>
    private HistoryEntry? _refused;

    private GestureResult GiveUpOnRepeat(HistoryEntry entry, HistoryRunResult run)
    {
        var result = run.Result;
        if (!run.Attempted) return result;
        if (result.Success)
        {
            _refused = null;
            return result;
        }
        switch (RefusalPolicy.Decide(entry))
        {
            case RefusalAction.DropNow:
                // Only steps with their own undo and redo (never a bone or
                // object move) ask for this, and the runner has already put
                // the step's own reason in the result.
                _refused = null;
                _history.Drop(entry);
                _notices.Note(result.Detail!);
                return result;
            case RefusalAction.Keep:
                _refused = null;
                return result;
        }
        if (!ReferenceEquals(_refused, entry))
        {
            _refused = entry;
            return result;
        }
        _refused = null;
        _history.Drop(entry);
        _notices.Note($"{entry.Description} could not be undone twice and was discarded.");
        return result;
    }

    public GestureResult Redo() => RedoCore(null);
    public GestureResult Redo(SelectionId entity) => RedoCore(entity);

    private GestureResult RedoCore(SelectionId? entity)
    {
        if (_runner.RecoverPending() is { } recovered)
            return recovered;
        if (IsRestoring)
            return GestureResult.Fail("A restore is still applying.");
        var entry = _history.PeekRedo(entity);
        if (entry == null)
            return GestureResult.Fail(entity is null ? "Nothing to redo." : "No independent redo step for this entity. Creation, removal, shared or scene-wide steps require global redo.");
        if (entry is TransformPatch { AwaitingTargets: true })
            return GestureResult.Fail(TargetsPending);
        if (entry.RequiredAsset is { } asset && !_assetExists(asset))
            return Refuse(AssetGone);
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, false, entity);
        return GiveUpOnRepeat(entry, _runner.Run(entry, false, entity));
    }

    private GestureResult ReplayUntilComplete(JournalStep step, bool before, SelectionId? entity)
    {
        var revision = _historyRevision;
        var started = _runner.Replay(step, before, entity);
        if (!started.Attempted || !started.Result.Success) return GiveUpOnRepeat(step, started);
        _refused = null;
        if (revision != _historyRevision) return Refuse(Dropped);
        _restoring = step;
        var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        GestureResult? completed = null;
        bool Current() => _restoring == step
            && revision == _historyRevision
            && (before ? _history.PeekUndo(entity) : _history.PeekRedo(entity))?.Id == step.Id;
        void Finish(GestureResult result)
        {
            if (_restoring != step) { cancellation.Dispose(); return; }
            bool current = Current();
            _restoring = null;
            _replayCancellation = null;
            cancellation.Dispose();
            if (!current) { completed = Refuse(Dropped); return; }
            if (!result.Success) { completed = Refuse(result.Detail ?? RestoreFailed); return; }
            if (before) _history.CommitUndo(step, entity); else _history.CommitRedo(step, entity);
            completed = result;
        }
        step.CompleteReplay!(before, Current, cancellation.Token, Finish);
        return completed ?? GestureResult.Ok();
    }

    private GestureResult Refuse(string why)
    {
        _notices.Note(why);
        return GestureResult.Fail(why);
    }

}
