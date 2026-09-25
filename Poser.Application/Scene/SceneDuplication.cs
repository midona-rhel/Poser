using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Operations;

namespace Poser.Application.Scene;

public interface ISceneDuplication
{
    SceneEntityHandle? DuplicateEntity(SelectionId id, bool withPose);
    void DuplicateAndSelect(SelectionId id, bool withPose = false);
    void DuplicateGroup(SceneGroup group, bool withPose);
    void DuplicateSelection(bool withPose);
}

public sealed class SceneDuplication(
    ISceneCreation creation, IPendingSceneCreation pending,
    SceneGroups groups, GroupSteps groupSteps, SelectionSession selection,
    ISessionGenerationSource sessions, Action<string> failure) : ISceneDuplication
{
    private readonly ISceneCreation _creation = creation;
    private readonly IPendingSceneCreation _pending = pending;
    private readonly SceneGroups _groups = groups;
    private readonly GroupSteps _groupSteps = groupSteps;
    private readonly SelectionSession _selection = selection;
    private readonly ISessionGenerationSource _sessions = sessions;
    private readonly Action<string> _failure = failure;

    public void DuplicateSelection(bool withPose)
    {
        if (_groups.ActiveSelection(_selection.Selected) is { } group)
            DuplicateGroup(group, withPose);
        else
            foreach (var id in _selection.Selected.ToArray())
                DuplicateEntity(id, withPose);
    }

    public void DuplicateAndSelect(SelectionId id, bool withPose = false)
    {
        if (DuplicateEntity(id, withPose) is { } handle)
            _pending.SelectWhenReady(handle);
    }

    /// <summary>One entity's copy receipt, or null when
    /// the kind has none or the copy failed.</summary>
    public SceneEntityHandle? DuplicateEntity(SelectionId id, bool withPose)
    {
        var result = _creation.Duplicate(id, withPose);
        if (result.Handle is null) _failure(result.Detail ?? "The entity could not be duplicated.");
        return result.Handle;
    }

    // ── duplicating groups ───────────────────────────────────────────────
    // The copies spawn at once; their bindings land on the scene's own
    // refresh, so the group is assembled from the pump once every copy
    // has an id (or patience runs out and what did bind is grouped).

    private sealed class GroupCopy
    {
        public string Name = "";
        public SessionGeneration Session;
        public bool Hidden, Paused, Night;
        public readonly List<SceneEntityHandle> Members = new();
        public readonly List<GroupCopy> Children = new();
        public Guid? Parent;
        public int Index = -1;
        public RootSlot? Anchor;
        public int Frames;
    }

    private readonly List<GroupCopy> _groupCopies = new();

    private const int GroupCopyPatience = 120;

    /// <summary>Copies the group and everything beneath it into a new
    /// group with the next name in its series, seated right after the original at the
    /// same level, gates and all.</summary>
    public void DuplicateGroup(SceneGroup group, bool withPose)
    {
        if (_sessions.ActiveSessionGeneration is not { } session) return;
        var copy = CopyGroupTree(group, withPose);
        copy.Session = session;
        copy.Parent = group.ParentId;
        if (group.ParentId is { } parentId && _groups.Find(parentId) is { } parent)
            copy.Index = parent.Children.IndexOf(group.Id) + 1;
        else
            copy.Anchor = RootSlot.ForGroup(group.Id);
        _groupCopies.Add(copy);
    }

    private GroupCopy CopyGroupTree(SceneGroup group, bool withPose)
    {
        var copy = new GroupCopy
        {
            Name = group.Name,
            Hidden = group.Hidden,
            Paused = group.Paused,
            Night = group.Night,
        };
        foreach (var member in group.Members)
            if (DuplicateEntity(member, withPose) is { } made)
                copy.Members.Add(made);
        foreach (var childId in group.Children)
            if (_groups.Find(childId) is { } child)
                copy.Children.Add(CopyGroupTree(child, withPose));
        return copy;
    }

    public void Tick()
    {
        for (int i = _groupCopies.Count - 1; i >= 0; i--)
        {
            var copy = _groupCopies[i];
            if (_sessions.ActiveSessionGeneration != copy.Session)
            {
                _groupCopies.RemoveAt(i);
                continue;
            }
            if (!CopyBound(copy) && ++copy.Frames < GroupCopyPatience)
                continue;
            _groupCopies.RemoveAt(i);
            var made = _groupSteps.Run("Duplicate group", () =>
            {
                var result = RealizeGroupCopy(copy);
                if (result == null) return null;
                if (copy.Parent is { } parentId && _groups.Find(parentId) != null)
                    _groupSteps.Nest(result.Id, parentId, copy.Index);
                else if (copy.Anchor is { } anchor)
                    _groupSteps.MoveRoot(
                        RootSlot.ForGroup(result.Id), anchor, after: true);
                return result;
            });
            if (made == null)
            {
                _failure($"'{copy.Name}' could not be duplicated: nothing in it copied.");
                continue;
            }
        }
    }

    private bool CopyBound(GroupCopy copy)
    {
        foreach (var member in copy.Members)
            if (ResolveCopy(member) == null)
                return false;
        foreach (var child in copy.Children)
            if (!CopyBound(child))
                return false;
        return true;
    }

    private SceneGroup? RealizeGroupCopy(GroupCopy copy)
    {
        var ids = new List<SelectionId>();
        foreach (var member in copy.Members)
            if (ResolveCopy(member) is { } id)
                ids.Add(id);
        var children = new List<SceneGroup>();
        foreach (var child in copy.Children)
            if (RealizeGroupCopy(child) is { } made)
                children.Add(made);
        if (ids.Count + children.Count == 0)
            return null;
        var group = _groupSteps.Create(
            EntityNames.Next(copy.Name, _groups.All.Select(x => x.Name)), ids, allowThin: true);
        if (group == null)
            return null;
        foreach (var child in children)
            _groupSteps.Nest(child.Id, group.Id);
        if (copy.Hidden)
            _groupSteps.SetHidden(group, true);
        if (copy.Paused)
            _groupSteps.SetPaused(group, true);
        if (copy.Night)
            _groupSteps.SetNight(group, true);
        return group;
    }

    private SelectionId? ResolveCopy(SceneEntityHandle receipt) => _creation.Resolve(receipt);


}
