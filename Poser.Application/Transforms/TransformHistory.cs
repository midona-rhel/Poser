namespace Poser.Application.Transforms;

public sealed record TransformPatch(
    string Description,
    IReadOnlyList<TransformTargetState> Before,
    IReadOnlyList<TransformTargetState> After);

/// <summary>Bounded before/after patch history.</summary>
public sealed class TransformHistory
{
    private readonly int _capacity;
    private readonly List<TransformPatch> _undo = new();
    private readonly List<TransformPatch> _redo = new();

    public event Action? PatchAppended;

    public TransformHistory(int capacity = 200)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? _redo[^1].Description : null;

    public void Append(TransformPatch patch)
    {
        _undo.Add(patch);
        if (_undo.Count > _capacity)
            _undo.RemoveAt(0);
        _redo.Clear();
        PatchAppended?.Invoke();
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
