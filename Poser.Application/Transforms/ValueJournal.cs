namespace Poser.Application.Transforms;

public readonly record struct ValueWriteResult(bool Success, string? Detail = null)
{
    public static ValueWriteResult Ok() => new(true);
}

/// <summary>
/// Value changes as journal steps. A set reads the old value, writes the
/// new one and appends one step whose undo writes the old value back.
/// Continuous controls stage their before/after values until <see cref="Seal"/>.
/// Discrete writes append immediately, even on the same property. A step whose target
/// has died undoes as a no-op: there is nothing left to put back, and the
/// steps under it must stay reachable.
/// </summary>
public sealed class ValueJournal
{
    private readonly TransformHistory _history;
    private readonly JournalContexts? _contexts;
    private PendingEdit? _pending;
    private object? _control;
    private int _editing;
    private readonly Dictionary<object, StagedValue> _staged = new();
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
        // A spawn, bake or transform may append through another journal
        // owner. Commit earlier value edits before that discrete action.
        _history.BeforeAppend += Seal;
        _history.Cleared += () => { _pending = null; _staged.Clear(); _control = null; };
    }

    /// <summary>
    /// Writes <paramref name="value"/> and journals the change. Nothing is
    /// written or journaled when the value already holds.
    /// </summary>
    /// <param name="key">What is being set: the target and the property.
    /// Equal keys share a baseline within a continuous edit.</param>
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
        if (_editing == 0) CommitPending();
        var current = read();
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        if (_suspended > 0)
        {
            write(value);
            return;
        }
        var scope = actors is { } lineages ? _contexts?.BeginActorStep(lineages) : null;
        write(value);
        var before = current;
        JournalStep Step(T after) => new(
            description, () => Put(alive, write, before), () => Put(alive, write, after))
        {
            Context = scope?.Complete(),
            BeforeValue = before,
            AfterValue = after,
        };
        if (_editing > 0) Stage(key, before, value, Step);
        else _history.Append(Step(value));
    }

    /// <summary>
    /// Journals a change that has already been written — for a write that
    /// can refuse, so only a landed change is a step. Continuous controls
    /// stage the original before and latest after; other calls append once.
    /// </summary>
    public void Record<T>(
        string description,
        T before,
        T after,
        Action<T> write,
        Func<bool>? alive = null)
    {
        if (_editing == 0) CommitPending();
        if (EqualityComparer<T>.Default.Equals(before, after) || _suspended > 0)
            return;
        JournalStep Step(T next) => new(
            description,
            () => Put(alive, write, before),
            () => Put(alive, write, next))
        {
            BeforeValue = before,
            AfterValue = next,
        };
        if (_editing > 0) Stage((description, typeof(T)), before, after, Step);
        else _history.Append(Step(after));
    }

    /// <summary>Like Set, but a refused write never changes history or the
    /// staged after value. Refused inverses retain their existing result behavior.</summary>
    public ValueWriteResult TrySet<T>(object key, string description, Func<T> read,
        Func<T, ValueWriteResult> write, T value, Func<bool>? alive = null)
    {
        if (_editing == 0) CommitPending();
        var before = read();
        if (EqualityComparer<T>.Default.Equals(before, value))
            return ValueWriteResult.Ok();
        var result = WriteResult(write, value);
        if (!result.Success || _suspended > 0)
            return result;
        JournalStep Step(T after) => ResultStep(description, before, after, () => after, write, alive);
        if (_editing > 0) Stage(key, before, value, Step);
        else _history.Append(Step(value));
        return result;
    }

    /// <summary>Records an already successful transaction whose inverses can refuse.</summary>
    public void RecordResult<T>(string description, T before, T after,
        Func<T, ValueWriteResult> write, Func<bool>? alive = null)
    {
        if (_editing == 0) CommitPending();
        if (EqualityComparer<T>.Default.Equals(before, after) || _suspended > 0)
            return;
        JournalStep Step(T next) => ResultStep(description, before, next, () => next, write, alive);
        if (_editing > 0) Stage((description, typeof(T)), before, after, Step);
        else _history.Append(Step(after));
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

    /// <summary>Commits the pending gesture. A later edit starts with a new baseline.</summary>
    public void Seal()
    {
        // Session methods may seal discrete actions themselves. The shared
        // control owns the boundary while it is applying a live value.
        if (_editing > 0) return;
        CommitPending();
    }

    public void BeginEdit(object control)
    {
        if (_editing == 0 && !Equals(_control, control)) Seal();
        _control = control;
        _editing++;
    }

    public void EndEdit() => _editing--;

    public void CommitEdit(object control)
    {
        if (Equals(_control, control)) Seal();
    }

    private void Stage<T>(object key, T before, T after, Func<T, JournalStep> step)
    {
        if (_staged.TryGetValue(key, out var prior)) { prior.SetAfter(after); return; }
        var box = new Box<T> { Value = after };
        _staged.Add(key, new(next => box.Value = (T)next!,
            () => EqualityComparer<T>.Default.Equals(before, box.Value) ? null : step(box.Value)));
    }

    /// <summary>Live numeric edit. History changes only when the control
    /// commits (release or typed focus loss), and a net no-op appends nothing.</summary>
    public ValueWriteResult Adjust<T>(object key, string description, Func<T> read,
        Func<T, ValueWriteResult> write, T value, Func<bool>? alive = null)
    {
        if (_editing > 0) return TrySet(key, description, read, write, value, alive);
        if (_staged.Count > 0) Seal();
        if (_pending is { } prior && !prior.Key.Equals(key))
            Seal();
        var before = read();
        if (EqualityComparer<T>.Default.Equals(before, value))
            return ValueWriteResult.Ok();
        var result = WriteResult(write, value);
        if (!result.Success || _suspended > 0)
            return result;
        if (_pending is { } pending)
        {
            pending.SetAfter(value!);
            return result;
        }
        var box = new Box<T> { Value = value };
        _pending = new PendingEdit(key, next => box.Value = (T)next,
            () => RecordResult(description, before, box.Value, write, alive));
        return result;
    }

    private void CommitPending()
    {
        var steps = _staged.Values.Select(value => value.Build()).OfType<JournalStep>().ToArray();
        _staged.Clear();
        _control = null;
        if (steps.Length == 1) _history.Append(steps[0]);
        else if (steps.Length > 1)
        {
            // One control can write several channels/targets. Keep one entry,
            // undo in reverse write order and redo in the original order.
            _history.Append(new JournalStep(steps[0].Description,
                () => steps.Reverse().All(step => step.Undo()),
                () => steps.All(step => step.Redo()))
            {
                BeforeValue = steps.Select(step => step.BeforeValue).ToArray(),
                AfterValue = steps.Select(step => step.AfterValue).ToArray(),
                RetainOnFailure = steps.Any(step => step.RetainOnFailure),
                FailureDetail = () => steps.Select(step => step.FailureDetail?.Invoke()).FirstOrDefault(detail => detail is not null),
            });
        }
        var pending = _pending;
        _pending = null;
        pending?.Commit();
    }

    private sealed record PendingEdit(object Key, Action<object> SetAfter, Action Commit);
    private sealed record StagedValue(Action<object?> SetAfter, Func<JournalStep?> Build);

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

}
