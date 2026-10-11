using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>What runs an entry's delta: the gesture service, which owns the
/// recovery barrier every mutation shares. The journal peeks the entry and
/// picks its path; the runner never reads history to find it again.</summary>
public interface IUndoRunner
{
    /// <summary>Finishes a pending recovery before history may move; null when
    /// nothing is pending.</summary>
    GestureResult? RecoverPending();

    /// <summary>Runs one direction of a synchronous entry and commits it to
    /// history (scoped to <paramref name="entity"/> when given) on success.</summary>
    GestureResult Run(HistoryEntry entry, bool undo, SelectionId? entity);

    /// <summary>Starts one direction of a multi-frame step. The journal owns
    /// completion and the history commit.</summary>
    GestureResult Replay(JournalStep step, bool before, SelectionId? entity);
}

/// <summary>
/// Undo and redo through each entry's recorded inverse. Explicit multi-frame
/// restoration waits for completion before moving history; file redo checks
/// its required asset. Animation changes never select an implicit recovery mode.
/// </summary>
public sealed class UndoJournal
{
    public const string AssetGone = "Cannot redo: the file is no longer there.";
    public const string RestoreFailed = "The pose could not be restored.";
    public const string Dropped = "The step was dropped: the history changed while restoring.";

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
        _history.PatchAppended += () =>
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

    public bool CanUndo => !IsRestoring && _history.CanUndo;
    public bool CanRedo => !IsRestoring && _history.CanRedo;
    public string? UndoDescription => _history.UndoDescription;
    public string? RedoDescription => _history.RedoDescription;

    public GestureResult Undo() => UndoCore(null);
    public GestureResult Undo(SelectionId entity) => UndoCore(entity);

    private GestureResult UndoCore(SelectionId? entity)
    {
        if (_runner.RecoverPending() is { } recovered)
            return recovered;
        if (IsRestoring)
            return GestureResult.Fail("A restore is still applying.");
        var entry = _history.PeekUndo(entity);
        if (entry == null)
            return GestureResult.Fail(entity is null ? "Nothing to undo." : "No independent undo step for this entity. Creation, removal, shared or scene-wide steps require global undo.");
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, true, entity);
        return GiveUpOnRepeat(entry, _runner.Run(entry, true, entity));
    }

    /// <summary>The entry the runner refused last; the same entry refused
    /// again is dropped, so one dead step (a bake whose bones are gone, a
    /// rollback that cannot land) never wedges every later undo.</summary>
    private HistoryEntry? _refused;

    private GestureResult GiveUpOnRepeat(HistoryEntry entry, GestureResult result)
    {
        if (result.Success)
        {
            _refused = null;
            return result;
        }
        switch (RefusalPolicy.Decide(entry))
        {
            case RefusalAction.DropNow:
                _refused = null;
                _history.Drop(entry);
                var reason = (entry as InverseEntry)?.FailureDetail?.Invoke() ?? result.Detail;
                _notices.Note(reason ?? $"{entry.Description} could not be restored and was discarded.");
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
        if (!started.Success) return GiveUpOnRepeat(step, started);
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
        try { step.CompleteReplay!(before, Current, cancellation.Token, Finish); }
        catch (Exception ex) { Finish(GestureResult.Fail(ex.Message)); }
        return completed ?? GestureResult.Ok();
    }

    private GestureResult Refuse(string why)
    {
        _notices.Note(why);
        return GestureResult.Fail(why);
    }

}
