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
    /// <summary>Actor context menu. The stored <see cref="ActorId"/> is resolved
    /// for each draw, because the actor may have left since the menu opened.</summary>
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
        var id = SelectionId.ForActor(actorId);
        bool attached = actor.OwnerActor is not null;
        bool posable = actor.CharacterSkeleton != null;
        bool visible = IsEntityVisible(id) ?? false;
        bool playing = _animation.AnyPlaying(actorId);
        // The name shown, never the real one: with Anonymous mode on, the real
        // name must not appear in the prompt.
        string shown = ActorNames.Display(_configuration, actorId, actor.Name);

        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            new("Center camera on actor", TablerIcon.Crosshair)
            { OnInvoke = () => _cameraPane.CenterOnActor(actorId), Place = MenuPlace.Framing },
            new("Set game target", TablerIcon.Crosshair)
            { OnInvoke = () => _actorControl.SetGameTarget(actorId), Place = MenuPlace.View },
            VisibilityItem(visible, () => SetEntityVisible(id, !visible)),
            PlaybackItem(playing, () => { if (playing) _animation.Pause(actorId); else _animation.Resume(actorId); },
                disabled: !_animation.IsSupported(actorId)),
            RenameItem("Rename actor", shown, name => _configuration.SetNickname(actorId.LogicalId, name),
                clear: () => _configuration.SetNickname(actorId.LogicalId, null),
                clearHelp: "Remove the nickname and show the real name"),
            new("Create collider", TablerIcon.Cube, disabled: !posable || _actorColliderCapture.Busy)
            { OnInvoke = () => CreateActorCollider(actorId, shown), Place = MenuPlace.More },
            new("Export Idle Pose…", TablerIcon.Upload, disabled: !posable || _poseFileSection.IdleExportBusy,
                help: "Bake body and expression into a Penumbra PMP")
            { OnInvoke = () => _poseFileSection.OpenIdleExport(actorId), Place = MenuPlace.More },
            Submenu("Tree", TablerIcon.Folder, MenuPlace.TreeMenu, BuildTreeSubmenu("actor:" + actorId)),
            Submenu("Bone presets", TablerIcon.Eye, MenuPlace.BonePresetsMenu,
                posable ? BuildBonePresetSubmenu(actorId) : [], disabled: !posable,
                help: "Named sets of which bones this actor shows in the overlay"),
            Submenu("Pose", TablerIcon.Walk, MenuPlace.PoseMenu, BuildActorPoseSubmenu(actorId), disabled: !posable,
                help: "The clicked actor's pose, including its equipment slots"),
        };

        // A mount, minion or ornament on an actor comes and goes with its
        // owner, so it has no Duplicate, Save or Destroy of its own.
        if (!attached)
        {
            items.Add(DuplicateItem(() => DuplicateAndSelect(id), () => DuplicateAndSelect(id, withPose: true),
                posable: posable, disabled: actor.IsCompanion));
            items.Add(SaveToLibraryItem(id, "actor", shown, disabled: !posable));
            if (_actorControl.Read(actorId)?.CanRemove == true)
                items.Add(DestroyItem(() => DestroyActor(actorId, actor.Name)));
        }

        if (_companions.ActionsFor(actorId) is { } companion)
        {
            var change = new ContextMenuItem(companion.IsAttachedChild || companion.Occupied ? "Change" : "Attach",
                TablerIcon.UserPlus, disruptive: true) { OnInvoke = () => _companions.OpenAttachPicker(actorId) };
            var detach = new ContextMenuItem("Detach", TablerIcon.UserMinus, disruptive: true)
            { OnInvoke = () => _companions.Detach(actorId) };
            items.Add(companion.IsAttachedChild
                ? Submenu("Attachment", TablerIcon.Paw, MenuPlace.CompanionMenu, [change, detach],
                    help: "Change or detach this body through its owner")
                : Submenu("Companion", TablerIcon.Paw, MenuPlace.CompanionMenu,
                    companion.Occupied ? [change, detach] : [change],
                    help: "Attach, change or detach a minion, mount or ornament"));
        }

        DrawMenu("##actor-ctx", ref _ctxOpenRequested, items, id);
    }

    private async void DestroyActor(ActorId actorId, string rawName)
    {
        var id = SelectionId.ForActor(actorId);
        if (_scene.Snapshot.FindActor(actorId)?.IsAdopted == true)
        {
            await RemoveEntitiesSafely([id]);
            return;
        }
        var removed = await RemoveEntitiesSafely([id]);
        if (removed is null)
            return;
        string name = ActorNames.Clean(rawName);
        if (removed == 1)
        {
            // Deselect the actor with its bones and bone groups, and nothing else.
            _selection.RemoveActorLineage(actorId.LogicalId);
            _notices.Done($"Destroyed '{name}'.");
        }
        else
            _notices.Report(Domain.Outcome.Fail($"'{name}' could not be destroyed."), "Destroy");
    }

    private async void CreateActorCollider(ActorId actorId, string name)
    {
        try
        {
            var result = await _actorColliderCapture.CreateAsync(actorId, name);
            var group = result.Group;
            _selection.Select(group.Members[0]);
            foreach (var member in group.Members.Skip(1)) _selection.Add(member);
            _groups.ActiveGroupId = group.Id;
            if (result.Warning != null) _notices.Note(result.Warning);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Actor collider capture failed");
            _notices.Report(Domain.Outcome.Fail(ex.Message), "Create collider", refusal: true);
        }
    }

    private ContextMenuItem[] BuildBonePresetSubmenu(ActorId actorId)
    {
        if (_scene.Snapshot.FindActor(actorId.LogicalId) is not { } actor)
            return [];
        _presetActorId = actorId;
        var presets = _bonePresets.Presets;
        var items = new List<ContextMenuItem>();
        if (presets.Count == 0)
            items.Add(new("No presets yet", TablerIcon.Circle, disabled: true,
                help: "Show the bones you want, then save them as a preset"));
        foreach (var preset in presets)
        {
            var name = preset.Name;
            items.Add(new(name, _bonePresets.IsApplied(actor, name) ? TablerIcon.CircleDot : TablerIcon.Circle,
                keepOpen: true, help: $"{preset.Bones.Count} bones")
            { OnInvoke = () => _bonePresets.Toggle(actor, name) });
        }
        items.Add(ContextMenuItem.Separator);
        items.Add(new("Toggle other", TablerIcon.Crosshair, disabled: presets.Count == 0,
            help: "Hide everything the presets claim and show the rest")
        { OnInvoke = () => _bonePresets.ToggleOther(actor) });
        items.Add(new("Hide every bone", TablerIcon.EyeOff, help: "Take this actor's overlay back to nothing")
        { OnInvoke = () => _bonePresets.Clear(actor) });
        items.Add(ContextMenuItem.Separator);
        items.Add(new("Manage presets", TablerIcon.Edit, help: "Save what this actor shows as a new preset, or delete one")
        {
            OnInvoke = () =>
            {
                _presetNameValue = string.Empty;
                _presetSaveNote = null;
                _presetManagerOpen = true;
            },
        });
        return items.ToArray();
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
    /// Right-click bone menu for moving around the skeleton and for actions on
    /// the bone itself. It reads the skeleton from the scene list and sends
    /// only ids to the selection and pose commands.
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
        var items = new List<ContextMenuItem>
        {
            new(visible ? "Hide from overlay" : "Show in overlay", visible ? TablerIcon.EyeOff : TablerIcon.Eye)
            { OnInvoke = () => _overlayPresentation.SetVisible(overlayBones, !visible), Place = MenuPlace.Visibility },
            Submenu("Select", TablerIcon.Crosshair, MenuPlace.SelectMenu,
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
            Submenu("Reset", TablerIcon.Refresh, MenuPlace.ResetMenu,
            [
                new("Bone", TablerIcon.Refresh, disruptive: true)
                { OnInvoke = () => _cleanPose.ResetBone(TransformTargetId.ForBone(boneId), descriptor.DisplayName) },
                new("Bone and descendants", TablerIcon.Refresh, disruptive: true, disabled: branch.Count == 1)
                { OnInvoke = () => _cleanPose.ResetBones(branch.Select(TransformTargetId.ForBone).ToArray(), "Reset bone branch") },
            ]),
            Submenu("Actor pose", TablerIcon.Walk, MenuPlace.PoseMenu, BuildActorPoseSubmenu(owner.Id),
                help: "The whole owning actor, including equipment"),
            Submenu("Bone presets", TablerIcon.Eye, MenuPlace.BonePresetsMenu, BuildBonePresetSubmenu(owner.Id)),
            Submenu("Tree", TablerIcon.Folder, MenuPlace.TreeMenu, BuildTreeSubmenu(_ctxBoneExpandKey)),
        };
        var mapTargets = _boneMap.AddToPresetActions(boneId);
        if (mapTargets.Length > 0)
            items.Add(new ContextMenuItem("Preset", TablerIcon.Edit, submenuItems: mapTargets));
        DrawMenu(menu, ref _boneCtxOpenRequested, items);
    }

    private ReferenceImageInstance? _ctxReferenceImage;

    private bool _referenceCtxOpenRequested;

    /// <summary>Reference pictures are not part of the scene's undo history.</summary>
    public void DrawReferenceImageContextMenu()
    {
        if (!_referenceCtxOpenRequested && !FloatingMenu.IsOpen("##reference-ctx")) return;
        if (_ctxReferenceImage is not { } image)
            return;
        // A picture closed from its own bar while the menu is open takes the
        // menu with it.
        if (!_referenceImages.Instances.Contains(image))
        {
            _ctxReferenceImage = null;
            FloatingMenu.Dismiss("##reference-ctx");
            return;
        }
        bool hidden = ReferenceImageSession.IsHidden(image);
        var items = new List<ContextMenuItem>
        {
            VisibilityItem(!hidden, () => _referenceImages.SetHidden(image, !hidden)),
            RenameItem("Rename reference image", image.Name, next => image.Entry.Name = next),
            DuplicateItem(() => _referenceImages.Duplicate(image)),
            new("Remove", TablerIcon.Trash, danger: true)
            { OnInvoke = () => _referenceImages.Close(image), Place = MenuPlace.Lifetime },
        };
        DrawMenu("##reference-ctx", ref _referenceCtxOpenRequested, items);
    }

    private OverlayId? _ctxOverlayNodeId;

    private bool _overlayNodeCtxOpenRequested;

    /// <summary>Menu for a chat bubble, dialogue box, status line or IK collider.</summary>
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
        var id = SelectionId.ForOverlay(overlayId);
        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            VisibilityItem(node.State.Visible, () => SetEntityVisible(id, !node.State.Visible)),
            RenameItem("Rename overlay", node.State.Name,
                next => _overlayControl.Set(overlayId, Application.Presentation.OverlayProperties.Name, next)),
            DuplicateItem(id),
            SaveToLibraryItem(id, "overlay", node.State.Name),
            DestroyItem(id),
            DestroyAllItem("overlays", _scene.Snapshot.Overlays.Count == 0, _removalDialog.ConfirmDestroyAllOverlays),
        };
        if (node.State.Collider is { } collider)
        {
            items.Add(LockItem(collider.Locked, () => _entityActions.ToggleLock(id)));
            items.Add(new(collider.Enabled ? "Disable collision" : "Enable collision", TablerIcon.Cube)
            {
                OnInvoke = () => _overlayControl.EditCollider(overlayId, c => c with { Enabled = !collider.Enabled }),
                Place = MenuPlace.Collision,
            });
        }
        DrawMenu("##overlay-node-ctx", ref _overlayNodeCtxOpenRequested, items, id);
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
        var items = new List<ContextMenuItem>
        {
            new(visible ? "Hide from overlay" : "Show in overlay", visible ? TablerIcon.EyeOff : TablerIcon.Eye)
            {
                OnInvoke = () =>
                {
                    if (_ctxOverlayMemoryKey is { } memoryKey)
                        _overlayPresentation.ToggleVisibleWithMemory(memoryKey, bones);
                    else _overlayPresentation.SetVisible(bones, !visible);
                },
                Place = MenuPlace.Visibility,
            },
            Submenu("Select", TablerIcon.Crosshair, MenuPlace.SelectMenu,
            [
                new("Bones in " + scope, TablerIcon.SelectChildren) { OnInvoke = () => SelectBones(bones) },
                new("All actor bones", TablerIcon.SelectChildren) { OnInvoke = () => SelectBones(ownerBones) },
            ]),
            Submenu("Visibility", TablerIcon.Eye, MenuPlace.VisibilityMenu,
            [
                new("Show only " + scope, TablerIcon.Crosshair)
                { OnInvoke = () => { _overlayPresentation.SetVisible(ownerBones, false); _overlayPresentation.SetVisible(bones, true); } },
                new("Show all actor bones", TablerIcon.Eye) { OnInvoke = () => _overlayPresentation.SetVisible(ownerBones, true) },
                new("Hide all actor bones", TablerIcon.EyeOff) { OnInvoke = () => _overlayPresentation.SetVisible(ownerBones, false) },
            ]),
            Submenu("Reset", TablerIcon.Refresh, MenuPlace.ResetMenu,
            [
                new("Bones in " + scope, TablerIcon.Refresh, disruptive: true)
                { OnInvoke = () => _cleanPose.ResetBones(bones.Select(TransformTargetId.ForBone).ToArray(), "Reset " + scope) },
            ]),
            Submenu("Actor pose", TablerIcon.Walk, MenuPlace.PoseMenu, BuildActorPoseSubmenu(owner.Id),
                help: "The whole owning actor, including equipment"),
            Submenu("Bone presets", TablerIcon.Eye, MenuPlace.BonePresetsMenu, BuildBonePresetSubmenu(owner.Id)),
            Submenu("Tree", TablerIcon.Folder, MenuPlace.TreeMenu, BuildTreeSubmenu(_ctxBranchExpandKey)),
        };
        DrawMenu(menu, ref _overlayCtxOpenRequested, items);

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

    /// <summary>The name prompt every Rename and Save to library uses.</summary>
    private readonly Controls.EntityNameModal _names;
    private readonly LibrarySavePrompt _librarySave;

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
        var id = SelectionId.ForLight(lightId);
        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            new(light.IsOn ? "Switch off" : "Switch on", light.IsOn ? TablerIcon.EyeOff : TablerIcon.Eye)
            { OnInvoke = () => SetEntityVisible(id, !light.IsOn), Place = MenuPlace.Visibility },
            RenameItem("Rename light", light.Name,
                next => _lightControl.Set(lightId, Application.Presentation.LightProperties.Name, next)),
            DuplicateItem(id),
            SaveToFileItem(() => _lightPane.OpenSave(lightId)),
            SaveToLibraryItem(id, "light", light.Name),
            // A light Poser made is destroyed; one taken from the world goes back.
            light.Ownership == LightOwnership.Spawned ? DestroyItem(id) : ReleaseItem(id),
            DestroyAllItem("lights", _scene.Snapshot.Lights.Count == 0, _removalDialog.ConfirmDestroyAllLights),
        };
        DrawMenu("##light-ctx", ref _lightCtxOpenRequested, items, id);
    }

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
        var id = SelectionId.ForProp(propId);
        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            VisibilityItem(prop.Visible, () => SetEntityVisible(id, !prop.Visible)),
            RenameItem("Rename object", prop.Name,
                next => _objectControl.Set(propId, Application.Presentation.PropProperties.Name, next)),
            DuplicateItem(id),
            SaveToLibraryItem(id, "prop", prop.Name),
            DestroyItem(id),
            DestroyAllItem("objects", _scene.Snapshot.Props.Count == 0, _removalDialog.ConfirmDestroyAllProps),
        };
        DrawMenu("##prop-ctx", ref _propCtxOpenRequested, items, id);
    }

    /// <summary>The camera menu. The main camera can't be destroyed.</summary>
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
        var id = SelectionId.ForCamera(cameraId);
        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            new(camera.IsLive ? "Return to main camera" : "Look through", TablerIcon.Video,
                disabled: camera.IsLive && camera.IsDefault)
            { OnInvoke = () => _cameraControl.SetLive(cameraId, !camera.IsLive), Place = MenuPlace.View },
            LockItem(camera.IsLocked, () => _entityActions.ToggleLock(id)),
            new("Look at tracked actor", TablerIcon.Crosshair, disabled: !CanRecenterOnTracked(cameraId),
                help: "Swing the camera back onto whoever it tracks")
            { OnInvoke = () => RecenterCameraOnTrackedActor(cameraId), Place = MenuPlace.Framing },
            RenameItem("Rename camera", camera.Name,
                next => _cameraControl.Set(cameraId, Application.Presentation.CameraProperties.Name, next)),
            DuplicateItem(id),
            SaveToFileItem(() => _cameraPane.OpenSave(cameraId)),
            SaveToLibraryItem(id, "camera", camera.Name),
            Submenu("Reset", TablerIcon.Refresh, MenuPlace.ResetMenu,
            [
                new("Transform", TablerIcon.Refresh, disabled: camera.IsLocked || !camera.Available)
                { OnInvoke = () => _cameraPane.ResetCameraTransform(cameraId) },
                new("Properties", TablerIcon.Refresh, disabled: camera.IsLocked)
                { OnInvoke = () => _cameraControl.ResetProperties(cameraId) },
            ]),
            DestroyAllItem("cameras", !_scene.Snapshot.Cameras.Any(c => !c.IsDefault), _removalDialog.ConfirmDestroyAllCameras),
        };
        if (!camera.IsDefault)
            items.Add(DestroyItem(id));
        DrawMenu("##camera-ctx", ref _cameraCtxOpenRequested, items);
    }

    // ── world-object / group / selection context menus ──────────────────

    private WorldObjectId? _ctxWorldObjectId;

    private bool _worldObjectCtxOpenRequested;

    private Guid? _ctxGroupId;

    private bool _groupCtxOpenRequested;

    private bool _selectionCtxOpenRequested;

    /// <summary>The menu for scenery, furniture and effects, borrowed from the map
    /// or spawned by Poser.</summary>
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
        var id = SelectionId.ForWorldObject(worldObjectId);
        string noun = worldObject.IsFurniture ? "furniture" : "object";
        var items = new List<ContextMenuItem>
        {
            HandleItem(id),
            VisibilityItem(worldObject.Visible, () => SetEntityVisible(id, !worldObject.Visible)),
            RenameItem($"Rename {noun}", worldObject.Name,
                next => _objectControl.Set(worldObjectId, Application.Presentation.WorldObjectProperties.Name, next)),
            DuplicateItem(id),
            SaveToLibraryItem(id, noun, worldObject.Name),
            // An object Poser spawned is destroyed; one borrowed from the map
            // goes back where it stood.
            worldObject.Spawned ? DestroyItem(id) : ReleaseItem(id),
        };
        if (worldObject.IsFurniture)
        {
            var lights = worldObject.FurnitureLights;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                items.Add(new($"Light {i + 1}: {(light.Enabled ? "On" : "Off")}", TablerIcon.Bulb, keepOpen: true)
                { OnInvoke = () => _objectControl.SetFurnitureLight(worldObjectId, light.Key, !light.Enabled) });
            }
        }
        if (worldObject.IsVfx)
            items.Add(PlaybackItem(!worldObject.VfxPaused, () => _objectControl.Set(worldObjectId,
                Application.Presentation.WorldObjectProperties.VfxPaused, !worldObject.VfxPaused)));
        else
        {
            items.Add(new(worldObject.NightState ? "Day" : "Night", worldObject.NightState ? TablerIcon.Sun : TablerIcon.Moon)
            {
                OnInvoke = () => _objectControl.Set(worldObjectId,
                    Application.Presentation.WorldObjectProperties.NightState, !worldObject.NightState),
            });
            if (!worldObject.Spawned)
                items.Add(PlaybackItem(!worldObject.AnimationPaused, () => _objectControl.Set(worldObjectId,
                    Application.Presentation.WorldObjectProperties.AnimationPaused, !worldObject.AnimationPaused)));
        }
        DrawMenu("##world-object-ctx", ref _worldObjectCtxOpenRequested, items, id);
    }

    /// <summary>The group's own actions. Actions on the selected members are in
    /// the selection's menu.</summary>
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
        bool filtering = !string.IsNullOrEmpty(_sidebar.Filter);
        var members = _groups.Descendants(group).ToArray();
        var items = new List<ContextMenuItem>
        {
            HandleItem(groupId),
            RenameItem("Rename group", group.Name, next => _groupSteps.Rename(groupId, next)),
            DuplicateItem(() => DuplicateGroup(group, withPose: false), () => DuplicateGroup(group, withPose: true)),
            SaveGroupToLibraryItem(members, group.Name),
            LockItem(group.Locked, () => _entityActions.ToggleGroupLock(groupId)),
            VisibilityItem(!group.Hidden, () => _groupSteps.SetHidden(group, !group.Hidden)),
            PlaybackItem(!group.Paused, () => _groupSteps.SetPaused(group, !group.Paused)),
            new(group.Night ? "Day" : "Night", group.Night ? TablerIcon.Sun : TablerIcon.Moon)
            { OnInvoke = () => _groupSteps.SetNight(group, !group.Night) },
            new("Ungroup", TablerIcon.X) { OnInvoke = () => _groupSteps.Dissolve(groupId) },
            MoveToCameraItem(members, disabled: group.Locked || !members.Any(_placement.CanPlace)),
            Submenu("Tree", TablerIcon.Folder, MenuPlace.TreeMenu,
            [
                new("Expand", TablerIcon.SquarePlus, disabled: filtering)
                { OnInvoke = () => SetGroupTreeCollapsed(groupId, collapsed: false, subtree: false) },
                new("Collapse", TablerIcon.SquareMinus, disabled: filtering)
                { OnInvoke = () => SetGroupTreeCollapsed(groupId, collapsed: true, subtree: false) },
                new("Expand all", TablerIcon.SquarePlus, disabled: filtering)
                { OnInvoke = () => SetGroupTreeCollapsed(groupId, collapsed: false, subtree: true) },
                new("Collapse all", TablerIcon.SquareMinus, disabled: filtering)
                { OnInvoke = () => SetGroupTreeCollapsed(groupId, collapsed: true, subtree: true) },
            ]),
            // Each member is removed the way its own kind is; the empty group
            // then goes by itself.
            DestroyItem(() => _ = RemoveEntitiesSafely(group.Members.ToArray())),
        };
        DrawMenu("##group-ctx", ref _groupCtxOpenRequested, items);
    }

    /// <summary>Actions for several selected things at once. An action is offered
    /// when at least one selected thing supports it.</summary>
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

        // Hide and Pause act on the whole set at once: Hide when anything is
        // visible, Pause when anything is playing.
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
        var items = new List<ContextMenuItem>
        {
            // Only a set with an actor in it can be copied in its pose.
            DuplicateItem(() => DuplicateSelection(withPose: false),
                anyActor ? () => DuplicateSelection(withPose: true) : null),
            VisibilityItem(anyVisible, () => SetSelectionVisible(!anyVisible)),
            MoveToCameraItem(_selection.Selected),
            DestroyItem(DestroySelection),
        };
        if (anyAnimated)
            items.Add(PlaybackItem(anyRunning, () => SetSelectionPaused(anyRunning)));
        if (matched is { } selectedGroup)
        {
            items.Add(HandleItem(selectedGroup.Id));
            items.Add(SaveGroupToLibraryItem(_groups.Descendants(selectedGroup).ToArray(), selectedGroup.Name));
            items.Add(new("Ungroup", TablerIcon.X) { OnInvoke = () => _groupSteps.Dissolve(selectedGroup.Id) });
        }
        else
            items.Add(new("Group…", TablerIcon.Folder)
            {
                OnInvoke = () => OpenEntityRename("Name the group",
                    global::Poser.Domain.Scene.EntityNames.Next("Group", _groups.All.Select(x => x.Name)),
                    name => _groupSteps.Create(name, _selection.Selected)),
            });
        items.Add(new("Deselect", TablerIcon.X) { OnInvoke = () => _selection.Clear() });
        DrawMenu("##selection-ctx", ref _selectionCtxOpenRequested, items);
    }
}
