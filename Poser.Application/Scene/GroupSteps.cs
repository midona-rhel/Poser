using Poser.Application.Transforms;
using Poser.Domain.Identity;

namespace Poser.Application.Scene;

/// <summary>
/// The group verbs as journal steps. Every verb records the whole group
/// model before and after and puts it back as one; the composite verbs
/// (a gate, a dissolve) re-run the surface's own routine under a
/// suspended value journal, so the member changes they cause are part of
/// the one step and not steps of their own.
/// </summary>
public sealed class GroupSteps
{
    private readonly SceneGroups _groups;
    private readonly TransformHistory _history;
    private readonly ValueJournal _values;

    public GroupSteps(SceneGroups groups, TransformHistory history, ValueJournal values)
    {
        _groups = groups;
        _history = history;
        _values = values;
    }

    /// <summary>The routine that makes the world match every group's gates
    /// after the model is put back — the shell owns the member verbs, so it
    /// hands the routine in.</summary>
    public Action? ReapplyGates { get; set; }

    /// <summary>Runs <paramref name="act"/> as one step. Entries the act
    /// appends on its own fold into the step.</summary>
    public T Run<T>(string description, Func<T> act)
    {
        var before = _groups.Capture();
        var top = _history.PeekUndo();
        T result;
        try
        {
            result = act();
        }
        finally
        {
            while (_history.PeekUndo() is { } inner && !ReferenceEquals(inner, top))
                _history.Drop(inner);
        }
        var after = _groups.Capture();
        if (!before.Equals(after))
            _history.Append(new JournalStep(
                description,
                () => Put(before),
                () => Put(after)));
        return result;
    }

    public void Run(string description, Action act) =>
        Run(description, () => { act(); return true; });

    private bool Put(GroupsSnapshot snapshot)
    {
        using (_values.Suspend())
        {
            _groups.Restore(snapshot);
            ReapplyGates?.Invoke();
        }
        return true;
    }

    public SceneGroup? Create(string name, IReadOnlyList<SelectionId> members, bool allowThin = false) =>
        Run("Create group", () => _groups.Create(name, members, allowThin));

    public void Rename(Guid id, string name) => Run("Rename group", () => _groups.Rename(id, name));

    public void AddMember(Guid groupId, SelectionId member, int index = -1) =>
        Run("Add to group", () => _groups.AddMember(groupId, member, index));

    public void RemoveMember(SelectionId member) => Run("Remove from group", () => _groups.RemoveMember(member));

    public bool Nest(Guid childId, Guid parentId, int index = -1) =>
        Run("Nest group", () => _groups.Nest(childId, parentId, index));

    public void Unnest(Guid groupId, RootSlot? anchor = null, bool after = true) =>
        Run("Unnest group", () => _groups.Unnest(groupId, anchor, after));

    public void SetLocked(Guid id, bool locked) =>
        Run(locked ? "Lock group" : "Unlock group", () => _groups.SetLocked(id, locked));

    public void MoveRoot(RootSlot moved, RootSlot target, bool after) =>
        Run("Reorder", () => _groups.MoveRoot(moved, target, after));

    public void MoveRootToEnd(RootSlot moved) => Run("Reorder", () => _groups.MoveRootToEnd(moved));

    public void RestoreOrder(IReadOnlyList<RootSlot> slots) => Run("Reorder", () => _groups.RestoreOrder(slots));
}
