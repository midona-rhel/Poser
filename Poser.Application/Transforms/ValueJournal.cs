namespace Poser.Application.Transforms;

/// <summary>
/// Value changes as journal steps. A set reads the old value, writes the
/// new one and appends one step whose undo writes the old value back.
/// Consecutive sets on the same key (a slider being dragged, a name being
/// typed) fold into the step already on top of the stack, so a drag is
/// one step and a word is one step; <see cref="Seal"/> closes the open
/// step so the next set on that key starts a new one. A step whose target
/// has died undoes as a no-op: there is nothing left to put back, and the
/// steps under it must stay reachable.
/// </summary>
public sealed class ValueJournal
{
    private readonly TransformHistory _history;
    private readonly JournalContexts? _contexts;
    private OpenStep? _open;
    private int _suspended;

    /// <summary>
    /// While held, sets and records still write but append nothing: the
    /// inverse of a composite step re-runs the surface's own routines, and
    /// those must not journal again inside the undo.
    /// </summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Resume(this);
    }

    public bool IsSuspended => _suspended > 0;

    private sealed class Resume(ValueJournal owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._suspended--;
        }
    }

    public ValueJournal(TransformHistory history, JournalContexts? contexts = null)
    {
        _history = history;
        _contexts = contexts;
        _history.Cleared += () => _open = null;
    }

    /// <summary>
    /// Writes <paramref name="value"/> and journals the change. Nothing is
    /// written or journaled when the value already holds.
    /// </summary>
    /// <param name="key">What is being set: the target and the property.
    /// Equal keys fold into one open step.</param>
    /// <param name="alive">Whether the target still exists; a dead target
    /// makes the step's undo and redo no-ops.</param>
    /// <param name="actors">The lineages of the actors the value belongs
    /// to, when it belongs to one; the step then carries their keys.</param>
    public void Set<T>(
        object key,
        string description,
        Func<T> read,
        Action<T> write,
        T value,
        Func<bool>? alive = null,
        IEnumerable<Guid>? actors = null)
    {
        var current = read();
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        if (_suspended > 0)
        {
            write(value);
            return;
        }
        if (_open is { } open
            && open.Key.Equals(key)
            && ReferenceEquals(_history.PeekUndo(), open.Entry))
        {
            write(value);
            open.SetAfter(value!);
            return;
        }
        var scope = actors is { } lineages ? _contexts?.BeginActorStep(lineages) : null;
        write(value);
        var box = new Box<T> { Value = value };
        var before = current;
        var entry = new JournalStep(
            description,
            () => Put(alive, write, before),
            () => Put(alive, write, box.Value))
        {
            Context = scope?.Complete(),
        };
        _history.Append(entry);
        _open = new OpenStep(key, entry, next => box.Value = (T)next);
    }

    /// <summary>
    /// Journals a change that has already been written — for a write that
    /// can refuse, so only a landed change is a step. Never folds.
    /// </summary>
    public void Record<T>(
        string description,
        T before,
        T after,
        Action<T> write,
        Func<bool>? alive = null)
    {
        if (EqualityComparer<T>.Default.Equals(before, after) || _suspended > 0)
            return;
        _history.Append(new JournalStep(
            description,
            () => Put(alive, write, before),
            () => Put(alive, write, after)));
        _open = null;
    }

    /// <summary>Closes the open step: the next set on its key starts a new
    /// one. Call it when a drag begins or a field commits.</summary>
    public void Seal() => _open = null;

    private static bool Put<T>(Func<bool>? alive, Action<T> write, T value)
    {
        if (alive is not null && !alive())
            return true;
        try
        {
            write(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Box<T>
    {
        public T Value = default!;
    }

    private sealed record OpenStep(object Key, HistoryEntry Entry, Action<object> SetAfter);
}
