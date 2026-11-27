using Poser.Domain;
using Poser.Domain.Identity;
using System.Runtime.CompilerServices;

namespace Poser.Application.Transforms;

/// <summary>Wraps a write that cannot say it was refused, for the environment,
/// animation, IK and parenting code whose setters return nothing.</summary>
public static class ValueWrites
{
    public static Func<T, Outcome> Unchecked<T>(Action<T> write) =>
        value => { write(value); return Outcome.Ok(); };
}

/// <summary>
/// Records value changes as undo steps. Setting a value reads the old one,
/// writes the new one and records a step whose undo writes the old one back.
/// While a slider or field is being dragged, the values before and after are
/// kept until <see cref="Seal"/> records them as one step; other changes are
/// recorded at once, even on the same property. Undoing a step whose thing is
/// gone does nothing, so the steps under it can still be undone.
/// </summary>
public sealed class ValueJournal
{
    private readonly EditHistory _history;
    private readonly Func<object, SelectionId?>? _identify;
    private PendingEdit? _pending;
    private object? _control;
    private int _editing;
    private readonly Dictionary<object, StagedValue> _staged = new();
    private int _suspended;

    /// <summary>
    /// While held, values are still written but no step is recorded: undoing a
    /// step made of several changes runs the same code that made them, and that
    /// code must not record new steps during the undo.
    /// </summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Resume(this);
    }

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

    /// <summary>A slider or field is being dragged: its value changes, and the
    /// step is recorded when the drag ends.</summary>
    public bool IsStaging => _pending != null || _staged.Count > 0 || _control != null;

    public ValueJournal(EditHistory history, Func<object, SelectionId?>? identify = null)
    {
        _history = history;
        _identify = identify;
        // Adding a thing, a bake or a move records its own step. Value changes
        // made before it are recorded first, so undo keeps their order.
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
    /// Changes with the same key during one drag of a control are one undo
    /// step that starts from the value before the drag.</param>
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
    /// Records a change that has already been made, so only a change that
    /// worked becomes a step. A key that names a thing makes the step part of
    /// that thing's own undo; any other key puts it only in the global undo.
    /// While a control is dragged, the first before value and the latest after
    /// value are kept; otherwise one step is recorded.
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

    // Only a refusal that may pass later keeps the step for another try. A
    // lasting refusal or an exception is reported, and the step is dropped
    // when it fails again, so a step that can never be undone does not block
    // the steps under it.
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

    /// <summary>Records the value change in progress as one undo step, so the
    /// next change starts a step of its own.</summary>
    public void Seal()
    {
        // Between BeginEdit and EndEdit a control is applying one change,
        // which must not be cut in two; its step ends at CommitEdit.
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
        return entity is { } found ? new[] { EditHistory.EntityOf(found) } : null;
    }

    private sealed record PendingEdit(object Key, Action<object> SetAfter, Action Commit);
    private sealed record StagedValue(Action<object?> SetAfter, Func<JournalStep?> Build);

    private sealed class Box<T>
    {
        public T Value = default!;
    }

}
