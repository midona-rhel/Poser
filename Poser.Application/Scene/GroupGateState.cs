using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.Application.Scene;

public interface IGroupGateState
{
    void Reapply();
    void RestoreReleased(GroupsSnapshot previous);
    void SetHidden(SceneGroup group, bool hidden);
    void SetPaused(SceneGroup group, bool paused);
    void SetNight(SceneGroup group, bool night);
    void Join(SelectionId member);
    void Leave(SelectionId member);
}

/// <summary>Member-state overrides and restoration; GroupSteps owns their history transaction.</summary>
public sealed class GroupGateState(
    SceneGroups groups, SelectionEntityCommands entities, IScenePlaybackControl playback) : IGroupGateState
{
    private readonly SceneGroups _groups = groups;
    private readonly SelectionEntityCommands _entities = entities;
    private readonly IScenePlaybackControl _playback = playback;
    /// <summary>Makes the world match every group's gates, after the model
    /// was put back: the journal's way to undo a gate or a dissolve.</summary>
    public void Reapply()
    {
        foreach (var group in _groups.All)
        {
            SetGate(group, group.Hidden, group.RememberedVisible, g => g.Hidden,
                _entities.ReadVisibility, (id, visible) => SetVisible(id, visible),
                imposed: false);
            SetGate(group, group.Paused, group.RememberedPlaying, g => g.Paused,
                _playback.ReadPlaying, _playback.SetPlaying, imposed: false);
            SetGate(group, group.Night, group.RememberedNight, g => g.Night,
                _playback.ReadNight, _playback.SetNight, imposed: true);
        }
    }

    public void RestoreReleased(GroupsSnapshot previous)
    {
        foreach (var group in previous.Groups)
        {
            Restore(group.RememberedVisible, g => g.Hidden,
                (id, visible) => SetVisible(id, visible));
            Restore(group.RememberedPlaying, g => g.Paused, _playback.SetPlaying);
            Restore(group.RememberedNight, g => g.Night, _playback.SetNight);
        }

        void Restore(IReadOnlyDictionary<SelectionId, bool> remembered,
            Func<SceneGroup, bool> closed, Action<SelectionId, bool> write)
        {
            foreach (var (member, value) in remembered)
                if (!UnderClosedGate(member, closed))
                    write(member, value);
        }
    }

    private bool UnderClosedGate(SelectionId member, Func<SceneGroup, bool> closed)
    {
        if (_groups.GroupOf(member) is not { } own)
            return false;
        if (closed(own))
            return true;
        foreach (var ancestor in _groups.Ancestors(own))
            if (closed(ancestor))
                return true;
        return false;
    }

    /// <summary>One gate's mechanics, shared by the three: closing reads
    /// and remembers each member's own state and imposes the gate's;
    /// opening gives the remembered state back to every member no other
    /// closed gate still covers.</summary>
    private void SetGate(
        SceneGroup group,
        bool close,
        Dictionary<SelectionId, bool> remembered,
        Func<SceneGroup, bool> closedOn,
        Func<SelectionId, bool?> read,
        Action<SelectionId, bool> write,
        bool imposed)
    {
        if (close)
        {
            foreach (var member in _groups.Descendants(group))
            {
                if (read(member) is not { } own)
                    continue;
                if (!remembered.ContainsKey(member))
                    remembered[member] = own;
                write(member, imposed);
            }
        }
        else
        {
            foreach (var (member, own) in remembered)
                if (!UnderClosedGate(member, closedOn))
                    write(member, own);
            remembered.Clear();
        }
        _groups.Touch();
    }

    public void SetHidden(SceneGroup group, bool hidden)
    {
        if (group.Hidden == hidden)
            return;
        group.Hidden = hidden;
        SetGate(group, hidden, group.RememberedVisible, g => g.Hidden,
            _entities.ReadVisibility, (id, visible) => SetVisible(id, visible),
            imposed: false);
    }

    public void SetPaused(SceneGroup group, bool paused)
    {
        if (group.Paused == paused)
            return;
        group.Paused = paused;
        SetGate(group, paused, group.RememberedPlaying, g => g.Paused,
            _playback.ReadPlaying, _playback.SetPlaying, imposed: false);
    }

    public void SetNight(SceneGroup group, bool night)
    {
        if (group.Night == night)
            return;
        group.Night = night;
        SetGate(group, night, group.RememberedNight, g => g.Night,
            _playback.ReadNight, _playback.SetNight, imposed: true);
    }

    /// <summary>A member joining under closed gates takes each gate's
    /// state from the outermost closed group, which remembers its own.</summary>
    public void Join(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } home)
            return;
        var chain = new List<SceneGroup> { home };
        chain.AddRange(_groups.Ancestors(home));
        SceneGroup? hiding = null, pausing = null, benighting = null;
        foreach (var group in chain)
        {
            if (group.Hidden)
                hiding = group;
            if (group.Paused)
                pausing = group;
            if (group.Night)
                benighting = group;
        }
        if (hiding != null && _entities.ReadVisibility(member) is { } visible)
        {
            hiding.RememberedVisible[member] = visible;
            SetVisible(member, false);
        }
        if (pausing != null && _playback.ReadPlaying(member) is { } playing)
        {
            pausing.RememberedPlaying[member] = playing;
            _playback.SetPlaying(member, false);
        }
        if (benighting != null && _playback.ReadNight(member) is { } night)
        {
            benighting.RememberedNight[member] = night;
            _playback.SetNight(member, true);
        }
    }

    /// <summary>A member leaving its group gets its own state back from
    /// whichever group remembered it.</summary>
    public void Leave(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } own)
            return;
        var chain = new List<SceneGroup> { own };
        chain.AddRange(_groups.Ancestors(own));
        foreach (var group in chain)
        {
            if (group.RememberedVisible.Remove(member, out var visible))
                SetVisible(member, visible);
            if (group.RememberedPlaying.Remove(member, out var playing))
                _playback.SetPlaying(member, playing);
            if (group.RememberedNight.Remove(member, out var night))
                _playback.SetNight(member, night);
        }
    }
    private void SetVisible(SelectionId id, bool visible) => _entities.SetVisibility([id], visible);
}
