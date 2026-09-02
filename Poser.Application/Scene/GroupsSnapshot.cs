using Poser.Domain.Identity;

namespace Poser.Application.Scene;

/// <summary>One group, frozen: everything a restore needs to rebuild it
/// with the same id.</summary>
public sealed record GroupRecord(
    Guid Id,
    string Name,
    IReadOnlyList<SelectionId> Members,
    IReadOnlyList<Guid> Children,
    Guid? ParentId,
    bool Locked,
    bool Hidden,
    bool Paused,
    bool Night,
    IReadOnlyDictionary<SelectionId, bool> RememberedVisible,
    IReadOnlyDictionary<SelectionId, bool> RememberedPlaying,
    IReadOnlyDictionary<SelectionId, bool> RememberedNight);

/// <summary>The whole group model at one moment: every group and the root
/// order. Two snapshots are equal when they describe the same model.</summary>
public sealed class GroupsSnapshot : IEquatable<GroupsSnapshot>
{
    public GroupsSnapshot(
        IReadOnlyList<GroupRecord> groups,
        IReadOnlyList<RootSlot> order,
        Guid? activeGroupId)
    {
        Groups = groups;
        Order = order;
        ActiveGroupId = activeGroupId;
    }

    public IReadOnlyList<GroupRecord> Groups { get; }
    public IReadOnlyList<RootSlot> Order { get; }
    public Guid? ActiveGroupId { get; }

    public bool Equals(GroupsSnapshot? other) =>
        other is not null
        && Order.SequenceEqual(other.Order)
        && Groups.Count == other.Groups.Count
        && Groups.Zip(other.Groups).All(pair => Same(pair.First, pair.Second));

    private static bool Same(GroupRecord a, GroupRecord b) =>
        a.Id == b.Id
        && a.Name == b.Name
        && a.ParentId == b.ParentId
        && a.Locked == b.Locked && a.Hidden == b.Hidden
        && a.Paused == b.Paused && a.Night == b.Night
        && a.Members.SequenceEqual(b.Members)
        && a.Children.SequenceEqual(b.Children)
        && SameMap(a.RememberedVisible, b.RememberedVisible)
        && SameMap(a.RememberedPlaying, b.RememberedPlaying)
        && SameMap(a.RememberedNight, b.RememberedNight);

    private static bool SameMap(IReadOnlyDictionary<SelectionId, bool> a, IReadOnlyDictionary<SelectionId, bool> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var v) && v == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as GroupsSnapshot);

    public override int GetHashCode() => Groups.Count ^ Order.Count;
}
