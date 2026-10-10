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
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;
using Poser.UI.Widgets;
using Poser.Domain.Posing;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.DialogWidgets;
using static Poser.UI.Widgets.TextInputWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI;

/// <summary>The context menus per entity kind.</summary>
internal sealed partial class EntityContextMenus
{
    /// <summary>The selected actor's skeleton, or null when nothing posable is
    /// selected or its binding no longer resolves.</summary>

    /// <summary>Right-click actor menu: the lifetime actions that were stranded
    /// without a sidebar affordance (target / visibility / rename / clone / companion / despawn).
    /// The menu state is a stable ActorId; the legacy lifetime services still
    /// take live actors, so the id resolves through the binding registry for
    /// the duration of one frame and is dropped when resolution fails.</summary>
    public void DrawActorContextMenu()
    {
        if (!_ctxOpenRequested && !FloatingMenu.IsOpen("##actor-ctx")) return;
        if (_ctxActorId is not { } actorId)
            return;
        var actor = ResolveActorDescriptor(actorId);
        if (actor == null)
        {
            _ctxActorId = null;
            FloatingMenu.Dismiss("##actor-ctx");
            return;
        }
        var descriptor = actor;
        bool attached = descriptor?.OwnerActor is not null;
        var attachment = _companions.ActionsFor(actorId);

        var items = new List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Crosshair),
            new("Center camera on actor", TablerIcon.Crosshair),
            new(!(IsEntityVisible(SelectionId.ForActor(actorId)) ?? false) ? "Show" : "Hide", !(IsEntityVisible(SelectionId.ForActor(actorId)) ?? false) ? TablerIcon.Eye : TablerIcon.EyeOff),
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
            () => _actorControl.SetGameTarget(actorId),
            () => _cameraPane.CenterOnActor(actorId),
            () => SetEntityVisible(SelectionId.ForActor(actorId),
                !(IsEntityVisible(SelectionId.ForActor(actorId)) ?? false)),
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
                ActorNames.Display(_configuration, actorId, actor.Name),
                name => _configuration.SetNickname(
                    actorId.LogicalId, name),
                clear: () => _configuration.SetNickname(
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
                    : DuplicateSubmenu((actor.CharacterSkeleton != null))));
            actions.Add(null); // Duplicate — child clicks are read separately.
            items.Add(new ContextMenuItem("Save to library", TablerIcon.Library,
                disabled: !(actor.CharacterSkeleton != null)));
            actions.Add(() => OpenEntityRename(
                "Save actor to library",
                ActorNames.Display(_configuration, actorId, actor.Name),
                name => SaveOwnedActorEntry(actorId, name)));
        }

        items.Add(new ContextMenuItem("Create collider", TablerIcon.Cube,
            disabled: !(actor.CharacterSkeleton != null) || _actorColliderCapture.Busy));
        actions.Add(() => CreateActorCollider(actorId, ActorNames.Display(_configuration, actorId, actor.Name)));

        items.Add(new ContextMenuItem("Export Idle Pose…", TablerIcon.Upload,
            disabled: actor.CharacterSkeleton == null || _poseFileSection.IdleExportBusy,
            help: "Bake body and expression into a Penumbra PMP"));
        actions.Add(() => _poseFileSection.OpenIdleExport(actorId));

        items.Add(new ContextMenuItem("Tree", TablerIcon.Folder,
            submenuItems: BuildTreeSubmenu("actor:" + actorId, out var treeActions)));
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
                    submenuItems: BindSubmenu(rows.ToArray(), companionActions)));
            }
            actions.Add(null); // Attachment submenu.
        }

        // Bone presets belong to this actor.
        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(
            "Bone presets", TablerIcon.Eye,
            disabled: !(actor.CharacterSkeleton != null),
            help: "Named sets of which bones this actor shows in the overlay",
            submenuItems: (actor.CharacterSkeleton != null)
                ? BuildBonePresetSubmenu(actorId)
                : null));
        actions.Add(null); // separator
        actions.Add(null); // Child clicks are read separately.

        items.Add(ContextMenuItem.Separator);
        actions.Add(null); // separator
        items.Add(new ContextMenuItem("Pose", TablerIcon.Walk,
            disabled: !(actor.CharacterSkeleton != null),
            help: "The clicked actor's pose, including its equipment slots",
            submenuItems: BuildActorPoseSubmenu(actorId, out var poseActions)));
        actions.Add(null);

        // ONE verb for every actor, Brio's: Destroy
        // (Brio ActorLifetimeWidget.cs:82 — the same word whoever spawned
        // the actor, your own clone included). The row appears only when the
        // service would admit it right now — an actor it must refuse (a
        // companion child, a stale wrapper) gets no row rather than a row
        // that refuses.
        if (!attached && _actorControl.Read(actorId)?.CanRemove == true)
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(async () =>
            {
                string name = ActorNames.Clean(actor.Name);
                if (_scene.Snapshot.FindActor(actorId)?.IsAdopted == true)
                {
                    await RemoveEntitiesSafely([SelectionId.ForActor(actorId)]);
                    return;
                }
                // Through the seam, exactly as Clone is: spawning an actor
                // is a history step; destroying is undoable only when Poser
                // spawned it and can respawn it.
                var removed = await RemoveEntitiesSafely(
                    [SelectionId.ForActor(actorId)]);
                if (removed is null)
                    return;
                if (removed == 1)
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

        AddHandleAction(items, actions, SelectionId.ForActor(actorId));
        var moreActions = MoveMoreActions(items, actions, SelectionId.ForActor(actorId));
        if (_ctxOpenRequested)
        {
            _ctxOpenRequested = false;
            OpenContextMenu("##actor-ctx", items.ToArray());
        }
        // The preset rows show live checks: the menu takes this frame's
        // rows so a toggle shows at once while the menu stays open.
        FloatingMenu.Refresh("##actor-ctx", items.ToArray());
        int clicked = FloatingMenu.Draw("##actor-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        // Route each submenu click through its parent row.
        int subClicked = FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (InvokeComposedAction(items, subParent, subClicked)) return;
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count)
        {
            var submenu = items[subParent].Label switch
            {
                "Bone presets" => _bonePresetActions,
                "More" => moreActions,
                "Companion" or "Attachment" => companionActions,
                "Pose" => poseActions,
                "Duplicate" => new List<Action?>
                {
                    () => DuplicateAndSelect(SelectionId.ForActor(actorId)),
                    () => DuplicateAndSelect(SelectionId.ForActor(actorId), withPose: true),
                },
                "Tree" => treeActions,
                _ => null,
            };
            if (submenu != null && subClicked < submenu.Count)
                submenu[subClicked]?.Invoke();
        }
    }

    private async void CreateActorCollider(ActorId actorId, string name)
    {
        try
        {
            var group = await _actorColliderCapture.CreateAsync(actorId, name);
            _selection.Select(group.Members[0]);
            foreach (var member in group.Members.Skip(1)) _selection.Add(member);
            _groups.ActiveGroupId = group.Id;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Actor collider capture failed");
            _notices.Refused("Create collider", ex.Message);
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
            "Toggle other", TablerIcon.Crosshair,
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
    public void DrawBonePresetManager()
    {
        if (!_presetManagerOpen)
            return;
        var actor = _presetActorId is { } id ? _scene.Snapshot.FindActor(id.LogicalId) : null;
        float gap = 8f * ImGuiHelpers.GlobalScale;
        Dialog(
            "##bone-presets-manage",
            _presetManagerOpen,
            next => _presetManagerOpen = next,
            "Bone visibility presets",
            () =>
        {
            var presets = _bonePresets.Presets;
            if (presets.Count == 0)
                Text("No presets stored yet.");
            var theme = ActiveTheme;
            float rowHeight = theme.Controls.ShellIconAction * ImGuiHelpers.GlobalScale;
            var countStyle = new TextStyle { Color = theme.TextDim };
            float countWidth = MeasureText("99999 bones", countStyle).X;
            string? doomed = null;
            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                var name = preset.Name;
                var row = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X;
                float nameWidth = MathF.Max(0f, width - rowHeight - countWidth - gap * 2f);
                TextInBand(row, new Vector2(nameWidth, rowHeight), name,
                    default, TextConstraint.Truncate(nameWidth));
                TextInBand(
                    row + new Vector2(width - rowHeight - gap - countWidth, 0f),
                    new Vector2(countWidth, rowHeight), $"{preset.Bones.Count} bones",
                    countStyle, TextAlign.End);
                ImGui.SetCursorScreenPos(row + new Vector2(width - rowHeight, 0f));
                if (IconButton(
                        TablerIcon.Trash,
                        style: ControlStyle.Square(theme.Controls.ShellIconAction),
                        id: $"bone-preset-delete-{i}",
                        help: $"Delete '{name}'"))
                    doomed = name;
                ImGui.SetCursorScreenPos(row + new Vector2(0f, rowHeight + gap));
            }
            if (doomed != null)
            {
                _bonePresets.Delete(doomed);
                _presetSaveNote = null;
            }
            ImGui.Dummy(new Vector2(0f, gap));
            TextInput(
                "##bone-preset-name",
                _presetNameValue,
                next => _presetNameValue = next,
                placeholder: "New preset name");
            if (_presetSaveNote is { Length: > 0 } note)
                Text(note);
        },
        footer: () =>
        {
            if (Button("Close", id: "bone-preset-close"))
                _presetManagerOpen = false;
            ImGui.SameLine(0f, gap);
            if (Button(
                    "Save preset",
                    variant: ButtonVariant.Primary,
                    id: "bone-preset-save",
                    disabled: actor == null || string.IsNullOrWhiteSpace(_presetNameValue),
                    help: "Store every bone currently shown in the overlay under that name"))
            {
                _presetSaveNote = _bonePresets.SaveCurrent(_presetNameValue, actor!);
                if (_presetSaveNote == null)
                    _presetNameValue = string.Empty;
            }
        });
    }

    /// <summary>
    /// Right-click bone menu for hierarchy navigation and bone-local
    /// operations. Hierarchy facts come from the scene snapshot; selection and
    /// pose commands dispatch stable ids only.
    /// </summary>
    public void DrawBoneContextMenu()
    {
        const string menu = "##bone-ctx";
        if (!_boneCtxOpenRequested && !FloatingMenu.IsOpen(menu)) return;
        if (_ctxBoneId is not { } boneId) return;
        var owner = _scene.Snapshot.FindActor(boneId.Skeleton.Actor);
        var bones = owner?.GetSkeleton(boneId.Slot)?.Bones;
        var descriptor = bones?.FirstOrDefault(b => b.Id == boneId);
        if (owner == null || bones == null || descriptor == null)
        {
            _ctxBoneId = null;
            FloatingMenu.Dismiss(menu);
            return;
        }
        var mirrorName = PoseMath.GetMirrorBoneName(boneId.CanonicalName);
        var mirror = bones.FirstOrDefault(b => b.Id.CanonicalName == mirrorName && b.Id.PartialId == boneId.PartialId);
        var branch = new List<BoneId> { boneId };
        var byId = bones.ToDictionary(b => b.Id);
        foreach (var candidate in bones)
            for (var parent = candidate.Parent; parent is { } parentId; parent = byId.TryGetValue(parentId, out var node) ? node.Parent : null)
                if (parentId == boneId) { branch.Add(candidate.Id); break; }
        var overlayBones = _ctxBoneOverlayBones ?? new[] { boneId };
        bool visible = _overlayPresentation.AreVisible(overlayBones);
        var tree = BindSubmenu(BuildTreeSubmenu(_ctxBoneExpandKey, out var treeActions), treeActions);
        var pose = BindSubmenu(BuildActorPoseSubmenu(owner.Id, out var poseActions), poseActions);
        var presets = BindSubmenu(BuildBonePresetSubmenu(owner.Id), _bonePresetActions);
        ContextMenuItem[] items =
        [
            new(visible ? "Hide from overlay" : "Show in overlay", visible ? TablerIcon.EyeOff : TablerIcon.Eye)
            { OnInvoke = () => _overlayPresentation.SetVisible(overlayBones, !visible) },
            new("Select", TablerIcon.Crosshair, submenuItems:
            [
                new("Parent", TablerIcon.SelectParent, disabled: descriptor.Parent == null)
                { OnInvoke = () => { if (descriptor.Parent is { } parent) _selection.Select(SelectionId.ForBone(parent)); } },
                new("Bone and descendants", TablerIcon.SelectChildren, disabled: branch.Count == 1)
                { OnInvoke = () => { _selection.Select(SelectionId.ForBone(boneId)); foreach (var child in branch.Skip(1)) _selection.Add(SelectionId.ForBone(child)); } },
                new("Mirrored bone", TablerIcon.SelectMirror, disabled: mirror == null)
                { OnInvoke = () => { if (mirror != null) _selection.Select(SelectionId.ForBone(mirror.Id)); } },
            ]),
            new("Flip bone", TablerIcon.Rotate)
            { OnInvoke = () => _cleanPose.FlipBone(TransformTargetId.ForBone(boneId), descriptor.DisplayName) },
            new("Reset", TablerIcon.Refresh, submenuItems:
            [
                new("Bone", TablerIcon.Refresh, disruptive: true)
                { OnInvoke = () => _cleanPose.ResetBone(TransformTargetId.ForBone(boneId), descriptor.DisplayName) },
                new("Bone and descendants", TablerIcon.Refresh, disruptive: true, disabled: branch.Count == 1)
                { OnInvoke = () => _cleanPose.ResetBones(branch.Select(TransformTargetId.ForBone).ToArray(), "Reset bone branch") },
            ]),
            new("Actor pose", TablerIcon.Walk, submenuItems: pose, help: "The whole owning actor, including equipment"),
            new("Bone presets", TablerIcon.Eye, submenuItems: presets),
            new("Tree", TablerIcon.Folder, submenuItems: tree),
        ];
        var mapTargets = _boneMap.AddToPresetActions(boneId);
        if (mapTargets.Length > 0)
            items = items.Append(new ContextMenuItem("Preset", TablerIcon.Edit, submenuItems: mapTargets)).ToArray();
        DrawComposedMenu(menu, ref _boneCtxOpenRequested, items);
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
    public void DrawReferenceImageContextMenu()
    {
        if (!_referenceCtxOpenRequested && !FloatingMenu.IsOpen("##reference-ctx")) return;
        if (_ctxReferenceImage is not { } image)
            return;
        // A picture closed from its own bar while the menu is up leaves the
        // roster; the menu goes with it rather than acting on a dead entry.
        if (!_referenceImages.Instances.Contains(image))
        {
            _ctxReferenceImage = null;
            FloatingMenu.Dismiss("##reference-ctx");
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
            new ContextMenuItem("Remove", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => _referenceImages.SetHidden(image, !hidden),
            () => OpenEntityRename("Rename reference image", image.Name, next => image.Entry.Name = next),
            () => _referenceImages.Duplicate(image),
            () => _referenceImages.Close(image),
        };
        MoveMoreActions(ref items, ref actions);
        if (_referenceCtxOpenRequested)
        {
            _referenceCtxOpenRequested = false;
            OpenContextMenu("##reference-ctx", items);
        }
        FloatingMenu.Refresh("##reference-ctx", items);
        int clicked = FloatingMenu.Draw("##reference-ctx");
        if (clicked < 0)
            return;
        if (clicked < actions.Length) actions[clicked]?.Invoke();
        _ctxReferenceImage = null;
    }

    private OverlayId? _ctxOverlayNodeId;

    private bool _overlayNodeCtxOpenRequested;

    /// <summary>Right-click menu for a staged overlay NODE (balloon, talk,
    /// status) — distinct from the bone-category overlay menu below. The
    /// same lifetime family the light menu speaks, in the overlay's
    /// vocabulary; the shared creation service supplies the duplication
    /// rule answers everywhere.</summary>
    public void DrawOverlayNodeContextMenu()
    {
        if (!_overlayNodeCtxOpenRequested && !FloatingMenu.IsOpen("##overlay-node-ctx")) return;
        if (_ctxOverlayNodeId is not { } overlayId)
            return;
        if (_overlayControl.Read(overlayId) is not { } node)
        {
            _ctxOverlayNodeId = null;
            FloatingMenu.Dismiss("##overlay-node-ctx");
            return;
        }
        var items = new[]
        {
            new ContextMenuItem(node.State.Visible ? "Hide" : "Show",
                node.State.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy all overlays…", TablerIcon.Trash, danger: true,
                disabled: _scene.Snapshot.Overlays.Count == 0),
        };
        var actions = new Action?[]
        {
            () => SetEntityVisible(SelectionId.ForOverlay(overlayId), !node.State.Visible),
            () => OpenEntityRename(
                "Rename overlay", node.State.Name, next => _overlayControl.Set(overlayId, Application.Presentation.OverlayProperties.Name, next)),
            () => DuplicateAndSelect(SelectionId.ForOverlay(overlayId)),
            () => OpenEntityRename(
                "Save overlay to library", node.State.Name,
                name => _scenePane.SaveEntry(SelectionId.ForOverlay(overlayId), name)),
            null, // separator
            () =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForOverlay(overlayId)],
                    () => _selection.Remove(SelectionId.ForOverlay(overlayId)));
            },
            null,
            _removalDialog.ConfirmDestroyAllOverlays,
        };
        if (node.State.Collider is { } collider)
        {
            items = new[] {
                new ContextMenuItem(collider.Locked ? "Unlock transform" : "Lock transform", TablerIcon.Lock),
                new ContextMenuItem(collider.Enabled ? "Disable collision" : "Enable collision", TablerIcon.Cube) }
                .Concat(items).ToArray();
            actions = new Action?[] {
                () => _overlayControl.EditCollider(overlayId, c => c with { Locked = !collider.Locked }),
                () => _overlayControl.EditCollider(overlayId, c => c with { Enabled = !collider.Enabled }) }
                .Concat(actions).ToArray();
        }
        AddHandleAction(ref items, ref actions, SelectionId.ForOverlay(overlayId));
        var moreActions = MoveMoreActions(ref items, ref actions, SelectionId.ForOverlay(overlayId));
        if (_overlayNodeCtxOpenRequested)
        {
            _overlayNodeCtxOpenRequested = false;
            OpenContextMenu("##overlay-node-ctx", items);
        }
        FloatingMenu.Refresh("##overlay-node-ctx", items);
        int clicked = FloatingMenu.Draw("##overlay-node-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        DrawMoreAction(items, moreActions);
    }

    public void DrawOverlayContextMenu()
    {
        const string menu = "##overlay-ctx";
        if (!_overlayCtxOpenRequested && !FloatingMenu.IsOpen(menu)) return;
        if (_ctxOverlayBones is not { Count: > 0 } captured) return;
        var ownerId = _ctxBranchSkeleton?.Actor ?? captured[0].Skeleton.Actor;
        var owner = _scene.Snapshot.FindActor(ownerId);
        var slot = _ctxBranchSkeleton is { } skeletonId ? owner?.GetSkeleton(skeletonId.Slot) : null;
        if (owner == null || (_ctxBranchSkeleton != null && slot == null))
        {
            _ctxOverlayBones = null;
            FloatingMenu.Dismiss(menu);
            return;
        }
        var bones = slot != null ? slot.Bones.Select(b => b.Id).ToArray() : captured;
        var ownerBones = owner.Skeletons.SelectMany(s => s.Bones).Select(b => b.Id).ToArray();
        bool visible = _overlayPresentation.Resolve(bones) != OverlayVisibility.None;
        string scope = _ctxBranchLabel;
        var tree = BindSubmenu(BuildTreeSubmenu(_ctxBranchExpandKey, out var treeActions), treeActions);
        var pose = BindSubmenu(BuildActorPoseSubmenu(owner.Id, out var poseActions), poseActions);
        var presets = BindSubmenu(BuildBonePresetSubmenu(owner.Id), _bonePresetActions);
        ContextMenuItem[] items =
        [
            new(visible ? "Hide from overlay" : "Show in overlay", visible ? TablerIcon.EyeOff : TablerIcon.Eye)
            { OnInvoke = () =>
                {
                    if (_ctxOverlayMemoryKey is { } memoryKey)
                        _overlayPresentation.ToggleVisibleWithMemory(memoryKey, bones);
                    else _overlayPresentation.SetVisible(bones, !visible);
                }
            },
            new("Select", TablerIcon.Crosshair, submenuItems:
            [
                new("Bones in " + scope, TablerIcon.SelectChildren) { OnInvoke = () => SelectBones(bones) },
                new("All actor bones", TablerIcon.SelectChildren) { OnInvoke = () => SelectBones(ownerBones) },
            ]),
            new("Visibility", TablerIcon.Eye, submenuItems:
            [
                new("Show only " + scope, TablerIcon.Crosshair)
                { OnInvoke = () => { _overlayPresentation.SetVisible(ownerBones, false); _overlayPresentation.SetVisible(bones, true); } },
                new("Show all actor bones", TablerIcon.Eye) { OnInvoke = () => _overlayPresentation.SetVisible(ownerBones, true) },
                new("Hide all actor bones", TablerIcon.EyeOff) { OnInvoke = () => _overlayPresentation.SetVisible(ownerBones, false) },
            ]),
            new("Reset", TablerIcon.Refresh, submenuItems:
            [
                new("Bones in " + scope, TablerIcon.Refresh, disruptive: true)
                { OnInvoke = () => _cleanPose.ResetBones(bones.Select(TransformTargetId.ForBone).ToArray(), "Reset " + scope) },
            ]),
            new("Actor pose", TablerIcon.Walk, submenuItems: pose, help: "The whole owning actor, including equipment"),
            new("Bone presets", TablerIcon.Eye, submenuItems: presets),
            new("Tree", TablerIcon.Folder, submenuItems: tree),
        ];
        DrawComposedMenu(menu, ref _overlayCtxOpenRequested, items);

        void SelectBones(IReadOnlyList<BoneId> selected)
        {
            _selection.Clear();
            foreach (var bone in selected) _selection.Add(SelectionId.ForBone(bone));
        }
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
    public void DrawLightContextMenu()
    {
        if (!_lightCtxOpenRequested && !FloatingMenu.IsOpen("##light-ctx")) return;
        if (_ctxLightId is not { } lightId)
            return;
        if (_lightControl.Read(lightId) is not { } light)
        {
            _ctxLightId = null;
            FloatingMenu.Dismiss("##light-ctx");
            return;
        }

        var items = new List<ContextMenuItem>
        {
            new(light.IsOn ? "Switch off" : "Switch on",
                light.IsOn ? TablerIcon.EyeOff : TablerIcon.Eye),
            new("Rename", TablerIcon.Edit),
            new("Move to camera", TablerIcon.Camera),
            new("Duplicate", TablerIcon.Copy),
            new("Save to file…", TablerIcon.DeviceFloppy),
            new("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
        };
        var actions = new List<Action?>
        {
            () => SetEntityVisible(SelectionId.ForLight(lightId), !light.IsOn),
            () => OpenEntityRename(
                "Rename light", light.Name, next => _lightControl.Set(lightId, Application.Presentation.LightProperties.Name, next)),
            () => _lightPane.MoveToCamera(lightId),
            () => DuplicateAndSelect(SelectionId.ForLight(lightId)),
            () => _lightPane.OpenSave(lightId),
            // The library save asks for the entry's NAME first — the same
            // modal renames use, with the light's name as the start.
            () => OpenEntityRename(
                "Save light to library", light.Name,
                name => _scenePane.SaveEntry(SelectionId.ForLight(lightId), name)),
            null, // separator
        };
        if (light.Ownership == LightOwnership.Spawned)
        {
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(() =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForLight(lightId)],
                    () => _selection.Remove(SelectionId.ForLight(lightId)));
            });
        }
        else
        {
            items.Add(new ContextMenuItem("Release", TablerIcon.X));
            actions.Add(() =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForLight(lightId)],
                    () => _selection.Remove(SelectionId.ForLight(lightId)));
            });
        }

        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("Destroy all lights…", TablerIcon.Trash,
            danger: true, disabled: _scene.Snapshot.Lights.Count == 0));
        actions.Add(_removalDialog.ConfirmDestroyAllLights);

        AddHandleAction(items, actions, SelectionId.ForLight(lightId));
        var moreActions = MoveMoreActions(items, actions, SelectionId.ForLight(lightId));
        if (_lightCtxOpenRequested)
        {
            _lightCtxOpenRequested = false;
            OpenContextMenu("##light-ctx", items.ToArray());
        }
        FloatingMenu.Refresh("##light-ctx", items.ToArray());
        int clicked = FloatingMenu.Draw("##light-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        DrawMoreAction(items, moreActions);
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
    public void DrawPropContextMenu()
    {
        if (!_propCtxOpenRequested && !FloatingMenu.IsOpen("##prop-ctx")) return;
        if (_ctxPropId is not { } propId)
            return;
        if (_objectControl.Read(propId) is not { } prop)
        {
            _ctxPropId = null;
            FloatingMenu.Dismiss("##prop-ctx");
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
            ContextMenuItem.Separator,
            new("Destroy all objects…", TablerIcon.Trash, danger: true,
                disabled: _scene.Snapshot.Props.Count == 0),
        };
        var actions = new Action?[]
        {
            () => SetEntityVisible(SelectionId.ForProp(propId), !prop.Visible),
            () => OpenEntityRename(
                "Rename object", prop.Name, next => _objectControl.Set(propId, Application.Presentation.PropProperties.Name, next)),
            () => DuplicateAndSelect(SelectionId.ForProp(propId)),
            () => OpenEntityRename(
                "Save prop to library", prop.Name,
                name => _scenePane.SaveEntry(SelectionId.ForProp(propId), name)),
            null, // separator
            () =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForProp(propId)],
                    () => _selection.Remove(SelectionId.ForProp(propId)));
            },
            null,
            _removalDialog.ConfirmDestroyAllProps,
        };

        AddHandleAction(ref items, ref actions, SelectionId.ForProp(propId));
        var moreActions = MoveMoreActions(ref items, ref actions, SelectionId.ForProp(propId));
        if (_propCtxOpenRequested)
        {
            _propCtxOpenRequested = false;
            OpenContextMenu("##prop-ctx", items);
        }
        FloatingMenu.Refresh("##prop-ctx", items);
        int clicked = FloatingMenu.Draw("##prop-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        DrawMoreAction(items, moreActions);
    }

    /// <summary>Right-click camera menu for live, framing, file, and lifetime
    /// actions. The default camera cannot be destroyed.
    /// </summary>
    public void DrawCameraContextMenu()
    {
        if (!_cameraCtxOpenRequested && !FloatingMenu.IsOpen("##camera-ctx")) return;
        if (_ctxCameraId is not { } cameraId)
            return;
        if (_cameraControl.Read(cameraId) is not { } camera)
        {
            _ctxCameraId = null;
            FloatingMenu.Dismiss("##camera-ctx");
            return;
        }

        bool canRecenterTracked = CanRecenterOnTracked(cameraId);
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
            new("Reset", TablerIcon.Refresh, submenuItems:
            [
                new("Transform", TablerIcon.Refresh,
                    disabled: camera.IsLocked || !camera.Available),
                new("Properties", TablerIcon.Refresh, disabled: camera.IsLocked),
            ]),
        };
        var actions = new List<Action?>
        {
            () => _cameraControl.SetLive(cameraId, !camera.IsLive),
            () => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.IsLocked, !camera.IsLocked),
            () => RecenterCameraOnTrackedActor(cameraId),
            () => OpenEntityRename(
                "Rename camera", camera.Name, next => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.Name, next)),
            () => DuplicateAndSelect(SelectionId.ForCamera(cameraId)),
            () => _cameraPane.OpenSave(cameraId),
            () => OpenEntityRename(
                "Save camera to library", camera.Name,
                name => _scenePane.SaveEntry(SelectionId.ForCamera(cameraId), name)),
            null,
        };
        if (!camera.IsDefault)
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForCamera(cameraId)],
                    () => _selection.Remove(SelectionId.ForCamera(cameraId)));
            });
        }

        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("Destroy all cameras…", TablerIcon.Trash,
            danger: true, disabled: !_scene.Snapshot.Cameras.Any(c => !c.IsDefault)));
        actions.Add(_removalDialog.ConfirmDestroyAllCameras);

        AddHandleAction(items, actions, SelectionId.ForCamera(cameraId));
        var moreActions = MoveMoreActions(items, actions);
        if (_cameraCtxOpenRequested)
        {
            _cameraCtxOpenRequested = false;
            OpenContextMenu("##camera-ctx", items.ToArray());
        }
        FloatingMenu.Refresh("##camera-ctx", items.ToArray());
        int clicked = FloatingMenu.Draw("##camera-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        int sub = FloatingMenu.ConsumeSubmenuClick(out int parent);
        if (parent >= 0 && parent < items.Count)
        {
            if (InvokeComposedAction(items, parent, sub)) return;
            if (items[parent].Label == "More" && sub >= 0 && sub < moreActions.Count)
                moreActions[sub]?.Invoke();
            else if (items[parent].Label == "Reset")
            {
                if (sub == 0) _cameraPane.ResetCameraTransform(cameraId);
                else if (sub == 1) _cameraControl.ResetProperties(cameraId);
            }
        }
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
    public void DrawWorldObjectContextMenu()
    {
        if (!_worldObjectCtxOpenRequested && !FloatingMenu.IsOpen("##world-object-ctx")) return;
        if (_ctxWorldObjectId is not { } worldObjectId)
            return;
        if (_objectControl.Read(worldObjectId) is not { } worldObject)
        {
            _ctxWorldObjectId = null;
            FloatingMenu.Dismiss("##world-object-ctx");
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
            () => SetEntityVisible(SelectionId.ForWorldObject(worldObjectId), !worldObject.Visible),
            () => OpenEntityRename(
                worldObject.IsFurniture ? "Rename furniture" : "Rename object", worldObject.Name,
                next => _objectControl.Set(worldObjectId, Application.Presentation.WorldObjectProperties.Name, next)),
            () =>
            {
                DuplicateAndSelect(SelectionId.ForWorldObject(worldObjectId));
            },
            () => OpenEntityRename(
                worldObject.IsFurniture ? "Save furniture to library" : "Save object to library", worldObject.Name,
                name => _scenePane.SaveEntry(SelectionId.ForWorldObject(worldObjectId), name)),
            null, // separator
            () =>
            {
                _ = RemoveEntitiesSafely(
                    [SelectionId.ForWorldObject(worldObjectId)],
                    () => _selection.Remove(
                        SelectionId.ForWorldObject(worldObjectId)));
            },
        };
        var stateItems = new List<ContextMenuItem>();
        var stateActions = new List<Action?>();
        if (worldObject.IsFurniture)
        {
            var lights = worldObject.FurnitureLights;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                stateItems.Add(new($"Light {i + 1}: {(light.Enabled ? "On" : "Off")}",
                    TablerIcon.Bulb, keepOpen: true));
                stateActions.Add(() => _objectControl.SetFurnitureLight(worldObjectId, light.Key, !light.Enabled));
            }
        }
        if (worldObject.IsVfx)
        {
            stateItems.Add(new(worldObject.VfxPaused ? "Play" : "Pause",
                worldObject.VfxPaused ? TablerIcon.PlayerPlay : TablerIcon.PlayerPause));
            stateActions.Add(() => _objectControl.Set(worldObjectId, Application.Presentation.WorldObjectProperties.VfxPaused, !worldObject.VfxPaused));
        }
        else
        {
            stateItems.Add(new(worldObject.NightState ? "Day" : "Night",
                worldObject.NightState ? TablerIcon.Sun : TablerIcon.Moon));
            stateActions.Add(() => _objectControl.Set(worldObjectId, Application.Presentation.WorldObjectProperties.NightState, !worldObject.NightState));
            if (!worldObject.Spawned)
            {
                stateItems.Add(new(worldObject.AnimationPaused ? "Play" : "Pause",
                    worldObject.AnimationPaused ? TablerIcon.PlayerPlay : TablerIcon.PlayerPause));
                stateActions.Add(() => _objectControl.Set(worldObjectId, Application.Presentation.WorldObjectProperties.AnimationPaused, !worldObject.AnimationPaused));
            }
        }
        items = stateItems.Concat(items).ToArray();
        actions = stateActions.Concat(actions).ToArray();
        AddHandleAction(ref items, ref actions, SelectionId.ForWorldObject(worldObjectId));
        var moreActions = MoveMoreActions(ref items, ref actions, SelectionId.ForWorldObject(worldObjectId));
        if (_worldObjectCtxOpenRequested)
        {
            _worldObjectCtxOpenRequested = false;
            OpenContextMenu("##world-object-ctx", items);
        }
        FloatingMenu.Refresh("##world-object-ctx", items);
        int clicked = FloatingMenu.Draw("##world-object-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        DrawMoreAction(items, moreActions);
    }

    /// <summary>Right-click group-head menu: the structure verbs. The
    /// selection verbs live one click away — the head's left click IS the
    /// member selection, whose own menu then answers.</summary>
    public void DrawGroupContextMenu()
    {
        if (!_groupCtxOpenRequested && !FloatingMenu.IsOpen("##group-ctx")) return;
        if (_ctxGroupId is not { } groupId)
            return;
        if (_groups.Find(groupId) is not { } group)
        {
            _ctxGroupId = null;
            FloatingMenu.Dismiss("##group-ctx");
            return;
        }
        bool locked = group.Locked;
        bool filtering = !string.IsNullOrEmpty(_sidebar.Filter);
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
            new ContextMenuItem(_overlayPresentation.IsHandleShown(groupId) ? "Hide handle" : "Show handle",
                _overlayPresentation.IsHandleShown(groupId) ? TablerIcon.EyeOff : TablerIcon.Eye,
                keepOpen: true, help: "Only the overlay handle; does not hide the group or its members"),
            new ContextMenuItem(group.Paused ? "Play" : "Pause",
                group.Paused ? TablerIcon.PlayerPlay : TablerIcon.PlayerPause),
            new ContextMenuItem(group.Night ? "Day" : "Night",
                group.Night ? TablerIcon.Sun : TablerIcon.Moon),
            ContextMenuItem.Separator,
            new ContextMenuItem("Ungroup", TablerIcon.X),
            new ContextMenuItem("Tree", TablerIcon.Folder, submenuItems:
            [
                new("Expand", TablerIcon.SquarePlus, disabled: filtering),
                new("Collapse", TablerIcon.SquareMinus, disabled: filtering),
                new("Expand all", TablerIcon.SquarePlus, disabled: filtering),
                new("Collapse all", TablerIcon.SquareMinus, disabled: filtering),
            ]),
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
                    _groups.Descendants(group).ToArray(), name)),
            () => _groupSteps.SetLocked(groupId, !group.Locked),
            null, // separator
            () => _groupSteps.SetHidden(group, !group.Hidden),
            () => _overlayPresentation.ToggleHandle(groupId),
            () => _groupSteps.SetPaused(group, !group.Paused),
            () => _groupSteps.SetNight(group, !group.Night),
            null, // separator
            () => _groupSteps.Dissolve(groupId),
            null, // Tree submenu.
            null, // separator
            // The members go through each kind's own lifetime seam; the
            // emptied group dissolves through the scene prune.
            () => _ = RemoveEntitiesSafely(group.Members.ToArray()),
        };
        items = items.Append(new ContextMenuItem("Move to camera", TablerIcon.Camera,
            disabled: locked || !_groups.Descendants(group).Any(_placement.CanPlace))).ToArray();
        actions = actions.Append((Action?)(() => ReportPlacement(_placement.MoveToCamera(_groups.Descendants(group).ToArray())))).ToArray();
        var moreActions = MoveMoreActions(ref items, ref actions);
        if (_groupCtxOpenRequested)
        {
            _groupCtxOpenRequested = false;
            OpenContextMenu("##group-ctx", items);
        }
        FloatingMenu.Refresh("##group-ctx", items);
        int clicked = FloatingMenu.Draw("##group-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        int subClicked = FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Length
            && InvokeComposedAction(items, subParent, subClicked)) return;
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Length
            && items[subParent].Label == "Duplicate")
            DuplicateGroup(group, withPose: subClicked == 1);
        else if (subClicked is >= 0 and < 4 && subParent >= 0 && subParent < items.Length
            && items[subParent].Label == "Tree")
            SetGroupTreeCollapsed(groupId, collapsed: subClicked % 2 == 1, subtree: subClicked >= 2);
        else if (subClicked >= 0 && subClicked < moreActions.Count
            && subParent >= 0 && subParent < items.Length
            && items[subParent].Label == "More")
            moreActions[subClicked]?.Invoke();
    }

    /// <summary>Right-click on any row of a multi-entity selection: one
    /// menu for the WHOLE selection, every verb dispatching per kind
    /// through the same plumbing the single menus use. A kind a verb
    /// cannot reach is skipped, never refused; verbs no selected kind
    /// answers disable in place.</summary>
    public void DrawSelectionContextMenu()
    {
        if (!_selectionCtxOpenRequested && !FloatingMenu.IsOpen("##selection-ctx")) return;
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities < 2)
        {
            FloatingMenu.Dismiss("##selection-ctx");
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
            anyActor |= id.Kind == SceneEntityKind.Actor;
            anyVisible |= IsEntityVisible(id) == true;
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
        if (matched is { } selectedGroup)
        {
            bool handleShown = _overlayPresentation.IsHandleShown(selectedGroup.Id);
            items.Add(new ContextMenuItem(handleShown ? "Hide handle" : "Show handle",
                handleShown ? TablerIcon.EyeOff : TablerIcon.Eye, keepOpen: true,
                help: "Only the overlay handle; does not hide the group or its members"));
            actions.Add(() => _overlayPresentation.ToggleHandle(selectedGroup.Id));
        }
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
                    _groups.Descendants(matched).ToArray(), name)));
            items.Add(new ContextMenuItem("Ungroup", TablerIcon.X));
            actions.Add(() => _groupSteps.Dissolve(matched.Id));
        }
        else
        {
            items.Add(new ContextMenuItem("Group…", TablerIcon.Folder));
            actions.Add(() => OpenEntityRename(
                "Name the group",
                global::Poser.Domain.Scene.EntityNames.Next("Group", _groups.All.Select(x => x.Name)),
                name => _groupSteps.Create(name, _selection.Selected)));
        }
        items.Add(new ContextMenuItem("Deselect", TablerIcon.X));
        actions.Add(() => _selection.Clear());
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash,
            danger: true));
        actions.Add(DestroySelection);
        var moreActions = MoveMoreActions(items, actions);
        if (_selectionCtxOpenRequested)
        {
            _selectionCtxOpenRequested = false;
            OpenContextMenu("##selection-ctx", items.ToArray());
        }
        FloatingMenu.Refresh("##selection-ctx", items.ToArray());
        int clicked = FloatingMenu.Draw("##selection-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        int subClicked = FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count
            && InvokeComposedAction(items, subParent, subClicked)) return;
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count
            && items[subParent].Label == "Duplicate")
            DuplicateSelection(withPose: subClicked == 1);
        else if (subClicked >= 0 && subClicked < moreActions.Count
            && subParent >= 0 && subParent < items.Count
            && items[subParent].Label == "More")
            moreActions[subClicked]?.Invoke();
    }
}
