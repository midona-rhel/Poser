using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Transforms;
using Poser.Domain.Companions;
using Poser.Game.Posing;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Group gates and overrides.</summary>
public partial class MainWindow
{
    /// <summary>Ungrouping opens every gate first so each member gets its
    /// own state back.</summary>
    private void DissolveGroup(Guid id) =>
        _groupSteps.Run("Dissolve group", () =>
        {
            if (_groups.Find(id) is { } group)
            {
                SetGroupHiddenCore(group, false);
                SetGroupPausedCore(group, false);
                SetGroupNightCore(group, false);
            }
            _groups.Dissolve(id);
        });

    /// <summary>Makes the world match every group's gates, after the model
    /// was put back: the journal's way to undo a gate or a dissolve.</summary>
    private void ReapplyGroupGates()
    {
        foreach (var group in _groups.All)
        {
            SetGate(group, group.Hidden, group.RememberedVisible, g => g.Hidden,
                IsEntityVisible, SetEntityVisible, imposed: false);
            SetGate(group, group.Paused, group.RememberedPlaying, g => g.Paused,
                PlayingOf, SetPlaying, imposed: false);
            SetGate(group, group.Night, group.RememberedNight, g => g.Night,
                NightOf, SetNight, imposed: true);
        }
    }

    private void SetGroupHidden(global::Poser.Application.Scene.SceneGroup group, bool hidden) =>
        _groupSteps.Run(hidden ? "Hide group" : "Show group", () => SetGroupHiddenCore(group, hidden));

    private void SetGroupPaused(global::Poser.Application.Scene.SceneGroup group, bool paused) =>
        _groupSteps.Run(paused ? "Pause group" : "Resume group", () => SetGroupPausedCore(group, paused));

    private void SetGroupNight(global::Poser.Application.Scene.SceneGroup group, bool night) =>
        _groupSteps.Run(night ? "Group night on" : "Group night off", () => SetGroupNightCore(group, night));

    private bool UnderClosedGate(SelectionId member, Func<global::Poser.Application.Scene.SceneGroup, bool> closed)
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
        global::Poser.Application.Scene.SceneGroup group,
        bool close,
        Dictionary<SelectionId, bool> remembered,
        Func<global::Poser.Application.Scene.SceneGroup, bool> closedOn,
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

    private void SetGroupHiddenCore(global::Poser.Application.Scene.SceneGroup group, bool hidden)
    {
        if (group.Hidden == hidden)
            return;
        group.Hidden = hidden;
        SetGate(group, hidden, group.RememberedVisible, g => g.Hidden,
            IsEntityVisible, SetEntityVisible, imposed: false);
    }

    private void SetGroupPausedCore(global::Poser.Application.Scene.SceneGroup group, bool paused)
    {
        if (group.Paused == paused)
            return;
        group.Paused = paused;
        SetGate(group, paused, group.RememberedPlaying, g => g.Paused,
            PlayingOf, SetPlaying, imposed: false);
    }

    private void SetGroupNightCore(global::Poser.Application.Scene.SceneGroup group, bool night)
    {
        if (group.Night == night)
            return;
        group.Night = night;
        SetGate(group, night, group.RememberedNight, g => g.Night,
            NightOf, SetNight, imposed: true);
    }

    /// <summary>A member joining under closed gates takes each gate's
    /// state from the outermost closed group, which remembers its own.</summary>
    private void JoinGroupOverrides(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } home)
            return;
        var chain = new List<global::Poser.Application.Scene.SceneGroup> { home };
        chain.AddRange(_groups.Ancestors(home));
        global::Poser.Application.Scene.SceneGroup? hiding = null, pausing = null, benighting = null;
        foreach (var group in chain)
        {
            if (group.Hidden)
                hiding = group;
            if (group.Paused)
                pausing = group;
            if (group.Night)
                benighting = group;
        }
        if (hiding != null && IsEntityVisible(member) is { } visible)
        {
            hiding.RememberedVisible[member] = visible;
            SetEntityVisible(member, false);
        }
        if (pausing != null && PlayingOf(member) is { } playing)
        {
            pausing.RememberedPlaying[member] = playing;
            SetPlaying(member, false);
        }
        if (benighting != null && NightOf(member) is { } night)
        {
            benighting.RememberedNight[member] = night;
            SetNight(member, true);
        }
    }

    /// <summary>A member leaving its group gets its own state back from
    /// whichever group remembered it.</summary>
    private void LeaveGroupOverrides(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } own)
            return;
        var chain = new List<global::Poser.Application.Scene.SceneGroup> { own };
        chain.AddRange(_groups.Ancestors(own));
        foreach (var group in chain)
        {
            if (group.RememberedVisible.Remove(member, out var visible))
                SetEntityVisible(member, visible);
            if (group.RememberedPlaying.Remove(member, out var playing))
                SetPlaying(member, playing);
            if (group.RememberedNight.Remove(member, out var night))
                SetNight(member, night);
        }
    }
}
