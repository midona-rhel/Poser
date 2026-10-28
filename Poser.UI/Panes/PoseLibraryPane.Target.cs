using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The target actor and the apply targets.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>The apply target: the selection's actor — a bone selection
    /// resolves to the actor that owns it — as a live actor, or null when
    /// nothing resolves. Entering the library does not clear the selection, so
    /// the actor being posed is still the actor a pose lands on.</summary>
    private ActorId? TargetActor() =>
        _selection.PrimaryActor is { } id && _scene.Snapshot.FindActor(id) is not null ? id : null;

    private bool IsApplyTarget(ActorId id) =>
        _scene.Snapshot.FindActor(id) is { } actor
        && (_type == LibraryType.Mcdf || actor.CharacterSkeleton is not null);

    // Preview, options and Apply must resolve the same dropdown choice.
    private ActorId? CurrentApplyTarget() =>
        _applyChoice is { } chosen ? IsApplyTarget(chosen) ? chosen : null
        : TargetActor() is { } selected && IsApplyTarget(selected) ? selected
        : FirstApplyTarget();

    /// <summary>Updates action availability and labels using the resolved target.
    /// An explicit dropdown choice can differ from the sidebar selection.</summary>
    private void SyncTarget()
    {
        _vm.CanApply = CurrentApplyTarget() is not null;

        var host = CurrentApplyTarget();
        _files.SetHostImportTarget(
            host,
            inLibrary: true);

        // A character file is applied to an actor that already exists; there is
        // no "spawn and dress" path in v1.
        _vm.CanSpawn = _type != LibraryType.Mcdf && !IsSceneTile(_vm.Selected);

        // Loading a scene replaces the session and does not need an actor target.
        _vm.ApplyDisruptive = false;
        if (IsSceneTile(_vm.Selected))
        {
            _vm.CanApply = true;
            _vm.ApplyLabel = "Load scene";
            return;
        }

        // Object entries use one Spawn action instead of an actor-targeted Apply action.
        if (_type == LibraryType.Objects)
        {
            _vm.CanApply = true;
            _vm.ApplyLabel = "Spawn";
            return;
        }

        // Character-file imports redraw the target actor, so use the disruptive action style.
        _vm.ApplyLabel = "Apply";
        _vm.ApplyDisruptive = _type == LibraryType.Mcdf;
    }

    /// <summary>An explicit dropdown target. If it leaves the scene, keep the
    /// choice unavailable until the user chooses another actor.</summary>
    private ActorId? _applyChoice;

    public void SetApplyTarget(ActorId? actor) => _applyChoice = actor;
#if DEBUG
    public object DebugTargetState => new
    {
        chosen = _applyChoice?.ToString(),
        resolved = CurrentApplyTarget()?.ToString(),
        previewSource = PreviewSource()?.ToString(),
        selected = TargetActor()?.ToString(),
    };
#endif

    private void SyncApplyTargets()
    {
        bool shows = _type is LibraryType.Poses or LibraryType.Mcdf;
        _vm.ShowApplyTarget = shows;
        if (!shows)
            return;
        _applyTargets.Clear();
        foreach (var actor in _scene.Snapshot.Actors)
            if (_type == LibraryType.Mcdf || actor.CharacterSkeleton is not null)
                _applyTargets.Add(actor.Id);
        // Keep an explicit vanished target visible instead of silently selecting
        // someone else. Choosing a different dropdown entry is the retarget action.
        if (_applyChoice is { } explicitTarget && !_applyTargets.Contains(explicitTarget))
            _applyTargets.Add(explicitTarget);
        if (_vm.ApplyTargetNames.Length != _applyTargets.Count)
            _vm.ApplyTargetNames = new string[_applyTargets.Count];
        for (int i = 0; i < _applyTargets.Count; i++)
        {
            var actor = _applyTargets[i];
            _vm.ApplyTargetNames[i] = IsApplyTarget(actor) && _scene.Snapshot.FindActor(actor) is { } described
                ? ActorNames.Display(_config, described) : "Actor unavailable";
        }
        int index = _applyChoice != null ? _applyTargets.IndexOf(_applyChoice.Value) : -1;
        if (index < 0)
        {
            var selected = TargetActor();
            index = selected != null ? _applyTargets.IndexOf(selected.Value) : -1;
        }
        _vm.ApplyTargetIndex = index < 0 && _applyTargets.Count > 0 ? 0 : index;
    }

    private void ApplyToChosen(int index)
    {
        if (CurrentApplyTarget() is not { } actor)
        {
            _notices.Refused("No actor to apply to.");
            return;
        }
        ApplyTo(index, actor);
    }

    /// <summary>The first actor this tab's apply could land on, in scene order
    /// — the candidate the picker leads with, and the same eligibility
    /// the target dropdown lists by.</summary>
    private ActorId? FirstApplyTarget()
    {
        foreach (var candidate in _scene.Snapshot.Actors)
            if (_type == LibraryType.Mcdf || candidate.CharacterSkeleton is not null)
                return candidate.Id;
        return null;
    }

    // ── the preview ──────────────────────────────────────────────────────
}
