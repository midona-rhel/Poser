using Poser.Domain;
using Poser.Domain.Identity;
using System.Runtime.CompilerServices;

namespace Poser.Application.Transforms;

/// <summary>Adapts a write that cannot report a refusal. Only for runtimes
/// whose setters are still void (environment, animation, IK, parenting).</summary>
public static class ValueWrites
{
    public static Func<T, Outcome> Unchecked<T>(Action<T> write) =>
        value => { write(value); return Outcome.Ok(); };
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
    private readonly Func<object, SelectionId?>? _identify;
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

    public ValueJournal(TransformHistory history, Func<object, SelectionId?>? identify = null)
    {
        _history = history;
        _identify = identify;
        // A spawn, bake or transform may append through another journal
        // owner. Commit earlier value edits before that discrete action.
        _history.BeforeAppend += Seal;
        _history.Cleared += () => { _pending = null; _staged.Clear(); _control = null; };
    }

    /// <summary>
    /// Writes <paramref name="value"/> and journals the change. Nothing is
    /// written or journaled when the value already holds. A refused (or
    /// throwing) write returns its detail and leaves history, redo and the
    /// staged after value unchanged.
    /// </summary>
    /// <param name="key">What is being set: the target and the property.
    /// Equal keys share a baseline within a continuous edit.</param>
    /// <param name="alive">Whether the target still exists; a dead target
    /// makes the step's undo and redo no-ops.</param>
    public Outcome Set<T>(object key, string description, Func<T> read,
        Func<T, Outcome> write, T value, Func<bool>? alive = null)
    {
        if (_editing == 0) CommitPending();
        var before = read();
        if (EqualityComparer<T>.Default.Equals(before, value))
            return Outcome.Ok();
        var result = WriteResult(write, value);
        if (!result.Success || _suspended > 0)
            return result;
        JournalStep Step(T after) => ResultStep(description, before, after, write, alive)
            with { AffectedEntities = Scope(key) };
        if (_editing > 0) Stage(key, before, value, Step);
        else _history.Append(Step(value));
        return result;
    }

    /// <summary>
    /// Journals a change that has already landed, so only a landed change is
    /// a step. The key's owner scopes the step; a key that names no entity
    /// leaves it global. Continuous controls stage the original before and
    /// latest after; other calls append once.
    /// </summary>
    public void Record<T>(object key, string description, T before, T after,
        Func<T, Outcome> write, Func<bool>? alive = null)
    {
        if (_editing == 0) CommitPending();
        if (EqualityComparer<T>.Default.Equals(before, after) || _suspended > 0)
            return;
        JournalStep Step(T next) => ResultStep(description, before, next, write, alive)
            with { AffectedEntities = Scope(key) };
        if (_editing > 0) Stage((key, description, typeof(T)), before, after, Step);
        else _history.Append(Step(after));
    }

    // Only a transient refusal keeps the step for retry. A permanent refusal
    // or a throw is reported once and dropped when the same step refuses
    // again, so one inverse that can never land does not wedge history.
    private static JournalStep ResultStep<T>(string description, T before, T after,
        Func<T, Outcome> write, Func<bool>? alive)
    {
        string? failure = null;
        bool transient = false;
        bool Put(T value)
        {
            failure = null;
            transient = false;
            if (alive is not null && !alive())
                return true;
            try
            {
                var result = write(value);
                failure = result.Detail;
                transient = !result.Success && result.Transient;
                return result.Success;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }
        return new JournalStep(description, () => Put(before), () => Put(after))
        {
            BeforeValue = before,
            AfterValue = after,
            OnRefusal = () => transient ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
            FailureDetail = () => failure,
        };
    }

    private static Outcome WriteResult<T>(Func<T, Outcome> write, T value)
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
    public Outcome Adjust<T>(object key, string description, Func<T> read,
        Func<T, Outcome> write, T value, Func<bool>? alive = null)
    {
        if (_editing > 0) return Set(key, description, read, write, value, alive);
        if (_staged.Count > 0) Seal();
        if (_pending is { } prior && !prior.Key.Equals(key))
            Seal();
        var before = read();
        if (EqualityComparer<T>.Default.Equals(before, value))
            return Outcome.Ok();
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
            () => Record(key, description, before, box.Value, write, alive));
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
                AffectedEntities = steps.All(step => step.AffectedEntities is not null)
                    ? steps.SelectMany(step => step.AffectedEntities!).Distinct().ToArray() : null,
                OnRefusal = () => steps.Any(step => RefusalPolicy.Decide(step) == RefusalAction.Keep)
                    ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
                FailureDetail = () => steps.Select(step => step.FailureDetail?.Invoke()).FirstOrDefault(detail => detail is not null),
            });
        }
        var pending = _pending;
        _pending = null;
        pending?.Commit();
    }

    private IReadOnlyList<SelectionId>? Scope(object key)
    {
        // Value keys identify their owner first, followed by a property/channel.
        while (key is ITuple { Length: > 0 } tuple && tuple[0] is { } owner) key = owner;
        SelectionId? entity = key switch
        {
            SelectionId id => id,
            ActorId actor => SelectionId.ForActor(actor),
            BoneId bone => SelectionId.ForBone(bone),
            TransformTargetId target => target.ToSelectionId(),
            _ => _identify?.Invoke(key),
        };
        return entity is { } found ? new[] { TransformHistory.EntityOf(found) } : null;
    }

    private sealed record PendingEdit(object Key, Action<object> SetAfter, Action Commit);
    private sealed record StagedValue(Action<object?> SetAfter, Func<JournalStep?> Build);

    private sealed class Box<T>
    {
        public T Value = default!;
    }

}
