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
    GestureResult CompleteSnapshotRestore(HistoryEntry entry, bool before, Action commit)
    {
        commit();
        return GestureResult.Ok();
    }
}

/// <summary>
/// Undo and redo for every surface. The journal decides HOW an entry comes
/// back: an entry whose actor keys still match runs its delta through the
/// runner; an entry whose key moved restores the actors' snapshots
/// instead, silently, with one notice. A redo that depends on a file asks
/// for the file first.
/// </summary>
public sealed class UndoJournal
{
    public const string RestoredFromSnapshot =
        "Undo restored the pose from a snapshot: the actor changed since.";
    public const string RedoneFromSnapshot =
        "Redo restored the pose from a snapshot: the actor changed since.";
    public const string AssetGone = "Cannot redo: the file is no longer there.";
    public const string ActorGone = "Cannot undo: the actor is gone.";
    public const string RestoreFailed = "The pose could not be restored.";
    public const string Dropped = "The step was dropped: the history changed while restoring.";

    private readonly TransformHistory _history;
    private readonly IUndoRunner _runner;
    private readonly IActorStateKeySource _keys;
    private readonly Lazy<IPoseSnapshotPort> _snapshots;
    private readonly Func<string, bool> _assetExists;
    private readonly Action<string> _notice;
    private HistoryEntry? _restoring;
    private CancellationTokenSource? _replayCancellation;

    public UndoJournal(
        TransformHistory history,
        IUndoRunner runner,
        IActorStateKeySource keys,
        Lazy<IPoseSnapshotPort> snapshots,
        Func<string, bool> assetExists,
        Action<string> notice)
    {
        _history = history;
        _runner = runner;
        _keys = keys;
        _snapshots = snapshots;
        _assetExists = assetExists;
        _notice = notice;
        _history.Cleared += () =>
        {
            _restoring = null;
            _replayCancellation?.Cancel();
        };
    }

    /// <summary>True while a snapshot restore is in flight; undo and redo
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
        if (entry is JournalStep { RestoreSnapshotsAfterReplay: true } step)
            return ReplayWithSnapshots(step, true);
        if (entry.Context is { } context)
        {
            var validity = Validity(context);
            if (validity == KeyState.Gone)
                return Refuse(ActorGone);
            if (validity == KeyState.Moved)
                return RestoreSnapshots(
                    entry, context.Before, true, RestoredFromSnapshot,
                    () => _history.PeekUndo()?.Id == entry.Id,
                    () => _history.CommitUndo(entry));
        }
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
        if (entry is JournalStep { CompleteReplay: not null } pendingStep)
            return ReplayUntilComplete(pendingStep, false);
        if (entry.Context is { } context)
        {
            if (context.Asset is { } asset && !_assetExists(asset))
                return Refuse(AssetGone);
            if (entry is JournalStep { RestoreSnapshotsAfterReplay: true } step)
                return ReplayWithSnapshots(step, false);
            var validity = Validity(context);
            if (validity == KeyState.Gone)
                return Refuse(ActorGone);
            if (validity == KeyState.Moved)
                return RestoreSnapshots(
                    entry, context.After, false, RedoneFromSnapshot,
                    () => _history.PeekRedo()?.Id == entry.Id,
                    () => _history.CommitRedo(entry));
        }
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

    private GestureResult ReplayWithSnapshots(JournalStep step, bool before)
    {
        if (step.Context is not { } context) return Refuse(RestoreFailed);
        if (Validity(context) == KeyState.Gone) return Refuse(ActorGone);
        var result = _runner.Replay(step, before);
        if (!result.Success) return GiveUpOnRepeat(step, result);
        return RestoreSnapshots(step, before ? context.Before : context.After, before, null,
            () => (before ? _history.PeekUndo() : _history.PeekRedo())?.Id == step.Id,
            () => { if (before) _history.CommitUndo(step); else _history.CommitRedo(step); },
            allowEmpty: true);
    }

    private enum KeyState { Current, Moved, Gone }

    /// <summary>Whether a step's keys are checked before its delta runs.
    /// Off, every step is current and undoes by its delta; the redo asset
    /// check stays. See <see cref="JournalContexts.StateKeys"/>.</summary>
    public bool StateKeys { get; set; }

    private KeyState Validity(StepContext context)
    {
        if (!StateKeys)
            return KeyState.Current;
        var state = KeyState.Current;
        foreach (var key in context.Keys)
        {
            if (_keys.Current(key.Lineage) is not { } current)
                return KeyState.Gone;
            if (!key.Matches(current))
                state = KeyState.Moved;
        }
        return state;
    }

    private GestureResult Refuse(string why)
    {
        _notice(why);
        return GestureResult.Fail(why);
    }

    /// <summary>Restores the snapshots one after another (a restore is an
    /// import, and one import runs at a time), then commits the entry if
    /// it is still where it was.</summary>
    private GestureResult RestoreSnapshots(
        HistoryEntry entry,
        IReadOnlyList<ActorSnapshot> snapshots,
        bool before,
        string? done,
        Func<bool> stillOnTop,
        Action commit,
        bool allowEmpty = false)
    {
        if (snapshots.Count == 0 && !allowEmpty)
            return Refuse(RestoreFailed);
        _restoring = entry;
        GestureResult? completed = null;
        RestoreFrom(0);
        return completed ?? GestureResult.Ok();

        bool Current() => _restoring == entry && stillOnTop();

        void RestoreFrom(int index)
        {
            if (!Current()) { Finish(false); return; }
            if (index >= snapshots.Count)
            {
                Finish(true);
                return;
            }
            try
            {
                bool started = _snapshots.Value.Restore(snapshots[index], Current, ok =>
                {
                    if (_restoring != entry) return;
                    if (!ok) Finish(false);
                    else RestoreFrom(index + 1);
                });
                if (!started) Finish(false);
            }
            catch { Finish(false); }
        }

        void Finish(bool ok)
        {
            if (_restoring != entry) return;
            _restoring = null;
            if (!stillOnTop())
            {
                _history.Drop(entry);
                completed = Refuse(Dropped);
                return;
            }
            if (!ok)
            {
                completed = Refuse(RestoreFailed);
                return;
            }
            var result = _runner.CompleteSnapshotRestore(entry, before, () =>
            {
                if (stillOnTop())
                {
                    commit();
                    if (done != null) _notice(done);
                }
            });
            if (!result.Success) _notice(result.Detail ?? RestoreFailed);
            completed = result;
        }
    }
}
