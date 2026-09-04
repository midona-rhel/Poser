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
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The context menus per entity kind.</summary>
public partial class MainWindow
{
    /// <summary>The selected actor's skeleton, or null when nothing posable is
    /// selected or its binding no longer resolves.</summary>
    private ISkeleton? SelectedSkeleton()
    {
        if (SelectedActorId() is not { } actorId)
            return null;
        var resolved = _bindings.Resolve(actorId);
        return resolved.Success ? resolved.Value?.Skeleton : null;
    }

    /// <summary>Right-click actor menu: the lifetime actions that were stranded
    /// without a sidebar affordance (target / visibility / rename / clone / companion / despawn).
    /// The menu state is a stable ActorId; the legacy lifetime services still
    /// take live actors, so the id resolves through the binding registry for
    /// the duration of one frame and is dropped when resolution fails.</summary>
    private void DrawActorContextMenu()
    {
        if (_ctxActorId is not { } actorId)
            return;
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success)
        {
            _ctxActorId = null;
            Crystarium.FloatingMenu.Dismiss("##actor-ctx");
            return;
        }
        var actor = resolved.Value!;
        var descriptor = ResolveActorDescriptor(actorId);
        bool attached = descriptor?.OwnerActor is not null;
        var attachment = _companions.ActionsFor(actorId);

        var items = new List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Crosshair),
            new("Center camera on actor", TablerIcon.Crosshair),
            new(!_spawnService.IsVisible(actor) ? "Show" : "Hide", !_spawnService.IsVisible(actor) ? TablerIcon.Eye : TablerIcon.EyeOff),
            // The icon carries the verb the row performs: resume wears play,
            // pause wears pause.
            new(!_animation.AnyPlaying(actorId) ? "Play" : "Pause",
                !_animation.AnyPlaying(actorId)
                    ? TablerIcon.PlayerPlay
                    : TablerIcon.PlayerPause,
                disabled: !_animation.IsSupported(actorId)),
            new("Rename", TablerIcon.Edit),
        };
        var actions = new List<Action?>
        {
            () => _actorManager.SetGPoseTarget(actor),
            () => _cameraPane.CenterOnActor(actorId),
            () =>
            {
                var changed = _sessions.Actors.SetVisibility(
                    actor, !_spawnService.IsVisible(actor));
                if (!changed.Success)
                    _notices.Refused(
                        "Visibility",
                        changed.Detail ?? "The change was refused.");
            },
            () =>
            {
                if (_animation.AnyPlaying(actorId))
                    _animation.Pause(actorId);
                else
                    _animation.Resume(actorId);
            },
            // Seeds what the UI shows — nickname, else the mask while
            // anonymous mode is on. Prefilling the raw name would leak it.
            () => _names.Open(
                "Rename actor",
                ActorNames.Display(actorId, actor.Name),
                name => Config.ConfigurationService.Instance.SetNickname(
                    actorId.LogicalId, name),
                clear: () => Config.ConfigurationService.Instance.SetNickname(
                    actorId.LogicalId, null),
                clearHelp: "Remove the nickname and show the real name"),
        };

        // Attached bodies own pose/transform/presentation state, but their
        // lifetime remains their owner's slot. They therefore have neither
        // clone/save nor direct destroy verbs.
        if (!attached)
        {
            bool standaloneCreature = descriptor is { IsCompanion: true };
            items.Add(new ContextMenuItem("Duplicate", TablerIcon.Copy,
                disabled: standaloneCreature,
                submenuItems: standaloneCreature
                    ? null
                    : DuplicateSubmenu(actor.HasSkeleton)));
            actions.Add(null); // Duplicate — child clicks are read separately.
            items.Add(new ContextMenuItem("Save to library", TablerIcon.Library,
                disabled: !actor.HasSkeleton));
            actions.Add(() => OpenEntityRename(
                "Save actor to library",
                ActorNames.Display(actorId, actor.Name),
                name => SaveOwnedActorEntry(actorId, name)));
        }

        items.Add(new ContextMenuItem("Expand", TablerIcon.SquarePlus));
        actions.Add(() => SetTreeCollapsed("actor:" + actorId, false, subtree: false));
        items.Add(new ContextMenuItem("Collapse", TablerIcon.SquareMinus));
        actions.Add(() => SetTreeCollapsed("actor:" + actorId, true, subtree: false));
        items.Add(new ContextMenuItem("All", TablerIcon.Copy,
            submenuItems:
            [
                new ContextMenuItem("Expand all", TablerIcon.Copy),
                new ContextMenuItem("Collapse all", TablerIcon.Copy),
            ]));
        actions.Add(null);

        List<Action?>? companionActions = null;
        if (attachment is { } attachmentState)
        {
            items.Add(ContextMenuItem.Separator);
            actions.Add(null);
            if (attachmentState.IsAttachedChild)
            {
                items.Add(new ContextMenuItem("Attachment", TablerIcon.Paw,
                    help: "Change or detach this body through its owner",
                    submenuItems:
                    [
                        new ContextMenuItem("Change", TablerIcon.UserPlus,
                            disruptive: true),
                        new ContextMenuItem("Detach", TablerIcon.UserMinus,
                            disruptive: true),
                    ]));
                companionActions =
                [
                    () => _companions.OpenAttachPicker(actorId),
                    () => _companions.Detach(actorId),
                ];
            }
            else
            {
                string verb = attachmentState.Occupied ? "Change" : "Attach";
                var rows = new List<ContextMenuItem>
                {
                    new(verb, TablerIcon.UserPlus, disruptive: true),
                };
                companionActions = [() => _companions.OpenAttachPicker(actorId)];
                if (attachmentState.Occupied)
                {
                    rows.Add(new ContextMenuItem(
                        "Detach", TablerIcon.UserMinus, disruptive: true));
                    companionActions.Add(() => _companions.Detach(actorId));
                }
                items.Add(new ContextMenuItem("Companion", TablerIcon.Paw,
                    help: "Attach, change or detach a minion, mount or ornament",
                    submenuItems: rows.ToArray()));
            }
            actions.Add(null); // Attachment submenu.
        }

        // Bone presets belong to this actor.
        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(
            "Bone presets", TablerIcon.Eye,
            disabled: !actor.HasSkeleton,
            help: "Named sets of which bones this actor shows in the overlay",
            submenuItems: actor.HasSkeleton
                ? BuildBonePresetSubmenu(actorId)
                : null));
        actions.Add(null); // separator
        actions.Add(null); // Child clicks are read separately.

        items.Add(ContextMenuItem.Separator);
        actions.Add(null); // separator
        bool hasStash = _cleanPose.HasStash;
        items.Add(new ContextMenuItem(
            "Pose", TablerIcon.Walk,
            disabled: !actor.HasSkeleton,
            help: actor.HasSkeleton
                ? "Import, export or stash this actor's pose"
                : "Needs a loaded skeleton",
            submenuItems: actor.HasSkeleton
                ? hasStash
                    ?
                    [
                        new ContextMenuItem("Import", TablerIcon.Download),
                        new ContextMenuItem(
                            "Import from file", TablerIcon.FileText),
                        new ContextMenuItem("Export", TablerIcon.Upload),
                        new ContextMenuItem("Stash", TablerIcon.Stack2),
                        new ContextMenuItem(
                            "Apply stashed", TablerIcon.ArrowBackUp),
                    ]
                    :
                    // No stash, no row: a menu never holds an empty seat.
                    [
                        new ContextMenuItem("Import", TablerIcon.Download),
                        new ContextMenuItem(
                            "Import from file", TablerIcon.FileText),
                        new ContextMenuItem("Export", TablerIcon.Upload),
                        new ContextMenuItem("Stash", TablerIcon.Stack2),
                    ]
                : null));
        actions.Add(null); // Pose — child clicks are read separately.
        var poseActions = new List<Action?>
        {
            () => _poseFileSection.RequestImportMenu(withPresets: true),
            () =>
            {
                if (actor.HasSkeleton)
                    _poseFileSection.OpenImportFromFile(actor.Skeleton);
            },
            () => _poseFileSection.RequestExportMenu(),
            () => _cleanPose.Stash(
                actor,
                ActorNames.Display(actorId, actor.Name)),
            () => _cleanPose.ApplyStash(actor),
        };

        // ONE verb for every actor, Brio's: Destroy
        // (Brio ActorLifetimeWidget.cs:82 — the same word whoever spawned
        // the actor, your own clone included). The row appears only when the
        // service would admit it right now — an actor it must refuse (a
        // companion child, a stale wrapper) gets no row rather than a row
        // that refuses.
        if (!attached && (
                _spawnService.IsSpawnedActor(actor)
                || _spawnService.RemovalRefusal(actor) is null))
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                string name = ActorNames.Clean(actor.Name);
                // Through the seam, exactly as Clone is: spawning an actor
                // is a history step; destroying is undoable only when Poser
                // spawned it and can respawn it.
                if (_lifecycle.DespawnActor(actor))
                {
                    // Drop the whole selection lineage — the actor, its
                    // bones, its bone groups — not every selection the user
                    // holds.
                    _selection.RemoveActorLineage(actorId.LogicalId);
                    _notices.Done($"Destroyed '{name}'.");
                }
                else
                {
                    _notices.Failed($"'{name}' could not be destroyed.");
                }
            });
        }

        if (_ctxOpenRequested)
        {
            _ctxOpenRequested = false;
            Crystarium.FloatingMenu.Open("##actor-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        // The preset rows show live checks: the menu takes this frame's
        // rows so a toggle shows at once while the menu stays open.
        Crystarium.FloatingMenu.Refresh("##actor-ctx", items.ToArray());
        int clicked = Crystarium.FloatingMenu.Draw("##actor-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        // Three submenus share the menu; the click routes by its parent
        // row's label.
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count)
        {
            var submenu = items[subParent].Label switch
            {
                "Bone presets" => _bonePresetActions,
                "Companion" or "Attachment" => companionActions,
                "Pose" => poseActions,
                "Duplicate" => new List<Action?>
                {
                    () => Duplicate(actor),
                    () => DuplicateWithPose(actor),
                },
                "All" => new List<Action?>
                {
                    () => SetTreeCollapsed("actor:" + actorId, false, subtree: true),
                    () => SetTreeCollapsed("actor:" + actorId, true, subtree: true),
                },
                _ => null,
            };
            if (submenu != null && subClicked < submenu.Count)
                submenu[subClicked]?.Invoke();
        }
    }

    private ContextMenuItem[] BuildBonePresetSubmenu(ActorId actorId)
    {
        if (_scene.Snapshot.FindActor(actorId.LogicalId) is not { } actor)
            return Array.Empty<ContextMenuItem>();
        _presetActorId = actorId;
        _bonePresetItems.Clear();
        _bonePresetActions.Clear();
        var presets = _bonePresets.Presets;
        if (presets.Count == 0)
        {
            _bonePresetItems.Add(new ContextMenuItem(
                "No presets yet", TablerIcon.Circle, disabled: true,
                help: "Show the bones you want, then save them as a preset"));
            _bonePresetActions.Add(null);
        }
        foreach (var preset in presets)
        {
            var name = preset.Name;
            _bonePresetItems.Add(new ContextMenuItem(
                name,
                _bonePresets.IsApplied(actor, name)
                    ? TablerIcon.CircleDot
                    : TablerIcon.Circle,
                keepOpen: true,
                help: $"{preset.Bones.Count} bones"));
            _bonePresetActions.Add(() => _bonePresets.Toggle(actor, name));
        }

        _bonePresetItems.Add(ContextMenuItem.Separator);
        _bonePresetActions.Add(null);
        _bonePresetItems.Add(new ContextMenuItem(
            "Show uncovered bones", TablerIcon.Crosshair,
            disabled: presets.Count == 0,
            help: "Hide everything the presets claim and show the rest"));
        _bonePresetActions.Add(() => _bonePresets.ToggleOther(actor));
        _bonePresetItems.Add(new ContextMenuItem(
            "Hide every bone", TablerIcon.EyeOff,
            help: "Take this actor's overlay back to nothing"));
        _bonePresetActions.Add(() => _bonePresets.Clear(actor));
        _bonePresetItems.Add(ContextMenuItem.Separator);
        _bonePresetActions.Add(null);
        _bonePresetItems.Add(new ContextMenuItem(
            "Manage presets", TablerIcon.Edit,
            help: "Save what this actor shows as a new preset, or delete one"));
        _bonePresetActions.Add(() =>
        {
            _presetNameValue = string.Empty;
            _presetSaveNote = null;
            _presetManagerOpen = true;
        });
        return _bonePresetItems.ToArray();
    }

    /// <summary>The preset store, which is shared by every actor: create one
    /// from what the menu's actor currently shows, or delete one. These
    /// operations apply immediately and remain outside Settings.</summary>
    private void DrawBonePresetManager()
    {
        if (!_presetManagerOpen)
            return;
        var actor = _presetActorId is { } id ? _scene.Snapshot.FindActor(id.LogicalId) : null;
        float gap = 8f * ImGuiHelpers.GlobalScale;
        Crystarium.Modal(
            "##bone-presets-manage",
            _presetManagerOpen,
            next => _presetManagerOpen = next,
            "Bone visibility presets",
            () =>
        {
            Crystarium.TextInput(
                "##bone-preset-name",
                _presetNameValue,
                next => _presetNameValue = next,
                placeholder: "New preset name");
            ImGui.Dummy(new Vector2(0f, gap));
            if (Crystarium.Button(
                    "Save what this actor shows",
                    variant: ButtonVariant.Primary,
                    id: "bone-preset-save",
                    disabled: actor == null,
                    help: "Store every bone currently shown in the overlay under that name"))
            {
                _presetSaveNote =
                    _bonePresets.SaveCurrent(_presetNameValue, actor!);
                if (_presetSaveNote == null)
                    _presetNameValue = string.Empty;
            }
            if (_presetSaveNote is { Length: > 0 } note)
                Crystarium.Text(note);

            ImGui.Dummy(new Vector2(0f, gap));
            var presets = _bonePresets.Presets;
            if (presets.Count == 0)
            {
                Crystarium.Text("No presets stored yet.");
                return;
            }
            string? doomed = null;
            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                var name = preset.Name;
                if (Crystarium.IconButton(
                        TablerIcon.Trash,
                        id: $"bone-preset-delete-{i}",
                        help: $"Delete '{name}'"))
                    doomed = name;
                ImGui.SameLine(0f, gap);
                Crystarium.Text($"{name} — {preset.Bones.Count} bones");
            }
            if (doomed != null)
            {
                _bonePresets.Delete(doomed);
                _presetSaveNote = null;
            }
        });
    }

    /// <summary>
    /// Right-click bone menu for hierarchy navigation and bone-local
    /// operations. Hierarchy facts come from the scene snapshot; selection and
    /// pose commands dispatch stable ids only.
    /// </summary>
    private void DrawBoneContextMenu()
    {
        if (_ctxBoneId is not { } boneId)
            return;

        var owner = _scene.Snapshot.FindActor(boneId.Skeleton.Actor.LogicalId);
        var bones = owner?.GetSkeleton(boneId.Slot)?.Bones;
        var descriptor = bones?.FirstOrDefault(candidate => candidate.Id.Equals(boneId));
        if (bones == null || descriptor == null)
        {
            _ctxBoneId = null;
            _ctxBoneOverlayBones = null;
            Crystarium.FloatingMenu.Dismiss("##bone-ctx");
            return;
        }

        var mirrorName = _bonePosingService.GetMirrorBoneName(boneId.CanonicalName);
        var mirror = mirrorName == null
            ? null
            : bones.FirstOrDefault(candidate =>
                candidate.Id.CanonicalName == mirrorName &&
                candidate.Id.PartialId == boneId.PartialId);
        bool hasChildren = bones.Any(candidate => candidate.Parent?.Equals(boneId) == true);

        var overlayBones = _ctxBoneOverlayBones ?? new[] { boneId };
        bool overlayVisible =
            _overlayPresentation.AreVisible(overlayBones);
        var items = new[]
        {
            new ContextMenuItem("Select parent", TablerIcon.SelectParent, disabled: descriptor.Parent == null),
            new ContextMenuItem("Select children", TablerIcon.SelectChildren, disabled: !hasChildren),
            new ContextMenuItem("Select mirrored bone", TablerIcon.SelectMirror, disabled: mirror == null),
            new ContextMenuItem(
                overlayVisible
                    ? "Hide from overlay"
                    : "Show in overlay",
                overlayVisible
                    ? TablerIcon.EyeOff
                    : TablerIcon.Eye),
            ContextMenuItem.Separator,
            new ContextMenuItem("Flip bone", TablerIcon.Rotate),
            new ContextMenuItem("Reset bone", TablerIcon.Refresh, danger: true),
        };

        if (_boneCtxOpenRequested)
        {
            _boneCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open("##bone-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##bone-ctx");
        switch (clicked)
        {
            case 0 when descriptor.Parent is { } parent:
                _selection.Select(SelectionId.ForBone(parent));
                break;
            case 1:
            {
                _selection.Select(SelectionId.ForBone(boneId));
                var byId = bones.ToDictionary(candidate => candidate.Id);
                foreach (var candidate in bones)
                {
                    for (var parent = candidate.Parent;
                         parent is { } parentId;
                         parent = byId.TryGetValue(parentId, out var parentDescriptor)
                             ? parentDescriptor.Parent
                             : null)
                    {
                        if (!parentId.Equals(boneId))
                            continue;
                        _selection.Add(SelectionId.ForBone(candidate.Id));
                        break;
                    }
                }
                break;
            }
            case 2 when mirror != null:
                _selection.Select(SelectionId.ForBone(mirror.Id));
                break;
            case 3:
                _overlayPresentation.SetVisible(
                    overlayBones,
                    !overlayVisible);
                break;
            case 5:
                _cleanPose.FlipBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
            case 6:
                _cleanPose.ResetBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
        }
    }

    private ReferenceImageInstance? _ctxReferenceImage;

    private bool _referenceCtxOpenRequested;

    /// <summary>
    /// A reference picture's verbs, in the overlay-node rows' family: the eye's
    /// own verb, the rename every named thing in the tree carries, a second
    /// placement, and the close. No transform verbs and no journal entry — a
    /// picture is not in the scene, so there is nothing for undo to restore it
    /// to and nothing to isolate it from.
    /// </summary>
    private void DrawReferenceImageContextMenu()
    {
        if (_ctxReferenceImage is not { } image)
            return;
        // A picture closed from its own bar while the menu is up leaves the
        // roster; the menu goes with it rather than acting on a dead entry.
        if (!_referenceImages.Instances.Contains(image))
        {
            _ctxReferenceImage = null;
            Crystarium.FloatingMenu.Dismiss("##reference-ctx");
            return;
        }
        bool hidden = ReferenceImageSession.IsHidden(image);
        var items = new[]
        {
            new ContextMenuItem(
                hidden ? "Show" : "Hide",
                hidden ? TablerIcon.Eye : TablerIcon.EyeOff),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Remove", TablerIcon.Trash),
        };
        if (_referenceCtxOpenRequested)
        {
            _referenceCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##reference-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##reference-ctx");
        if (clicked < 0)
            return;
        switch (clicked)
        {
            case 0:
                _referenceImages.SetHidden(image, !hidden);
                break;
            case 1:
                OpenEntityRename(
                    "Rename reference image",
                    image.Name,
                    next => image.Entry.Name = next);
                break;
            case 2:
                _referenceImages.Duplicate(image);
                break;
            case 3:
                _referenceImages.Close(image);
                break;
        }
        _ctxReferenceImage = null;
    }

    private OverlayId? _ctxOverlayNodeId;

    private bool _overlayNodeCtxOpenRequested;

    /// <summary>Right-click menu for a staged overlay NODE (balloon, talk,
    /// status) — distinct from the bone-category overlay menu below. The
    /// same lifetime family the light menu speaks, in the overlay's
    /// vocabulary; the pane's own Duplicate is reused so one duplication
    /// rule answers everywhere.</summary>
    private void DrawOverlayNodeContextMenu()
    {
        if (_ctxOverlayNodeId is not { } overlayId)
            return;
        var resolved = _bindings.Resolve(overlayId);
        if (!resolved.Success || resolved.Value is not { } node)
        {
            _ctxOverlayNodeId = null;
            Crystarium.FloatingMenu.Dismiss("##overlay-node-ctx");
            return;
        }
        var items = new[]
        {
            new ContextMenuItem(node.Visible ? "Hide" : "Show",
                node.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => _sessions.Overlays.SetVisible(node, !node.Visible),
            () => OpenEntityRename(
                "Rename overlay", node.Name, next => node.Name = next),
            () => _overlayPane.Duplicate(node),
            () => OpenEntityRename(
                "Save overlay to library", node.State.Name,
                name => _scenePane.SaveOverlayEntry(
                    overlayId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.DestroyOverlay(node);
                _selection.Clear();
            },
        };
        if (_overlayNodeCtxOpenRequested)
        {
            _overlayNodeCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##overlay-node-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##overlay-node-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    private void DrawOverlayContextMenu()
    {
        if (_ctxOverlayBones is not { } bones)
            return;
        var owner = _scene.Snapshot.Actors.FirstOrDefault(actor =>
            actor.Skeletons.Any(skeleton =>
                skeleton.Bones.Any(candidate =>
                    bones.Contains(candidate.Id))));
        if (owner == null)
        {
            _ctxOverlayBones = null;
            _ctxOverlayMemoryKey = null;
            Crystarium.FloatingMenu.Dismiss("##overlay-ctx");
            return;
        }
        var state = _overlayPresentation.Resolve(bones);
        var items = new[]
        {
            new ContextMenuItem(
                state switch
                {
                    OverlayVisibility.None => "Show category in overlay",
                    _ => "Hide the currently shown bones",
                },
                state == OverlayVisibility.None
                    ? TablerIcon.Eye
                    : TablerIcon.EyeOff),
            new ContextMenuItem("Show only this category", TablerIcon.Crosshair),
            new ContextMenuItem("Show all of this actor", TablerIcon.Eye),
            new ContextMenuItem("Hide all of this actor", TablerIcon.EyeOff),
        };
        if (_overlayCtxOpenRequested)
        {
            _overlayCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##overlay-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##overlay-ctx");
        if (clicked < 0)
            return;
        // Isolate/show-all operate on the owning actor's bones only, so other
        // actors' overlay masks are untouched.
        var ownerBones = owner.Skeletons
            .SelectMany(skeleton => skeleton.Bones)
            .Select(candidate => candidate.Id)
            .ToArray();
        switch (clicked)
        {
            case 0:
                if (_ctxOverlayMemoryKey is { } memoryKey)
                    _overlayPresentation.ToggleVisibleWithMemory(
                        memoryKey, bones);
                else
                    _overlayPresentation.SetVisible(
                        bones, state == OverlayVisibility.None);
                break;
            case 1:
                _overlayPresentation.SetVisible(ownerBones, false);
                _overlayPresentation.SetVisible(bones, true);
                break;
            case 2:
                _overlayPresentation.SetVisible(ownerBones, true);
                break;
            case 3:
                _overlayPresentation.SetVisible(ownerBones, false);
                break;
        }
        _ctxOverlayBones = null;
        _ctxOverlayMemoryKey = null;
    }

    // ── light / camera / prop context menus ─────────────────────────────

    private LightId? _ctxLightId;

    private bool _lightCtxOpenRequested;

    private CameraId? _ctxCameraId;

    private bool _cameraCtxOpenRequested;

    private PropId? _ctxPropId;

    private bool _propCtxOpenRequested;

    /// <summary>THE naming prompt, shared with every pane: lights,
    /// cameras and props carry their name on the entity, so one modal
    /// writes whichever apply hook the opener handed it — unlike the
    /// actor modal, which writes a nickname beside a name the game
    /// owns.</summary>
    private readonly Controls.EntityNameModal _names;
    private readonly Controls.IssueReportModal _issueReport;

    /// <summary>Right-click light menu: the lifetime verbs the actor menu
    /// gives its rows, spoken in the light's vocabulary — the eye, the file,
    /// and the ownership-aware destroy/release the actions section makes.
    /// </summary>
    private void DrawLightContextMenu()
    {
        if (_ctxLightId is not { } lightId)
            return;
        var resolved = _bindings.Resolve(lightId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } light)
        {
            _ctxLightId = null;
            Crystarium.FloatingMenu.Dismiss("##light-ctx");
            return;
        }

        var items = new List<ContextMenuItem>
        {
            new(light.IsOn ? "Switch off" : "Switch on",
                light.IsOn ? TablerIcon.EyeOff : TablerIcon.Eye),
            new("Rename", TablerIcon.Edit),
            new("Duplicate", TablerIcon.Copy),
            new("Save to file…", TablerIcon.DeviceFloppy),
            new("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
        };
        var actions = new List<Action?>
        {
            () => _sessions.Lights.SetIsOn(light, !light.IsOn),
            () => OpenEntityRename(
                "Rename light", light.Name, next => light.Name = next),
            () => _lifecycle.CloneLight(light),
            () => _lightPane.OpenSave(light),
            // The library save asks for the entry's NAME first — the same
            // modal renames use, with the light's name as the start.
            () => OpenEntityRename(
                "Save light to library", light.Name,
                name => _scenePane.SaveLightEntry(lightId.LogicalId, name)),
            null, // separator
        };
        if (light.Ownership == LightOwnership.Spawned)
        {
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(() =>
            {
                _lifecycle.DestroyLight(light);
                _selection.Clear();
            });
        }
        else
        {
            items.Add(new ContextMenuItem("Release", TablerIcon.X));
            actions.Add(() =>
            {
                _lightingService.ReleaseLight(light);
                _selection.Clear();
            });
        }

        if (_lightCtxOpenRequested)
        {
            _lightCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##light-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##light-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
    }

    /// <summary>
    /// Right-click prop menu: the same lifetime family the light menu speaks,
    /// in the prop's vocabulary. A prop was the one entity row whose right
    /// click did nothing at all, while actors, bones, categories, lights and
    /// cameras all answered.
    ///
    /// <para>There is no "Save to file…" row because a prop has no document
    /// of its own — its whole identity is the model triple, which the scene
    /// file carries. Every lifetime verb goes through the history seam, so a
    /// clone and destroy use the same history seam as light actions.</para>
    /// </summary>
    private void DrawPropContextMenu()
    {
        if (_ctxPropId is not { } propId)
            return;
        var resolved = _bindings.Resolve(propId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } prop)
        {
            _ctxPropId = null;
            Crystarium.FloatingMenu.Dismiss("##prop-ctx");
            return;
        }

        var items = new ContextMenuItem[]
        {
            new(prop.Visible ? "Hide" : "Show",
                prop.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new("Rename", TablerIcon.Edit),
            new("Duplicate", TablerIcon.Copy),
            new("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            new("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => _sessions.Props.SetVisible(prop, !prop.Visible),
            () => OpenEntityRename(
                "Rename object", prop.Name, next => prop.Name = next),
            () =>
            {
                if (_lifecycle.CloneProp(prop) is IPropHandle clone &&
                    _bindings.GetPropId(clone) is { } cloneId)
                    _selection.Select(SelectionId.ForProp(cloneId));
            },
            () => OpenEntityRename(
                "Save prop to library", prop.Name,
                name => _scenePane.SavePropEntry(propId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.DestroyProp(prop);
                _selection.Clear();
            },
        };

        if (_propCtxOpenRequested)
        {
            _propCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##prop-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##prop-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    /// <summary>Right-click camera menu for live, framing, file, and lifetime
    /// actions. The default camera cannot be destroyed.
    /// </summary>
    private void DrawCameraContextMenu()
    {
        if (_ctxCameraId is not { } cameraId)
            return;
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success ||
            resolved.Value is not { IsValid: true } camera ||
            _bindings.GetCameraId(camera) != cameraId)
        {
            _ctxCameraId = null;
            Crystarium.FloatingMenu.Dismiss("##camera-ctx");
            return;
        }

        bool canRecenterTracked = CanRecenterOnTracked(camera);
        var items = new List<ContextMenuItem>
        {
            new(camera.IsLive
                    ? "Return to main camera"
                    : "Look through", TablerIcon.Video,
                disabled: camera.IsLive && camera.IsDefault),
            new(camera.IsLocked ? "Unlock" : "Lock",
                camera.IsLocked ? TablerIcon.LockOpen : TablerIcon.Lock),
            new("Look at tracked actor", TablerIcon.Crosshair,
                disabled: !canRecenterTracked,
                help: "Swing the camera back onto whoever it tracks"),
            new("Rename", TablerIcon.Edit, disabled: camera.IsLocked),
            new("Duplicate", TablerIcon.Copy),
            new("Save to file…", TablerIcon.DeviceFloppy),
            new("Save to library", TablerIcon.Library),
            new("Reset transform", TablerIcon.Refresh,
                disabled: camera.IsLocked || !_cameraService.IsAvailable),
            new("Reset properties", TablerIcon.Refresh,
                disabled: camera.IsLocked),
        };
        var actions = new List<Action?>
        {
            () =>
            {
                if (!camera.IsLive)
                {
                    _cameraService.SetLive(camera);
                    return;
                }
                foreach (var candidate in _cameraService.Cameras)
                {
                    if (candidate.IsDefault)
                    {
                        _cameraService.SetLive(candidate);
                        break;
                    }
                }
            },
            () => _sessions.Cameras.SetLocked(camera, !camera.IsLocked),
            () => RecenterCameraOnTrackedActor(cameraId),
            () => OpenEntityRename(
                "Rename camera", camera.Name, next => camera.Name = next),
            () =>
            {
                if (_lifecycle.CloneCamera(camera) is { } clone)
                    _cameraPane.SelectWhenBound(clone);
            },
            () => _cameraPane.OpenSave(camera),
            () => OpenEntityRename(
                "Save camera to library", camera.Name,
                name => _scenePane.SaveCameraEntry(cameraId.LogicalId, name)),
            () => _cameraPane.ResetCameraTransform(cameraId),
            () => camera.ResetProperties(),
        };
        if (!camera.IsDefault)
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                _lifecycle.DestroyCamera(camera);
                _selection.Clear();
            });
        }

        if (_cameraCtxOpenRequested)
        {
            _cameraCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##camera-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##camera-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
    }

    // ── world-object / group / selection context menus ──────────────────

    private WorldObjectId? _ctxWorldObjectId;

    private bool _worldObjectCtxOpenRequested;

    private Guid? _ctxGroupId;

    private bool _groupCtxOpenRequested;

    private bool _selectionCtxOpenRequested;

    /// <summary>Right-click borrowed-object menu: the eye, the user's own
    /// name over the map's model, and Release — never Destroy, because the
    /// map owns the thing and gets it back where it stood.</summary>
    private void DrawWorldObjectContextMenu()
    {
        if (_ctxWorldObjectId is not { } worldObjectId)
            return;
        var resolved = _bindings.Resolve(worldObjectId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } worldObject)
        {
            _ctxWorldObjectId = null;
            Crystarium.FloatingMenu.Dismiss("##world-object-ctx");
            return;
        }
        var items = new[]
        {
            new ContextMenuItem(worldObject.Visible ? "Hide" : "Show",
                worldObject.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            // A spawned object is Poser's own and DESTROYS; a borrowed
            // one is the map's and goes back where it stood.
            worldObject.Spawned
                ? new ContextMenuItem("Destroy", TablerIcon.Trash,
                    danger: true)
                : new ContextMenuItem("Release", TablerIcon.X),
        };
        var actions = new Action?[]
        {
            () => _sessions.WorldObjects.SetVisible(worldObject, !worldObject.Visible),
            () => OpenEntityRename(
                "Rename object", worldObject.Name,
                next => worldObject.Name = next),
            () =>
            {
                if (DuplicateWorldObject(worldObject) is { } copy
                    && _bindings.GetWorldObjectId(copy) is { } copyId)
                    _selection.Select(SelectionId.ForWorldObject(copyId));
            },
            () => OpenEntityRename(
                "Save object to library", worldObject.Name,
                name => _scenePane.SaveWorldObjectEntry(
                    worldObjectId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.ReleaseWorldObject(worldObject);
                _selection.Clear();
            },
        };
        if (_worldObjectCtxOpenRequested)
        {
            _worldObjectCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##world-object-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##world-object-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    /// <summary>Right-click group-head menu: the structure verbs. The
    /// selection verbs live one click away — the head's left click IS the
    /// member selection, whose own menu then answers.</summary>
    private void DrawGroupContextMenu()
    {
        if (_ctxGroupId is not { } groupId)
            return;
        if (_groups.Find(groupId) is not { } group)
        {
            _ctxGroupId = null;
            Crystarium.FloatingMenu.Dismiss("##group-ctx");
            return;
        }
        bool locked = group.Locked;
        // The gates read as the group's own state: closed shows the verb
        // that opens it. A closed gate anywhere above still wins.
        var items = new[]
        {
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy,
                submenuItems: DuplicateSubmenu(posable: true)),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            new ContextMenuItem(locked ? "Unlock" : "Lock",
                locked ? TablerIcon.LockOpen : TablerIcon.Lock),
            ContextMenuItem.Separator,
            new ContextMenuItem(group.Hidden ? "Show" : "Hide",
                group.Hidden ? TablerIcon.Eye : TablerIcon.EyeOff),
            new ContextMenuItem(group.Paused ? "Play" : "Pause",
                group.Paused ? TablerIcon.PlayerPlay : TablerIcon.PlayerPause),
            new ContextMenuItem(group.Night ? "Day" : "Night",
                group.Night ? TablerIcon.Sun : TablerIcon.Moon),
            ContextMenuItem.Separator,
            new ContextMenuItem("Ungroup", TablerIcon.X),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => OpenEntityRename(
                "Rename group", group.Name,
                next => _groupSteps.Rename(groupId, next)),
            null, // Duplicate — child clicks are read separately.
            () => OpenEntityRename(
                "Save group to library", group.Name,
                name => _scenePane.SaveGroupEntry(
                    group.Members, name, AllActorsOwned(group.Members))),
            () => _groupSteps.SetLocked(groupId, !group.Locked),
            null, // separator
            () => SetGroupHidden(group, !group.Hidden),
            () => SetGroupPaused(group, !group.Paused),
            () => SetGroupNight(group, !group.Night),
            null, // separator
            () => DissolveGroup(groupId),
            null, // separator
            // The members go through each kind's own lifetime seam; the
            // emptied group dissolves through the scene prune.
            () => DestroyEntities(group.Members.ToArray()),
        };
        if (_groupCtxOpenRequested)
        {
            _groupCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##group-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##group-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Length
            && items[subParent].Label == "Duplicate")
            DuplicateGroup(group, withPose: subClicked == 1);
    }

    /// <summary>Right-click on any row of a multi-entity selection: one
    /// menu for the WHOLE selection, every verb dispatching per kind
    /// through the same plumbing the single menus use. A kind a verb
    /// cannot reach is skipped, never refused; verbs no selected kind
    /// answers disable in place.</summary>
    private void DrawSelectionContextMenu()
    {
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities < 2)
        {
            Crystarium.FloatingMenu.Dismiss("##selection-ctx");
            return;
        }

        // Hide/Show and Pause/Play drive the set to ONE state: any
        // visible member means Hide, anything running means Pause. The
        // pause verb exists only when something in the set animates.
        bool anyVisible = false, anyAnimated = false, anyRunning = false;
        bool anyActor = false;
        foreach (var id in _selection.Selected)
        {
            if (PlayingOf(id) is { } playing)
            {
                anyAnimated = true;
                anyRunning |= playing;
            }
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    anyActor = true;
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor }
                        && _spawnService.IsVisible(actor))
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsOn: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
            }
        }

        var matched = _groups.ActiveSelection(_selection.Selected);
        // With an actor in the set, Duplicate opens the plain/posed
        // choice; without one there is nothing to pose.
        var items = new List<ContextMenuItem>
        {
            new("Duplicate", TablerIcon.Copy,
                submenuItems: anyActor ? DuplicateSubmenu(posable: true) : null),
            new(anyVisible ? "Hide" : "Show",
                anyVisible ? TablerIcon.EyeOff : TablerIcon.Eye),
        };
        var actions = new List<Action?>
        {
            anyActor ? null : () => DuplicateSelection(withPose: false),
            () => SetSelectionVisible(!anyVisible),
        };
        if (anyAnimated)
        {
            items.Add(new ContextMenuItem(anyRunning ? "Pause" : "Play",
                anyRunning ? TablerIcon.PlayerPause : TablerIcon.PlayerPlay));
            actions.Add(() => SetSelectionPaused(anyRunning));
        }
        items.Add(new ContextMenuItem("Move to camera", TablerIcon.Crosshair));
        actions.Add(MoveSelectionToCamera);
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        if (matched != null)
        {
            items.Add(new ContextMenuItem(
                "Save to library", TablerIcon.Library));
            actions.Add(() => OpenEntityRename(
                "Save group to library", matched.Name,
                name => _scenePane.SaveGroupEntry(
                    matched.Members, name, AllActorsOwned(matched.Members))));
            items.Add(new ContextMenuItem("Ungroup", TablerIcon.X));
            actions.Add(() => DissolveGroup(matched.Id));
        }
        else
        {
            items.Add(new ContextMenuItem("Group…", TablerIcon.Folder));
            actions.Add(() => OpenEntityRename(
                "Name the group",
                $"Group {_groups.All.Count + 1}",
                name => _groupSteps.Create(name, _selection.Selected)));
        }
        items.Add(new ContextMenuItem("Deselect", TablerIcon.X));
        actions.Add(() => _selection.Clear());
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash,
            danger: true));
        actions.Add(DestroySelection);
        if (_selectionCtxOpenRequested)
        {
            _selectionCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##selection-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##selection-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count
            && items[subParent].Label == "Duplicate")
            DuplicateSelection(withPose: subClicked == 1);
    }
}
