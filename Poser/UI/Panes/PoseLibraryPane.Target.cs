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
using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Game.Preview;
using Poser.Game.Scene;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The target actor and the apply targets.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>The apply target: the selection's actor — a bone selection
    /// resolves to the actor that owns it — as a live actor, or null when
    /// nothing resolves. Entering the library does not clear the selection, so
    /// the actor being posed is still the actor a pose lands on.</summary>
    private IActor? TargetActor()
    {
        if (_selection.PrimaryActor is not { } id)
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>The apply gates. The picker chooses the target, so applying
    /// only needs an ELIGIBLE ACTOR TO EXIST — the sidebar selection is
    /// irrelevant, and nothing here touches it (the old label-minting tail
    /// resolved the SELECTED actor and crashed the frame when the gate was
    /// true with nothing selected).</summary>
    private void SyncTarget()
    {
        _vm.CanApply = FirstApplyTarget() is not null;

        // The options rail this pane hosts resolves its commands through the
        // SCENE selection, which in library mode is routinely empty — the
        // library picks its own target. Push that target (and the fact that
        // the library is the host) every frame, the same way the preview seat
        // is pushed, so "From file", the presets and the export commands act
        // on the actor the tiles would apply to instead of silently eating
        // the click.
        _files.SetHostImportTarget(
            TargetActor() is { HasSkeleton: true } selected
                ? selected
                : FirstApplyTarget(),
            inLibrary: true);

        // A character file is applied to an actor that already exists; there is
        // no "spawn and dress" path in v1.
        _vm.CanSpawn = _type != LibraryType.Mcdf && _type != LibraryType.Scenes;

        // A scene has no target to pick: it IS the session. Its primary needs
        // a highlighted file and nothing else, and it names the transaction it
        // starts rather than the picker it does not open.
        _vm.ApplyDisruptive = false;
        if (_type == LibraryType.Scenes)
        {
            _vm.CanApply = true;
            _vm.ApplyLabel = "Load scene";
            return;
        }

        // An object entry has ONE verb — it spawns what it is. No picker,
        // no apply, no second spawn button.
        if (_type == LibraryType.Objects)
        {
            _vm.CanApply = true;
            _vm.ApplyLabel = "Spawn";
            return;
        }

        // The primary opens the actor picker; its caption is constant. A
        // character file redraws the actor, so its verb is Disruptive.
        _vm.ApplyLabel = "Apply";
        _vm.ApplyDisruptive = _type == LibraryType.Mcdf;
    }

    /// <summary>Whom a pose or character file applies to: the scene's
    /// eligible actors in a dropdown beside the verb, the selection's actor
    /// by default, a chosen one until the choice leaves the scene.</summary>
    private IActor? _applyChoice;

    private void SyncApplyTargets()
    {
        bool shows = _type is LibraryType.Poses or LibraryType.Mcdf;
        _vm.ShowApplyTarget = shows;
        if (!shows)
            return;
        _applyTargets.Clear();
        foreach (var actor in _actors.Actors)
            if (_type == LibraryType.Mcdf || actor.HasSkeleton)
                _applyTargets.Add(actor);
        if (_vm.ApplyTargetNames.Length != _applyTargets.Count)
            _vm.ApplyTargetNames = new string[_applyTargets.Count];
        for (int i = 0; i < _applyTargets.Count; i++)
        {
            var actor = _applyTargets[i];
            _vm.ApplyTargetNames[i] = _bindings.GetActorId(actor) is { } id
                ? ActorNames.Display(id, actor.Name)
                : ActorNames.Clean(actor.Name);
        }
        int index = _applyChoice != null ? _applyTargets.IndexOf(_applyChoice) : -1;
        if (index < 0)
        {
            _applyChoice = null;
            var selected = TargetActor();
            index = selected != null ? _applyTargets.IndexOf(selected) : -1;
        }
        _vm.ApplyTargetIndex = index < 0 && _applyTargets.Count > 0 ? 0 : index;
    }

    private void ApplyToChosen(int index)
    {
        if (_applyTargets.Count == 0)
        {
            _notices.Refused("No actor to apply to.");
            return;
        }
        int choice = Math.Clamp(_vm.ApplyTargetIndex, 0, _applyTargets.Count - 1);
        ApplyTo(index, _applyTargets[choice]);
    }

    /// <summary>The first actor this tab's apply could land on, in scene order
    /// — the candidate the picker leads with, and the same eligibility
    /// <see cref="DrawApplyMenu"/> lists by.</summary>
    private IActor? FirstApplyTarget()
    {
        foreach (var candidate in _actors.Actors)
            if (_type == LibraryType.Mcdf || candidate.HasSkeleton)
                return candidate;
        return null;
    }

    // ── the preview ──────────────────────────────────────────────────────
}
