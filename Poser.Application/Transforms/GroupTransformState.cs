using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

public readonly record struct GroupTransformKey(Guid? NamedGroup, string Membership)
{
    public static GroupTransformKey For(Guid? named, IEnumerable<TransformTargetId> targets) =>
        new(named, string.Join("|", targets.Select(target =>
            $"{target.Kind}:{GroupTransformIdentity.LogicalId(target):N}").Order(StringComparer.Ordinal)));
}

public sealed record GroupTransformHistoryChange(
    GroupTransformKey Key, GroupTransformSnapshot Before, GroupTransformSnapshot After)
{
    public GroupTransformHistoryChange? Remap(Func<TransformTargetId, TransformTargetId?> resolve)
    {
        var before = Before.Remap(resolve);
        var after = After.Remap(resolve);
        return before != null && after != null
            ? new(GroupTransformKey.For(Key.NamedGroup, before.Expected.Keys), before, after) : null;
    }
}

/// <summary>Session-owned records. Only explicit creation, selection,
/// membership, binding and transaction boundaries write this store.</summary>
public sealed class GroupTransformState
{
    public const int AnonymousCapacity = 128;
    private readonly Dictionary<GroupTransformKey, GroupTransformSnapshot> _records = new();
    private readonly LinkedList<GroupTransformKey> _anonymousOrder = new();

    public GroupTransformSnapshot? Snapshot(Guid? named, IEnumerable<TransformTargetId> targets) =>
        _records.GetValueOrDefault(GroupTransformKey.For(named, targets));
    public GroupTransformSnapshot? NamedSnapshot(Guid id) =>
        _records.FirstOrDefault(pair => pair.Key.NamedGroup == id).Value;
    public bool IsCurrent(GroupTransformKey key, GroupTransformSnapshot frozen) =>
        _records.TryGetValue(key, out var current) && ReferenceEquals(current, frozen);

    public bool Initialize(Guid? named, IReadOnlyDictionary<TransformTargetId, PoseTransform> members,
        GroupTransformFrame frame, out string? error)
    {
        if (!GroupTransformBaseline.TryCapture(members, frame, out var baseline, out error)) return false;
        Put(GroupTransformKey.For(named, members.Keys), new(baseline!, members,
            GroupTransformControls.Identity(baseline!.InitialCentroid)));
        return true;
    }
    public void Put(GroupTransformKey key, GroupTransformSnapshot state)
    {
        if (!state.IsValid || key != GroupTransformKey.For(key.NamedGroup, state.Expected.Keys))
            throw new ArgumentException("Invalid group transform record.", nameof(state));
        if (key.NamedGroup is { } id) Forget(id);
        else
        {
            _anonymousOrder.Remove(key);
            _anonymousOrder.AddLast(key);
            while (_anonymousOrder.Count > AnonymousCapacity)
            {
                _records.Remove(_anonymousOrder.First!.Value);
                _anonymousOrder.RemoveFirst();
            }
        }
        _records[key] = state;
    }
    public bool TryRead(Guid? named, IReadOnlyList<TransformTargetId> targets,
        Func<TransformTargetId, PoseTransform?> capture, GroupScaleMode mode,
        out GroupTransformDisplay display, out string? error)
    {
        display = default;
        if (Snapshot(named, targets) is not { } state)
        { error = "The group transform frame is not initialized."; return false; }
        var current = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var target in targets)
        {
            if (capture(target) is not { } value)
            { error = $"Group member {target} is unavailable."; return false; }
            current[target] = value;
        }
        return GroupTransformReadModel.TryRead(state, current, mode, out display, out error);
    }
    public void Restore(GroupTransformHistoryChange change, bool before) =>
        Put(change.Key, before ? change.Before : change.After);

    public IReadOnlyDictionary<GroupTransformKey, GroupTransformSnapshot> CaptureNamed() =>
        _records.Where(pair => pair.Key.NamedGroup != null).ToDictionary(pair => pair.Key, pair => pair.Value);
    public void RestoreNamed(IReadOnlyDictionary<GroupTransformKey, GroupTransformSnapshot> records)
    {
        foreach (var key in _records.Keys.Where(key => key.NamedGroup != null).ToArray()) _records.Remove(key);
        foreach (var (key, state) in records) Put(key, state);
    }
    public void Rekey(Func<TransformTargetId, TransformTargetId?> resolve)
    {
        foreach (var (key, value) in _records.ToArray())
        {
            // Avoid replacing an immutable record during an unrelated refresh.
            if (value.Expected.Keys.All(target => resolve(target) == target)) continue;
            if (value.Remap(resolve) is { } mapped) _records[key] = mapped;
            else { _records.Remove(key); _anonymousOrder.Remove(key); }
        }
    }
    public void Forget(Guid id)
    {
        foreach (var key in _records.Keys.Where(key => key.NamedGroup == id).ToArray()) _records.Remove(key);
    }
    public void ForgetMissingGroups(IEnumerable<Guid> ids)
    {
        var present = ids.ToHashSet();
        foreach (var key in _records.Keys.Where(key => key.NamedGroup is { } id && !present.Contains(id)).ToArray())
            _records.Remove(key);
    }
    public void Clear() { _records.Clear(); _anonymousOrder.Clear(); }
}
