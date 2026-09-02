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

/// <summary>The tab strip, the status bar and the tab content.</summary>
public partial class MainWindow
{
    /// <summary>The title cell's subject: the library mode, else the selected
    /// entity by kind, else the plain product name. Actor names travel the
    /// masked display route like every other surface.</summary>
    /// <summary>The KIND label leading the tab band: what the content
    /// side is showing.</summary>
    private string ContentKind(SelectionId? primary)
    {
        // ALWAYS the selected object's kind — the segment names what
        // Target would show, whichever panel is active. A multiselect IS
        // its own kind: the anonymous group.
        if (global::Poser.Application.Selection.EntitySelection.IsMultiEntity(
                _selection.Selected))
            return "Selection";
        return primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.Bone
                or SceneEntityKind.GazeTarget } => "Actor",
            { Kind: SceneEntityKind.Prop or SceneEntityKind.WorldObject }
                => "Object",
            { Kind: SceneEntityKind.Camera } => "Camera",
            { Kind: SceneEntityKind.Light } => "Light",
            { Kind: SceneEntityKind.Overlay } => "Overlay",
            _ => "",
        };
    }

    /// <summary>The environment strip's label as the pane's page.
    /// Positional against <see cref="_environmentTabs"/>.</summary>
    private static EnvironmentTab EnvironmentTabFor(string tab) => tab switch
    {
        "Sky" => EnvironmentTab.Sky,
        "Atmosphere" => EnvironmentTab.Atmosphere,
        "World" => EnvironmentTab.World,
        _ => EnvironmentTab.Lighting,
    };

    private int _multiTitleCount;

    private string _multiTitle = string.Empty;

    private string TitleEntity(SelectionId? primary)
    {
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities >= 2)
        {
            // A selection that IS a named group wears the group's name.
            if (_groups.ActiveSelection(_selection.Selected) is { } group)
                return group.Name;
            if (_multiTitleCount != entities)
            {
                _multiTitleCount = entities;
                _multiTitle = $"{entities} selected";
            }
            return _multiTitle;
        }
        return primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } actorId } =>
                _scene.Snapshot.FindActor(actorId.LogicalId) is { } actor
                    ? ActorNames.Display(actor)
                    : "Poser",
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } =>
                _scene.Snapshot.FindActor(boneId.Skeleton.Actor.LogicalId) is { } owner
                    ? ActorNames.Display(owner)
                    : "Poser",
            { Kind: SceneEntityKind.Environment } => "Environment",
            { Kind: SceneEntityKind.Light } => LightTitle(primary.Value),
            // The titlebar says the THING's name — "Balloon 1", never the
            // kind label (ruled 2026-08-31).
            { Kind: SceneEntityKind.Camera } =>
                EntityTitle(primary.Value, "Camera"),
            { Kind: SceneEntityKind.Prop } =>
                EntityTitle(primary.Value, "Object"),
            { Kind: SceneEntityKind.WorldObject } =>
                EntityTitle(primary.Value, "Object"),
            { Kind: SceneEntityKind.Overlay } =>
                EntityTitle(primary.Value, "Overlay"),
            // The empty state SAYS so, in the titlebar too.
            null => "Nothing selected",
            _ => "Poser",
        };
    }

    /// <summary>The selected entity's own name from the snapshot, by kind;
    /// the kind label only when the snapshot no longer holds it.</summary>
    private string EntityTitle(SelectionId id, string fallback)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                        return camera.Name;
                break;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                        return prop.Name;
                break;
            case { Kind: SceneEntityKind.WorldObject,
                WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                        return worldObject.Name;
                break;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                        return overlay.Name;
                break;
        }
        return fallback;
    }

    private string LightTitle(SelectionId id)
    {
        foreach (var light in _scene.Snapshot.Lights)
            if (id.Light is { } lightId && light.Id.Equals(lightId))
                return light.Name;
        return "Light";
    }

    private void BuildTabs(SelectionId? primary)
    {
        // Tabs are rebuilt each frame; the active one is preserved so a
        // selection change cannot silently return to Pose.
        _vm.Tabs.Clear();
        int contentMode = _contentMode;
        if (contentMode == 1)
        {
            // The environment is big enough to earn its strip: five
            // pages, exactly the split it had as a selection.
            _activeStrip = "environment";
            bool held = false;
            for (int i = 0; i < _environmentTabs.Length; i++)
                held |= _environmentTabs[i].Label == _activeTab;
            if (!held)
                _activeTab = "Lighting";
            for (int i = 0; i < _environmentTabs.Length; i++)
            {
                _environmentTabs[i].Active =
                    _environmentTabs[i].Label == _activeTab;
                _vm.Tabs.Add(_environmentTabs[i]);
            }
            return;
        }
        if (contentMode == 2)
        {
            // The scene page is one page: no tabs, the selector's own
            // Scene segment is its identity.
            _activeStrip = "scene";
            return;
        }
        var tabs = SyncStripAndTab(primary);
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].Active = tabs[i].Label == _activeTab;
            _vm.Tabs.Add(tabs[i]);
        }
    }

    /// <summary>
    /// Resolves the strip a selection answers for and settles
    /// <see cref="_activeStrip"/> and <see cref="_activeTab"/> onto it,
    /// returning that strip's tabs. Separated from <see cref="BuildTabs"/>
    /// because a selection can also change mid-frame, from a sidebar row the
    /// shell is already drawing, and the viewport contract has to move with
    /// it (see <see cref="ResyncTabLayout"/>).
    /// </summary>
    private ShellTab[] SyncStripAndTab(SelectionId? primary)
    {
        // The ANONYMOUS GROUP first: two or more entities together answer
        // with ONE Selection page, whatever their kinds — the multiselect
        // is a group that was never created.
        if (global::Poser.Application.Selection.EntitySelection.IsMultiEntity(
                _selection.Selected))
        {
            _activeStrip = "multi";
            _activeTab = "Selection";
            return _multiselectTabs;
        }
        // NOTHING selected: no strip and no tabs — the content side says
        // so instead of showing an ownerless actor page.
        if (primary == null)
        {
            _activeStrip = "none";
            _activeTab = string.Empty;
            return [];
        }
        // The strip is a function of the selection type: the environment's
        // tabs are its own, a light's are its own, and nothing else shares
        // either — neither entity has a pose, an animation or an appearance.
        var (tabs, strip) = primary switch
        {
            { Kind: SceneEntityKind.Light } => (_lightTabs, "light"),
            { Kind: SceneEntityKind.Camera } => (_cameraTabs, "camera"),
            { Kind: SceneEntityKind.Prop } => (_propTabs, "prop"),
            { Kind: SceneEntityKind.Overlay } => (_overlayTabs, "overlay"),
            { Kind: SceneEntityKind.WorldObject } =>
                (_worldObjectTabs, "world-object"),
            // Creatures share the actor strip: their skeleton poses, their
            // battle-chara body animates, and the Appearance pane hides the
            // humanoid-only sections itself.
            _ => (_selectionTabs, "actor"),
        };
        // Same-labeled tabs on different strips are different places: the
        // strip key joins the scroll identity in ApplyTabLayout.
        _activeStrip = strip;
        // The active tab is preserved within a strip, so a selection change
        // inside the actor set cannot silently return to Pose; a
        // strip that does not carry it falls to that strip's first tab.
        bool carried = false;
        for (int i = 0; i < tabs.Length; i++)
            carried |= tabs[i].Label == _activeTab;
        if (!carried)
            _activeTab = tabs[0].Label;
        return tabs;
    }

    /// <summary>
    /// Rebuilds the tab and viewport layout after a mid-frame selection change.
    /// The second build keeps the active selection and tab contract coherent.
    /// </summary>
    private void ResyncTabLayout()
    {
        // Rebuild the tab rows and viewport contract together.
        BuildTabs(_selection.Primary);
        ApplyTabLayout(_contentMode
            switch { 1 => _activeTab, 2 => "Scene", _ => _activeTab });
    }

    /// <summary>The two mode strips. A mode is a strip like an entity type is
    /// — it has its own tabs — so it owns its own scroll identity: entering
    /// the library from an actor and from a light must land on one library,
    /// not on two with separate scroll memories.</summary>

    // ── status bar, restated only when its numbers move ─────────────────
    private int _statusActorCount = -1;

    private int _statusBones;

    private int _statusFps = -1;

    private ulong _statusRevision;

    private bool _statusPrimed;

    private ActorId? _statusBoneActor;

    private void BuildStatus(SelectionId? primary)
    {
        int actorCount = _scene.Snapshot.Actors.Count;
        if (actorCount != _statusActorCount)
        {
            _statusActorCount = actorCount;
            _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";
        }

        ActorId? statusActor = primary switch
        {
            { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId,
            // A gaze anchor counts as its owning actor, exactly like a bone.
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeOwner } => gazeOwner,
            _ => null,
        };
        // The bone total moves only with the scene's structure or with which
        // actor is selected — never with the frame.
        if (!_statusPrimed ||
            _statusRevision != _scene.Revision ||
            _statusBoneActor != statusActor)
        {
            _statusPrimed = true;
            _statusRevision = _scene.Revision;
            _statusBoneActor = statusActor;
            int bones = 0;
            if (statusActor is { } owner && _scene.Snapshot.FindActor(owner.LogicalId) is { } descriptor)
                foreach (var skeleton in descriptor.Skeletons)
                    bones += skeleton.Bones.Count;
            _statusBones = bones;
            // Restate the right-hand string with the new count.
            _statusFps = -1;
        }

        int fps = (int)MathF.Round(
            ImGui.GetIO().Framerate, MidpointRounding.AwayFromZero);
        if (fps == _statusFps)
            return;
        _statusFps = fps;
        _vm.StatusRight = _statusBones > 0
            ? $"{_statusBones} bones · {fps} fps"
            : $"{fps} fps";
    }

    /// <summary>Catalog spawns carry their spawn kind's icon; slot
    /// companions keep the paw; everything else is a person.</summary>
    private TablerIcon SidebarActorIcon(ActorDescriptor actor)
    {
        var resolved = _bindings.Resolve(actor.Id);
        var kind = resolved.Success && resolved.Value is { } live
            ? _spawnService.GetSpawnedKind(live)
            : null;
        return kind switch
        {
            CompanionKind.Companion => TablerIcon.Paw,
            CompanionKind.Mount => TablerIcon.Horse,
            CompanionKind.Ornament => TablerIcon.Diamond,
            _ => actor.IsCompanion ? TablerIcon.Paw : TablerIcon.User,
        };
    }

    // ── shell callbacks ──────────────────────────────────────────────────

    /// <summary>
    /// Steps the tab strip by <paramref name="delta"/>, wrapping. It goes
    /// through the click path rather than moving <see cref="_activeTab"/>
    /// itself: the click is what also settles the viewport contract, and a
    /// keyboard step that skipped it would render one tab through another
    /// tab's layout for a frame. Whatever the strip currently holds is what
    /// steps — the library's types in library mode, the selection's tabs
    /// otherwise.
    /// </summary>
    public void CycleTab(int delta)
    {
        int count = _vm.Tabs.Count;
        if (count == 0)
            return;
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_vm.Tabs[i].Active)
                continue;
            active = i;
            break;
        }
        OnTabClicked(((active + delta) % count + count) % count);
    }

    private void OnTabClicked(int index)
    {
        if (index < 0 || index >= _vm.Tabs.Count) return;
        var label = _vm.Tabs[index].Label;

        _activeTab = label;
        for (int i = 0; i < _vm.Tabs.Count; i++)
            _vm.Tabs[i].Active = i == index;

        // The click occurs while AppShellView is already drawing. Update the
        // viewport contract in the same callback as the content selection so
        // the remainder of this frame cannot render one tab through another
        // tab's layout path.
        ApplyTabLayout(label);
    }

    /// <summary>The (strip, tab) pair whose scroll identity
    /// <see cref="AppShellViewModel.ContentScrollId"/> currently carries; the
    /// id string is minted only when the pair moves.</summary>
    private string _scrollIdStrip = "";

    private string _scrollIdTab = "";

    private void ApplyTabLayout(string tab)
    {
        // Scroll identity is per strip and tab: one shared id
        // would carry the previous tab's scroll offset and extent into the
        // next tab's first frame, and strips reuse labels ("Light" on the
        // light strip vs the environment strip), so the label alone would
        // still share scroll memory across strips. Minted on switch only —
        // this method also runs on the warm per-frame path.
        if (!string.Equals(_scrollIdTab, tab, StringComparison.Ordinal) ||
            !string.Equals(_scrollIdStrip, _activeStrip, StringComparison.Ordinal))
        {
            _scrollIdStrip = _activeStrip;
            _scrollIdTab = tab;
            _vm.ContentScrollId =
                AppShellViewModel.ContentScrollIdFor(_activeStrip, tab);
        }
        // The library paints its own bands and rules, so it takes the
        // viewport wall to wall; Pose keeps the shell-inset fixed viewport.
        _vm.ContentFlush = tab is "Library";
        _vm.ContentOwnsViewport = tab is "Pose" or "Appearance";
        // Every environment tab is a PageForm, as the one it replaced was.
        // "Light" is deliberately shared: it is a light's whole editor and the
        // environment's lighting tab, and both are pages, so the layout answer
        // is the same either way. Which pane draws it is decided by the
        // selection in DrawTabContent, never by this label.
        // The scene workspace is a Page like the rest of them; it was the one
        // page missing from this list, so the shell was insetting it a second
        // time on top of the Page's own.
        _vm.ContentUsesPage =
            tab is "Animation" or "Appearance" or "Object" or "Light"
                or "Environment" or "Scene" or "Selection"
                or "Lighting" or "Sky" or "Atmosphere" or "World"
                or "Camera"
                or "Scene"
;
    }

    private void DrawMultiselectPage(Vector2 origin, Vector2 size)
    {
        Span<int> counts = stackalloc int[5];
        foreach (var id in _selection.Selected)
        {
            int slot = id.Kind switch
            {
                SceneEntityKind.Actor => 0,
                SceneEntityKind.Prop or SceneEntityKind.WorldObject => 1,
                SceneEntityKind.Light => 2,
                SceneEntityKind.Camera => 3,
                SceneEntityKind.Overlay => 4,
                _ => -1,
            };
            if (slot >= 0)
                counts[slot]++;
        }
        for (int i = 0; i < 5; i++)
        {
            if (_multiCounts[i] == counts[i] && _multiCountText[i] != null)
                continue;
            _multiCounts[i] = counts[i];
            _multiCountText[i] = counts[i].ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var matched = _groups.ActiveSelection(_selection.Selected);
        Crystarium.Page("multiselect-page", origin, size, page =>
        {
            // The title is STABLE: the group's name lives in the field
            // below, never in the section header — a header that renamed
            // with each keystroke changed the field's identity and threw
            // the keyboard back to the game after one character.
            page.Section(matched != null ? "Group" : "Selection", form =>
            {
                if (matched is { } named)
                    form.TextInput("Name", named.Name,
                        value => _groupSteps.Rename(named.Id, value));
                for (int i = 0; i < 5; i++)
                    if (_multiCounts[i] > 0)
                        form.ReadOnly(MultiKindLabels[i], _multiCountText[i]);
                form.Actions(string.Empty, actions =>
                {
                    if (matched is { } group)
                    {
                        actions.Button("Save to library",
                            () => OpenEntityRename(
                                "Save group to library", group.Name,
                                name => _scenePane.SaveGroupEntry(
                                    group.Members, name, AllActorsOwned(group.Members))));
                        actions.Button("Ungroup",
                            () => DissolveGroup(group.Id));
                    }
                    else
                    {
                        actions.Button("Group…",
                            () => OpenEntityRename(
                                "Name the group",
                                $"Group {_groups.All.Count + 1}",
                                name => _groupSteps.Create(
                                    name, _selection.Selected)));
                    }
                    actions.Button("Move to camera", MoveSelectionToCamera);
                    actions.Button("Deselect", () => _selection.Clear());
                });
            }, divider: false);
        });
    }

    /// <summary>One undoable translate: the whole selection moves so its
    /// centroid lands in front of the camera, every member keeping its
    /// offset from the others.</summary>
    private void MoveSelectionToCamera()
    {
        var resolved = global::Poser.Application.Transforms.TransformTargetResolver
            .Resolve(
                _selection.Selected, _scene.Snapshot, _groups.IsLockedMember);
        if (resolved is not { } selection)
        {
            _notices.Failed("Nothing movable is selected.");
            return;
        }
        var sum = System.Numerics.Vector3.Zero;
        int counted = 0;
        foreach (var target in selection.Targets)
        {
            var pose =
                target is { Kind: TransformTargetKind.Actor, Actor: { } actor }
                    ? _viewportProjection.GetActorTransform(actor)
                    : _viewportProjection.GetModelTransform(target);
            if (pose is not { } position)
                continue;
            sum += position.Position;
            counted++;
        }
        if (counted == 0)
        {
            _notices.Failed("Nothing movable is selected.");
            return;
        }
        var centroid = sum / counted;
        var look = _gameCamera.GetLookDirection();
        if (look.LengthSquared() < 1e-6f)
            look = System.Numerics.Vector3.UnitZ;
        var goal = _gameCamera.GetCameraPosition()
            + System.Numerics.Vector3.Normalize(look) * 2.5f;
        var begin = _cleanTransforms.Begin(
            selection.Targets,
            global::Poser.Domain.Transforms.TransformOperation.Translate,
            global::Poser.Domain.Transforms.TransformSpace.World,
            description: "Move to camera");
        if (!begin.Success || begin.GestureId is not { } gestureId)
        {
            _notices.Failed(
                $"Move to camera: {begin.Detail ?? "refused"}.");
            return;
        }
        _cleanTransforms.Update(gestureId,
            new global::Poser.Domain.Transforms.TransformDelta(
                goal - centroid,
                System.Numerics.Quaternion.Identity,
                System.Numerics.Vector3.One));
        _cleanTransforms.Commit(gestureId);
    }

    private void DrawTabContent(Vector2 origin, Vector2 size)
    {
        int pageMode = _contentMode;
        if (pageMode == 1)
        {
            _environmentPane.Draw(origin, size, EnvironmentTabFor(_activeTab));
            return;
        }
        if (pageMode == 2)
        {
            // Scene recovery is browsable out of GPose; the workflow
            // itself refuses what needs a live session.
            _scenePane.DrawPage(origin, size);
            return;
        }

        if (!_gPoseService.IsGPosing)
        {
            Crystarium.TextAt(origin + new Vector2(0f, 8f) * ImGuiHelpers.GlobalScale, "Enter GPose to start posing.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            return;
        }

        ImGui.SetCursorScreenPos(origin);
        // Inspector-owned selection state drives IK and must be current even
        // when another tab owns the centre pane.
        _poseInspector.SetSelection(_selection.Primary);

        // The properties panel's empty state: one centred line.
        if (_selection.Primary == null
            && !global::Poser.Application.Selection.EntitySelection
                .IsMultiEntity(_selection.Selected))
        {
            var emptyStyle = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.LabelSize,
                Color = Crystarium.ActiveTheme.FormHint,
            };
            var measured = Crystarium.MeasureText(
                "Nothing selected", emptyStyle);
            Crystarium.TextAt(
                origin + (size - measured) * 0.5f,
                "Nothing selected", emptyStyle);
            return;
        }

        if (_activeTab == "Selection")
        {
            DrawMultiselectPage(origin, size);
            return;
        }
        if (_activeTab == "Animation")
        {
            _animationCatalog.EnsureLoaded();
            _animationPane.Draw(origin, size);
            return;
        }

        if (_activeTab == "Appearance")
        {
            _appearancePane.Draw(origin, size);
            return;
        }

        // The overlay tab stands only while an overlay is selected — the label
        // is unique across every strip, so it is the whole dispatch.
        if (_activeTab == "Overlay")
        {
            _overlayPane.Draw(origin, size);
            return;
        }

        // Both kinds of object name the same tab, because they share one
        // word for them. Which pane it opens is the selection's answer, never
        // the label's — the same rule "Light" already lives under.
        if (_activeTab == "Object")
        {
            if (_selection.Primary is { Kind: SceneEntityKind.WorldObject })
                _worldObjectsPane.Draw(origin, size);
            else
                _propsPane.Draw(origin, size);
            return;
        }

        // The three light tabs only ever stand while a light is selected: the
        // strip that carries them is chosen by the selection kind, and a strip
        // that does not carry the active label drops back to its own first tab.
        if (_activeTab == "Light")
        {
            _lightPane.DrawLight(origin, size);
            return;
        }

        // The camera tab stands only while a camera is selected — the label
        // is unique across every strip, so it is the whole dispatch, exactly
        // like the light's.
        if (_activeTab == "Camera")
        {
            _cameraPane.DrawCamera(origin, size);
            return;
        }

        _poseInspector.Draw(origin, size);
    }
}
