namespace Poser.Application.Transforms;

public sealed record TransformPatch(
    string Description,
    IReadOnlyList<TransformTargetState> Before,
    IReadOnlyList<TransformTargetState> After);

/// <summary>
/// Bounded before/after patch history.
///
/// <para>The bound is READ PER APPEND, not captured at construction: the depth
/// is a user setting, so raising or lowering it takes effect on the next edit
/// rather than on the next plugin load. Zero means undo is OFF — the stacks are
/// emptied and the patch is dropped, so an edit still applies but nothing
/// records it. Observers are notified either way, because the badge that reads
/// <see cref="CanUndo"/> must learn that undo just became impossible.</para>
/// </summary>
public sealed class TransformHistory
{
    /// <summary>The depth used when no setting is supplied (tests, and the
    /// parameterless construction the DI default would use).</summary>
    public const int DefaultCapacity = 200;

    private static readonly Func<int> FixedDefault = static () => DefaultCapacity;

    private readonly Func<int> _capacity;
    private readonly List<TransformPatch> _undo = new();
    private readonly List<TransformPatch> _redo = new();

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

    public void Append(TransformPatch patch)
    {
        int capacity = _capacity();
        if (capacity < 1)
        {
            // Undo turned off: drop the stacks rather than keep a history the
            // setting says may not be walked.
            _undo.Clear();
            _redo.Clear();
        }
        else
        {
            _undo.Add(patch);
            while (_undo.Count > capacity)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
        // The patch is committed before observers run. A surface observer is
        // never part of the transaction and cannot turn committed history into
        // an apparent apply failure (or prevent later observers from updating).
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

    public TransformPatch? PeekUndo() =>
        CanUndo ? _undo[^1] : null;

    public void CommitUndo(TransformPatch patch)
    {
        if (!CanUndo || !ReferenceEquals(_undo[^1], patch))
            throw new InvalidOperationException(
                "Undo history changed before commit.");
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(patch);
    }

    public TransformPatch? PeekRedo() =>
        CanRedo ? _redo[^1] : null;

    public void CommitRedo(TransformPatch patch)
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
    /// </summary>
    public void Reconcile(Func<Poser.Domain.Identity.TransformTargetId, bool> isCurrent)
    {
        bool Stale(TransformPatch patch) =>
            patch.Before.Any(state => !isCurrent(state.Target)) ||
            patch.After.Any(state => !isCurrent(state.Target));
        _undo.RemoveAll(patch => Stale(patch));
        _redo.RemoveAll(patch => Stale(patch));
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
