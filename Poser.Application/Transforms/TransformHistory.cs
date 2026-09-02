using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>
/// One undoable action. Transform entries restore captured state; lifecycle
/// entries run the inverse action through the service that owns the entity.
/// </summary>
public abstract record HistoryEntry(string Description)
{
    /// <summary>The keys and snapshots the step was recorded under; null
    /// for an entry that never invalidates.</summary>
    public StepContext? Context { get; init; }
}

public sealed record TransformPatch(
    string Description,
    IReadOnlyList<TransformTargetState> Before,
    IReadOnlyList<TransformTargetState> After) : HistoryEntry(Description);

/// <summary>
/// A scene-lifecycle action. Its undo and redo delegates report whether the
/// action landed and resolve the entity again when it is recreated.
/// </summary>
public sealed record SceneLifecyclePatch(
    string Description,
    Func<bool> Undo,
    Func<bool> Redo) : HistoryEntry(Description);

/// <summary>
/// Bounded before/after patch history. Capacity is read for each append, and
/// zero clears both stacks while still notifying observers.
/// </summary>
public sealed class TransformHistory
{
    /// <summary>The depth used when no setting is supplied (tests, and the
    /// parameterless construction the DI default would use).</summary>
    public const int DefaultCapacity = 500;

    private static readonly Func<int> FixedDefault = static () => DefaultCapacity;

    private readonly Func<int> _capacity;
    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();

    public event Action? PatchAppended;

    public TransformHistory()
        : this(FixedDefault)
    {
    }

    public TransformHistory(Func<int> capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? _redo[^1].Description : null;

    public void Append(HistoryEntry patch)
    {
        int capacity = _capacity();
        if (capacity < 1)
        {
            // Capacity zero disables undo and redo, including the new entry.
            _undo.Clear();
            _redo.Clear();
            RaiseCleared();
        }
        else
        {
            _undo.Add(patch);
            while (_undo.Count > capacity)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
        // Observers run after the history mutation and cannot roll it back.
        if (PatchAppended is { } observers)
            foreach (Action observer in observers.GetInvocationList())
                try
                {
                    observer();
                }
                catch
                {
                    // Observers have no transaction authority or result channel.
                }
    }

    public HistoryEntry? PeekUndo() =>
        CanUndo ? _undo[^1] : null;

    public void CommitUndo(HistoryEntry patch)
    {
        if (!CanUndo || !ReferenceEquals(_undo[^1], patch))
            throw new InvalidOperationException(
                "Undo history changed before commit.");
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(patch);
    }

    public HistoryEntry? PeekRedo() =>
        CanRedo ? _redo[^1] : null;

    public void CommitRedo(HistoryEntry patch)
    {
        if (!CanRedo || !ReferenceEquals(_redo[^1], patch))
            throw new InvalidOperationException(
                "Redo history changed before commit.");
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(patch);
    }

    /// <summary>
    /// Drops every patch touching a target that is no longer current. A
    /// patch is removed whole when ANY of its targets is stale (its restore
    /// could never succeed); patches whose targets all remain current
    /// survive, so replacing one slot never discards history that involves
    /// only unaffected slots or actors.
    ///
    /// <para>A lifecycle entry is never stale by this rule and is never
    /// dropped: it holds no target state, and the entity it names is
    /// deliberately absent for exactly half of its life — an "add light"
    /// entry whose light has been undone away is precisely the entry that
    /// must survive to be redone.</para>
    /// </summary>
    /// <summary>
    /// Drops what can never come back. A transform patch with a stale target
    /// survives only while it carries snapshots and every actor it keyed
    /// still exists — a new generation is an invalidation, not a loss. Any
    /// keyed entry goes when one of its actors is gone for good.
    /// </summary>
    public void Reconcile(
        Func<Poser.Domain.Identity.TransformTargetId, bool> isCurrent,
        Func<Guid, bool> lineagePresent)
    {
        bool ActorsGone(HistoryEntry entry) =>
            entry.Context is { } context &&
            context.Keys.Any(key => !lineagePresent(key.Lineage));
        bool Stale(HistoryEntry entry)
        {
            if (ActorsGone(entry))
                return true;
            if (entry is not TransformPatch patch)
                return false;
            bool staleTarget =
                patch.Before.Any(state => !isCurrent(state.Target)) ||
                patch.After.Any(state => !isCurrent(state.Target));
            if (!staleTarget)
                return false;
            return patch.Context is not { Before.Count: > 0 };
        }
        _undo.RemoveAll(entry => Stale(entry));
        _redo.RemoveAll(entry => Stale(entry));
    }

    /// <summary>Forgets one entry wherever it sits, without an event: the
    /// journal's answer to a restore that outlived its place.</summary>
    public void Drop(HistoryEntry entry)
    {
        _undo.Remove(entry);
        _redo.Remove(entry);
    }

    /// <summary>
    /// Raised whenever both stacks are emptied — by <see cref="Clear"/>, and
    /// equally by the undo-off branch of <see cref="Append"/>, which empties
    /// them just as thoroughly.
    ///
    /// <para>A lifecycle entry closes over state its owner keeps beside the
    /// stack — the slot that re-binds an entity across a destroy/respawn pair
    /// — and that state is meaningless once the entries naming it are gone.
    /// Announcing the emptying keeps the answer in ONE place: whoever put
    /// state behind an entry drops it here, rather than every site that
    /// empties the stacks having to remember a second sweep. This class's own
    /// Append forgot exactly that, which is the point.</para>
    /// </summary>
    public event Action? Cleared;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        RaiseCleared();
    }

    private void RaiseCleared()
    {
        if (Cleared is { } observers)
            foreach (Action observer in observers.GetInvocationList())
                try
                {
                    observer();
                }
                catch
                {
                    // Observers have no transaction authority or result
                    // channel, exactly as for PatchAppended.
                }
    }
}
