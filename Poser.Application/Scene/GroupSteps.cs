using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Scene;

/// <summary>
/// The group commands as undo steps. Each command records all groups before
/// and after and undo puts them back in one go. Commands that also change the
/// members, such as a gate or ungrouping, make those member changes with the
/// recording of single-value undo steps paused, so they belong to the same
/// undo step instead of adding their own.
/// </summary>
public sealed class GroupSteps
{
    private readonly SceneGroups _groups;
    private readonly EditHistory _history;
    private readonly ValueJournal _values;
    private readonly GroupTransformState? _groupTransforms;
    private readonly GroupTransformCoordinator? _groupCoordinator;
    private int _assemblyDepth;
    private readonly IGroupGateState? _gates;

    public GroupSteps(
        SceneGroups groups,
        EditHistory history,
        ValueJournal values,
        GroupTransformState? groupTransforms = null,
        GroupTransformCoordinator? groupCoordinator = null,
        IGroupGateState? gates = null)
    {
        _gates = gates;
        _groups = groups;
        _history = history;
        _values = values;
        _groupTransforms = groupTransforms;
        _groupCoordinator = groupCoordinator;
    }

    /// <summary>Runs <paramref name="act"/> as one step. Entries the act
    /// appends on its own fold into the step.</summary>
    public T Run<T>(string description, Func<T> act)
    {
        if (_assemblyDepth != 0) return act();
        var before = Capture();
        var top = _history.PeekUndo();
        T result;
        try
        {
            _assemblyDepth++;
            result = act();
        }
        finally
        {
            _assemblyDepth--;
            while (_history.PeekUndo() is { } inner && !ReferenceEquals(inner, top))
                _history.Drop(inner);
        }
        var after = CaptureFinalMembership();
        bool deferredCapture = false;
        if (!before.Equals(after))
            _history.Append(new JournalStep(
                description,
                () => Put(ref before, out deferredCapture),
                () => Put(ref after, out deferredCapture))
            {
                // A restore waiting for readable member poses must not discard
                // its preserved authored state on repeated temporary refusal.
                OnRefusal = () => deferredCapture ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
            });
        return result;
    }

    public GroupsSnapshot Capture() =>
        _groups.Capture().WithTransforms(
            _groupTransforms?.CaptureNamed()
                ?? new Dictionary<GroupTransformKey, GroupTransformSnapshot>());

    /// <summary>Reads the groups at the end of a group command, once its members
    /// exist in Poser's lists. Adding and removing entities use the same point
    /// without adding a second undo step.</summary>
    public GroupsSnapshot CaptureFinalMembership()
    {
        _groupCoordinator?.SynchronizeNamed();
        _groupTransforms?.ForgetMissingGroups(_groups.All.Select(group => group.Id));
        return Capture();
    }

    /// <summary>
    /// The groups' part of removing entities. A removed entity leaves its
    /// groups, and a group left with fewer than two members is ungrouped, so
    /// undoing the removal must put the groups back as well as the entities.
    /// This step goes first in the removal's undo step, so undo puts
    /// <paramref name="before"/> back once the entities have returned; their
    /// group memberships follow them to their new objects. Redo does nothing
    /// here: removing the entities again leaves the groups the same way.
    /// </summary>
    public JournalStep RemovalStructure(GroupsSnapshot before)
    {
        bool deferredCapture = false;
        return new JournalStep(
            "Restore groups",
            () => Put(ref before, out deferredCapture),
            () => true)
        {
            OnRefusal = () => deferredCapture ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
        };
    }

    public void Run(string description, Action act) =>
        Run(description, () => { act(); return true; });

    private bool Put(ref GroupsSnapshot snapshot, out bool deferredCapture)
    {
        deferredCapture = false;
        using (_values.Suspend())
        {
            var previous = Capture();
            Restore(snapshot);
            if (_groupCoordinator?.CompleteRestoredMembership() == false)
            {
                // Do not commit a structure whose deferred member capture is
                // still refused. No native gates have run; restore the model
                // and leave this history direction available for retry.
                Restore(previous);
                deferredCapture = true;
                return false;
            }
            // The destination snapshot may predate closing the gate and has
            // no remembered flags. Restore those from the outgoing snapshot.
            _gates?.RestoreReleased(previous);
            _gates?.Reapply();
            // Seal a deferred capture on its first successful restore. Later
            // undo/redo replays this complete snapshot, not fresh geometry.
            snapshot = Capture();
        }
        return true;
    }

    public void Restore(GroupsSnapshot snapshot)
    {
        snapshot = snapshot.Remap(_history);
        _groups.Restore(snapshot);
        if (snapshot.Transforms is not { } transforms) return;
        if (_groupCoordinator != null) _groupCoordinator.RestoreNamed(transforms);
        else _groupTransforms?.RestoreNamed(transforms);
    }

    public SceneGroup? Create(string name, IReadOnlyList<SelectionId> members, bool allowThin = false) =>
        Run("Create group", () => _groups.Create(name, members, allowThin));

    public void SetHidden(SceneGroup group, bool hidden) =>
        Run(hidden ? "Hide group" : "Show group", () => _gates?.SetHidden(group, hidden));

    public void SetPaused(SceneGroup group, bool paused) =>
        Run(paused ? "Pause group" : "Resume group", () => _gates?.SetPaused(group, paused));

    public void SetNight(SceneGroup group, bool night) =>
        Run(night ? "Group night on" : "Group night off", () => _gates?.SetNight(group, night));

    public void Dissolve(Guid id) =>
        Run("Dissolve group", () =>
        {
            if (_groups.Find(id) is { } group)
            {
                _gates?.SetHidden(group, false);
                _gates?.SetPaused(group, false);
                _gates?.SetNight(group, false);
            }
            _groups.Dissolve(id);
        });

    public void Rename(Guid id, string name) => Run("Rename group", () => _groups.Rename(id, name));

    public void AddMember(Guid groupId, SelectionId member, int index = -1) =>
        Run("Add to group", () =>
        {
            _gates?.Leave(member);
            _groups.AddMember(groupId, member, index);
            _gates?.Join(member);
        });

    public void RemoveMember(SelectionId member) => Run("Remove from group", () =>
        {
            _gates?.Leave(member);
            _groups.RemoveMember(member);
        });

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
