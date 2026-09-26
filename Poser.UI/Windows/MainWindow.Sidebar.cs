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
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The sidebar: its rows per entity kind, the flags that refresh them, and row clicks.</summary>
public partial class MainWindow
{
    private void ToggleWorldClass(int index)
    {
        if (index < 0 || index >= _worldClasses.Length)
            return;
        var (kind, entry) = _worldClasses[index];
        _worldAdoption.SetShown(kind, !_worldAdoption.IsShown(kind));
        entry.On = _worldAdoption.IsShown(kind);
    }

    private static readonly string[] CameraTrackingModeOptions =
        ["Follow", "Pan", "Follow and pan", "None"];


    /// <summary>
    /// The <c>_l</c>/<c>_r</c> counterpart the sibling-link mode co-selects,
    /// or null when the mode is off or the bone has none. Resolution never
    /// leaves the bone's own skeleton or partial: a name alone matches across
    /// slots, and pairing a character hand with a weapon bone of the same
    /// name would be a different bone entirely.
    /// </summary>
    private SelectionId? ResolveSiblingBone(SelectionId id)
    {
        if (!_configuration.Config.LinkSiblingBones ||
            id is not { Kind: SceneEntityKind.Bone, Bone: { } bone })
            return null;

        string name = bone.CanonicalName;
        string partner =
            name.EndsWith("_l", StringComparison.Ordinal)
                ? string.Concat(name.AsSpan(0, name.Length - 2), "_r")
                : name.EndsWith("_r", StringComparison.Ordinal)
                    ? string.Concat(name.AsSpan(0, name.Length - 2), "_l")
                    : string.Empty;
        if (partner.Length == 0)
            return null;

        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != bone.Skeleton.Actor.LogicalId)
                continue;
            foreach (var skeleton in actor.Skeletons)
            {
                if (skeleton.Id != bone.Skeleton)
                    continue;
                foreach (var candidate in skeleton.Bones)
                {
                    if (candidate.Id.PartialId == bone.PartialId &&
                        string.Equals(
                            candidate.Id.CanonicalName,
                            partner,
                            StringComparison.Ordinal))
                        return SelectionId.ForBone(candidate.Id);
                }
            }

            return null;
        }

        return null;
    }

    private Action? _openLibrary;

    /// <summary>
    /// One row click, then one layout resync — after every change the click
    /// makes, never between them. The strip a frame draws is a function of
    /// the selection, so the mode exits below must not restate the layout on
    /// their way out: they would resolve it against the outgoing selection
    /// (the library clears it entirely) and settle the active tab onto that
    /// strip's first tab, losing the prior tab before entering the
    /// mode — the promise <see cref="BuildTabs"/> makes for the library.
    /// </summary>
    private void OnRowClicked(ShellSidebarRow row)
    {
        ApplyRowClick(row);
        // The row was clicked while the shell is already drawing, and the tab
        // strip is a function of the selection's type: without this the rest
        // of the frame renders the incoming pane through the outgoing strip's
        // viewport contract, and draws the outgoing strip's labels with it.
        ResyncTabLayout();
    }

    private void ApplyRowClick(ShellSidebarRow row)
    {
        // A reference picture is not in the scene, so there is nothing to
        // select: the row's body raises its window instead, and shows it first
        // if the eye had set it aside — a click that focuses something the
        // hidden window would read as a no-op.
        if (row.Tag is ReferenceImageInstance clickedImage)
        {
            _referenceImages.SetHidden(clickedImage, false);
            row.LightOn = true;
            ImGui.SetWindowFocus(
                ReferenceImageWindow.WindowNameFor(clickedImage));
            return;
        }

        // Touching anything in the scene tree is leaving the library or the
        // scene workspace: they are alternatives in one workspace. A selecting
        // click leaves through the selection itself; a bare category
        // disclosure selects nothing, so the tree still states it here.
        _workspace.Leave();
        // A group row selects its whole MEMBERSHIP — the anonymous-group
        // machinery does the rest. Ctrl adds the members instead.
        if (row.Tag is GroupRowTag groupTag)
        {
            if (_groups.Find(groupTag.Id) is not { } group)
                return;
            var everything = new List<SelectionId>(_groups.Descendants(group));
            if (everything.Count == 0)
                return;
            var io2 = ImGui.GetIO();
            // A group row's members live one level down; they join a
            // multi-selection only when it already sits at that level.
            if (io2.KeyCtrl && SelectionParentIs(group.Id))
            {
                foreach (var member in everything)
                    _selection.Add(member);
            }
            else
            {
                _selection.Select(everything[0]);
                for (int i = 1; i < everything.Count; i++)
                    _selection.Add(everything[i]);
                // The HEAD click alone makes the selection "the group" —
                // hand-selecting every member stays a member selection.
                _groups.ActiveGroupId = group.Id;
            }
            return;
        }
        if (row.Tag is not SelectionId id) return;

        var io = ImGui.GetIO();
        if (row.SelectionBones is { Count: > 0 }
            && id.Kind == SceneEntityKind.Bone
            && id.Bone is null)
        {
            if (io.KeyCtrl)
            {
                foreach (var bone in row.SelectionBones)
                    _selection.Toggle(SelectionId.ForBone(bone));
            }
            else
            {
                _selection.Select(SelectionId.ForBone(row.SelectionBones[0]));
                for (int i = 1; i < row.SelectionBones.Count; i++)
                    _selection.Add(SelectionId.ForBone(row.SelectionBones[i]));
            }
            return;
        }
        // Multi-selection keeps ONE parent — the anchor's: root things
        // with root things, a group's members with each other. A shift or
        // ctrl click on another level starts over there.
        Guid? clickedParent = _groups.GroupOf(id)?.Id;
        if (io.KeyShift && _selection.Anchor is { } anchor
            && SelectionParentIs(clickedParent))
        {
            var displayOrder = new List<SelectionId>();
            foreach (var section in _vm.Sections)
                foreach (var visibleRow in section.Rows)
                    if (visibleRow.Tag is SelectionId visibleId
                        && _groups.GroupOf(visibleId)?.Id == clickedParent)
                        displayOrder.Add(visibleId);
            _selection.SelectRange(anchor, id, displayOrder);
        }
        else if (io.KeyCtrl && SelectionParentIs(clickedParent))
        {
            _selection.Toggle(id);
        }
        else
        {
            _selection.Select(id);
        }
    }

    // ── the multiselect page: the anonymous group ────────────────────────

    /// <summary>Per-kind counts, minted only when they change — a warm
    /// frame restates the same strings.</summary>
    private readonly int[] _multiCounts = new int[5];

    private readonly string[] _multiCountText = new string[5];

    private static readonly string[] MultiKindLabels =
        ["Actors", "Objects", "Lights", "Cameras", "Overlays"];
}
