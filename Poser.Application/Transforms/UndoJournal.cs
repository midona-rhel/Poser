using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>What runs an entry's delta: the gesture service, which owns the
/// recovery barrier every mutation shares.</summary>
public interface IUndoRunner
{
    GestureResult Undo();
    GestureResult Redo();
    GestureResult Replay(JournalStep step, bool before) =>
        GestureResult.Fail("Deferred history replay is not supported by this runner.");
    GestureResult? RecoverPending() => null;
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

    private readonly TransformHistory _history;
    private readonly IUndoRunner _runner;
    private readonly Func<string, bool> _assetExists;
    private readonly Action<string> _notice;
    private HistoryEntry? _restoring;
    private CancellationTokenSource? _replayCancellation;

    public UndoJournal(
        TransformHistory history,
        IUndoRunner runner,
        Func<string, bool> assetExists,
        Action<string> notice)
    {
        _history = history;
        _runner = runner;
        _assetExists = assetExists;
        _notice = notice;
        _history.Cleared += () =>
        {
            _restoring = null;
            _replayCancellation?.Cancel();
        };
    }

    /// <summary>True while an explicit restore is in flight; undo and redo
    /// wait for it.</summary>
    public bool IsRestoring => _restoring != null;

    public bool CanUndo => !IsRestoring && _history.CanUndo;
    public bool CanRedo => !IsRestoring && _history.CanRedo;
    public string? UndoDescription => _history.UndoDescription;
    public string? RedoDescription => _history.RedoDescription;

    public GestureResult Undo()
    {
        if (_runner.RecoverPending() is { } recovered)
            return recovered;
        if (IsRestoring)
            return GestureResult.Fail("A restore is still applying.");
        var entry = _history.PeekUndo();
        if (entry == null)
            return GestureResult.Fail("Nothing to undo.");
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, true);
        return GiveUpOnRepeat(entry, _runner.Undo());
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
        if (entry is SceneLifecyclePatch { DropOnFailure: { } shouldDrop }
            && shouldDrop())
        {
            _refused = null;
            _history.Drop(entry);
            var reason = entry is SceneLifecyclePatch lifecycle
                ? lifecycle.FailureDetail?.Invoke() ?? result.Detail
                : result.Detail;
            _notice(reason ?? $"{entry.Description} could not be restored and was discarded.");
            return result;
        }
        if (entry is not JournalStep step || step.RetainOnFailure
            || step.HasDeferredGroupCapture?.Invoke() == true)
        {
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
        _notice($"{step.Description} could not be undone twice and was discarded.");
        return result;
    }

    public GestureResult Redo()
    {
        if (_runner.RecoverPending() is { } recovered)
            return recovered;
        if (IsRestoring)
            return GestureResult.Fail("A restore is still applying.");
        var entry = _history.PeekRedo();
        if (entry == null)
            return GestureResult.Fail("Nothing to redo.");
        if (entry.RequiredAsset is { } asset && !_assetExists(asset))
            return Refuse(AssetGone);
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, false);
        return GiveUpOnRepeat(entry, _runner.Redo());
    }

    private GestureResult ReplayUntilComplete(JournalStep step, bool before)
    {
        var started = _runner.Replay(step, before);
        if (!started.Success) return GiveUpOnRepeat(step, started);
        _restoring = step;
        var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        GestureResult? completed = null;
        bool Current() => _restoring == step
            && (before ? _history.PeekUndo() : _history.PeekRedo())?.Id == step.Id;
        void Finish(GestureResult result)
        {
            if (_restoring != step) { cancellation.Dispose(); return; }
            bool current = Current();
            _restoring = null;
            _replayCancellation = null;
            cancellation.Dispose();
            if (!current) { completed = Refuse(Dropped); return; }
            if (!result.Success) { completed = Refuse(result.Detail ?? RestoreFailed); return; }
            if (before) _history.CommitUndo(step); else _history.CommitRedo(step);
            completed = result;
        }
        try { step.CompleteReplay!(before, Current, cancellation.Token, Finish); }
        catch (Exception ex) { Finish(GestureResult.Fail(ex.Message)); }
        return completed ?? GestureResult.Ok();
    }

    private GestureResult Refuse(string why)
    {
        _notice(why);
        return GestureResult.Fail(why);
    }

}
