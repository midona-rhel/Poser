using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Game.Scene;

/// <summary>
/// Owns one entity family's identity slots. Every history direction uses the
/// same slot, captures through the family's removal path, restores through its
/// family port, and rebinds the slot to the current instance.
/// </summary>
internal sealed class LifecycleSlotOwner<TInstance, TSlot>
    where TInstance : class
    where TSlot : class
{
    private readonly Dictionary<TInstance, TSlot> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly Func<TInstance, TSlot> _create;
    private readonly Func<TSlot, TInstance?> _current;
    private readonly Action<TSlot, TInstance?> _setCurrent;
    private readonly Func<TSlot, bool> _captureAndRemove;
    private readonly Func<TSlot, bool> _restore;
    private readonly bool _retainAliases;
    private readonly TransformHistory? _history;
    private readonly Func<TInstance, TransformTargetId?>? _transformTarget;

    public LifecycleSlotOwner(
        Func<TInstance, TSlot> create,
        Func<TSlot, TInstance?> current,
        Action<TSlot, TInstance?> setCurrent,
        Func<TSlot, bool> captureAndRemove,
        Func<TSlot, bool> restore,
        bool retainAliases = false,
        TransformHistory? history = null,
        Func<TInstance, TransformTargetId?>? transformTarget = null)
    {
        _create = create;
        _current = current;
        _setCurrent = setCurrent;
        _captureAndRemove = captureAndRemove;
        _restore = restore;
        _retainAliases = retainAliases;
        _history = history;
        _transformTarget = transformTarget;
    }

    public int Count => _slots.Count;

    public TSlot SlotFor(TInstance instance)
    {
        if (_slots.TryGetValue(instance, out var slot))
            return slot;
        slot = _create(instance);
        _slots.Add(instance, slot);
        return slot;
    }

    public bool TryGetSlot(TInstance instance, out TSlot slot) =>
        _slots.TryGetValue(instance, out slot!);

    public TInstance? CurrentInstance(TSlot slot) => _current(slot);

    public TInstance? Resolve(TInstance original) =>
        _slots.TryGetValue(original, out var slot) ? _current(slot) : original;

    public void BindReplacement(TInstance original, TInstance replacement)
    {
        if (_slots.TryGetValue(original, out var slot))
            BindCurrent(slot, replacement);
    }

    public bool CaptureAndRemove(TSlot slot)
    {
        var previous = _current(slot);
        // Removal can publish a scene refresh immediately. Preserve the old
        // target before that refresh reconciles history against live bindings.
        if (previous is not null && _transformTarget?.Invoke(previous) is { } target)
            _history?.RetainLifecycleTarget(target, () =>
                _current(slot) is { } live ? _transformTarget(live) : null);
        if (!_captureAndRemove(slot))
            return false;
        if (previous is not null && !_retainAliases)
            _slots.Remove(previous);
        _setCurrent(slot, null);
        return true;
    }

    public bool Restore(TSlot slot)
    {
        if (!_restore(slot))
            return false;
        if (_current(slot) is { } current)
            BindCurrent(slot, current);
        return true;
    }

    public void BindCurrent(TSlot slot, TInstance instance)
    {
        if (!_retainAliases && _current(slot) is { } previous)
            _slots.Remove(previous);
        _setCurrent(slot, instance);
        _slots[instance] = slot;
    }

    public void ForgetCurrent(TSlot slot)
    {
        if (!_retainAliases && _current(slot) is { } current)
            _slots.Remove(current);
        _setCurrent(slot, null);
    }

    public void Clear() => _slots.Clear();
}
