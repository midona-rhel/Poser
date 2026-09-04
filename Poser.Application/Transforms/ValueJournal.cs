namespace Poser.Application.Transforms;

public readonly record struct ValueWriteResult(bool Success, string? Detail = null)
{
    public static ValueWriteResult Ok() => new(true);
}

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
    /// <summary>A step folded a later value into itself: the entry and
    /// its new after value, for the action recorder.</summary>
    public event Action<HistoryEntry, object?>? Folded;

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
            Folded?.Invoke(open.Entry, value);
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
            BeforeValue = before,
            AfterValue = value,
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
            () => Put(alive, write, after))
        {
            BeforeValue = before,
            AfterValue = after,
        });
        _open = null;
    }

    /// <summary>Like Set, but a refused write never appends or changes the
    /// open step. Refused inverses stay available for retry with their detail.</summary>
    public ValueWriteResult TrySet<T>(object key, string description, Func<T> read,
        Func<T, ValueWriteResult> write, T value, Func<bool>? alive = null)
    {
        var before = read();
        if (EqualityComparer<T>.Default.Equals(before, value))
            return ValueWriteResult.Ok();
        var result = WriteResult(write, value);
        if (!result.Success || _suspended > 0)
            return result;
        if (_open is { } open && open.Key.Equals(key)
            && ReferenceEquals(_history.PeekUndo(), open.Entry))
        {
            open.SetAfter(value!);
            Folded?.Invoke(open.Entry, value);
            return result;
        }
        var box = new Box<T> { Value = value };
        var entry = ResultStep(description, before, value, () => box.Value, write, alive);
        _history.Append(entry);
        _open = new OpenStep(key, entry, next => box.Value = (T)next);
        return result;
    }

    /// <summary>Records an already successful transaction whose inverses can refuse.</summary>
    public void RecordResult<T>(string description, T before, T after,
        Func<T, ValueWriteResult> write, Func<bool>? alive = null)
    {
        if (EqualityComparer<T>.Default.Equals(before, after) || _suspended > 0)
            return;
        _history.Append(ResultStep(description, before, after, () => after, write, alive));
        _open = null;
    }

    private static JournalStep ResultStep<T>(string description, T before, T after,
        Func<T> latest, Func<T, ValueWriteResult> write, Func<bool>? alive)
    {
        string? failure = null;
        bool PutResult(T value)
        {
            failure = null;
            if (alive is not null && !alive())
                return true;
            var result = WriteResult(write, value);
            failure = result.Detail;
            return result.Success;
        }
        return new JournalStep(description, () => PutResult(before), () => PutResult(latest()))
        {
            BeforeValue = before,
            AfterValue = after,
            RetainOnFailure = true,
            FailureDetail = () => failure,
        };
    }

    private static ValueWriteResult WriteResult<T>(Func<T, ValueWriteResult> write, T value)
    {
        try { return write(value); }
        catch (Exception ex) { return new(false, ex.Message); }
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
