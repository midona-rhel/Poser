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
internal sealed partial class SidebarComposer
{
    private static readonly bool[] RootTreeLines = Array.Empty<bool>();
    private readonly AppShellViewModel _vm;
    private readonly Config.ConfigurationService _configuration;
    private readonly SceneSession _scene;
    private readonly SceneGroups _groups;
    private readonly SelectionSession _selection;
    private readonly Application.Animation.IAnimationPlayback _animation;
    private readonly Application.Gaze.IGazeControl _gazeService;
    private readonly Application.Presentation.ICameraControl _cameraControl;
    private readonly Application.Presentation.IOverlayControl _overlayControl;
    private readonly Application.Presentation.ILightControl _lightControl;
    private readonly Application.Presentation.ISceneObjectControl _objectControl;
    private readonly Application.Viewport.IViewportReads _viewportProjection;
    private readonly SelectionEntityCommands _entityCommands;
    private readonly ReferenceImageSession _referenceImages;
    private readonly Func<ActorDescriptor, TablerIcon> _actorIcon;
    private readonly ShellSidebarSection _sceneSection = new() { Title = "" };
    private readonly HashSet<string> _collapsedNodes = new();
    private readonly HashSet<string> _knownCategoryNodes = new();
    private readonly HashSet<string> _knownActorNodes = new();
    private readonly List<ActorRowState> _actorRows = new();

    private readonly record struct ActorRowState(
        ShellSidebarRow Row,
        ActorId Id,
        string RawName,
        bool SnapshotHidden);

    // ── sidebar rebuild gate (see BuildSidebar) ─────────────────────────
    private bool _sidebarBuilt;

    private ulong _sidebarRevision;

    private string _sidebarFilter = "";

    private int _sidebarExpandVersion = -1;

    /// <summary>A gaze mode transition landed since the last rebuild. Gaze mode
    /// is not part of the scene revision and cannot be, so the one row it owns
    /// needs its own arming bit. Written from the gaze service's publishing
    /// thread — volatile, and nothing but the bit is touched there.</summary>
    private volatile bool _gazeDirty;

    /// <summary>Bumped by every disclosure toggle. The gate cannot observe
    /// <see cref="_collapsedNodes"/> directly — a set carries no version — and
    /// disclosure is the one non-scene input that changes the row count.
    /// </summary>
    private int _expandVersion;

    private int _sidebarGroupsRevision = -1;


    public string Filter => _sidebarFilter;
    public void InvalidateGaze() => _gazeDirty = true;
    public void ToggleDisclosure(string key)
    {
        _expandVersion++;
        if (!_collapsedNodes.Add(key)) _collapsedNodes.Remove(key);
    }

    public SidebarComposer(
        AppShellViewModel vm,
        Config.ConfigurationService configuration,
        SceneSession scene,
        SceneGroups groups,
        SelectionSession selection,
        Application.Animation.IAnimationPlayback animation,
        Application.Gaze.IGazeControl gazeService,
        Application.Presentation.ICameraControl cameraControl,
        Application.Presentation.IOverlayControl overlayControl,
        Application.Presentation.ILightControl lightControl,
        Application.Presentation.ISceneObjectControl objectControl,
        Application.Viewport.IViewportReads viewportProjection,
        SelectionEntityCommands entityCommands,
        ReferenceImageSession referenceImages,
        Func<ActorDescriptor, TablerIcon> actorIcon)
    {
        _vm = vm;
        _configuration = configuration;
        _scene = scene;
        _groups = groups;
        _selection = selection;
        _animation = animation;
        _gazeService = gazeService;
        _cameraControl = cameraControl;
        _overlayControl = overlayControl;
        _lightControl = lightControl;
        _objectControl = objectControl;
        _viewportProjection = viewportProjection;
        _entityCommands = entityCommands;
        _referenceImages = referenceImages;
        _actorIcon = actorIcon;
    }

    /// <summary>Folds or opens the tree: the root row alone, or the root
    /// and everything keyed beneath it.</summary>
    public void SetTreeCollapsed(string root, bool collapsed, bool subtree)
    {
        _expandVersion++;
        void Set(string key)
        {
            if (collapsed)
                _collapsedNodes.Add(key);
            else
                _collapsedNodes.Remove(key);
        }
        Set(root);
        if (!subtree)
            return;
        foreach (var key in _subtreeDisclosure.Keys.ToArray())
            if (key == root || key.StartsWith(root + "/", StringComparison.Ordinal))
                _subtreeDisclosure.Remove(key);
        _subtreeDisclosure[root] = collapsed;
        IEnumerable<string> keys = _knownActorNodes.Concat(_knownCategoryNodes);
        foreach (var key in keys.ToArray())
            if (key.StartsWith(root + "/", StringComparison.Ordinal))
                Set(key);
        // Attached actors are visual children but keep independent actor keys.
        if (_scene.Snapshot.Actors.FirstOrDefault(actor => "actor:" + actor.Id == root) is { } owner)
            foreach (var child in _scene.Snapshot.Actors)
                if (IsOwnedBy(child, owner))
                    SetTreeCollapsed("actor:" + child.Id, collapsed, true);
    }

    private readonly Dictionary<string, bool> _subtreeDisclosure = new();

    private void SeedTreeNode(string key, HashSet<string> known)
    {
        if (!known.Add(key)) return;
        // Children are built lazily. Expand all must also reach branches
        // that have never been drawn, with the closest branch command winning.
        bool collapsed = true;
        int closest = -1;
        foreach (var (root, value) in _subtreeDisclosure)
            if (root.Length > closest && (key == root || key.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                collapsed = value;
                closest = root.Length;
            }
        if (collapsed) _collapsedNodes.Add(key);
    }



    /// <summary>
    /// Restates the sidebar. The row tree is assembled only when the gate below
    /// flips; every other frame walks the retained rows and refreshes the flags
    /// that read live state, allocating nothing.
    ///
    /// <para>The gate is exactly the inputs that can change the row count or
    /// order: the published scene revision (the structural signature — actor
    /// set and generations, slot presence, bone counts), the search filter, and
    /// the disclosure version. Selection, actor visibility, pause state and
    /// library mode are per-row flags: they are refreshed in place, so they
    /// still land on the frame they change. A display name is a flag too,
    /// except while filtering, where it can change what matches — that case
    /// re-arms the gate.</para>
    /// </summary>
    public void Build()
    {
        // Trim hands back the same instance when there is nothing to trim, so
        // the common (unfiltered) frame builds no string here.
        string filter = _vm.SidebarSearch.Trim();
        if (!_sidebarBuilt ||
            _gazeDirty ||
            _sidebarRevision != _scene.Revision ||
            _sidebarGroupsRevision != _groups.Revision ||
            _sidebarExpandVersion != _expandVersion ||
            !string.Equals(_sidebarFilter, filter, StringComparison.Ordinal))
        {
            _sidebarBuilt = true;
            // Cleared before the walk, so a transition that lands mid-rebuild
            // re-arms rather than being swallowed by the rebuild it raced.
            _gazeDirty = false;
            _sidebarRevision = _scene.Revision;
            _sidebarGroupsRevision = _groups.Revision;
            _sidebarExpandVersion = _expandVersion;
            _sidebarFilter = filter;
            RebuildSidebar(filter);
        }

        RefreshSidebarFlags();
    }

    /// <summary>The gaze node's three aim points, in the order the gaze pane
    /// itself lists them. Static because the set is fixed: a gaze always has
    /// exactly these three parts, so no actor mints its own copy.</summary>
    private static readonly (string Label, string Icon, GazePart Part)[] GazeParts =
    {
        ("Eyes", "eye", GazePart.Eyes),
        ("Head", "head", GazePart.Head),
        ("Body", "body", GazePart.Body),
    };

    /// <summary>
    /// The cold path: the whole actor/bone tree. Everything here is discarded
    /// and restated wholesale, so it runs only behind
    /// <see cref="BuildSidebar"/>'s gate.
    /// </summary>
    private void RebuildSidebar(string filter)
    {
        _vm.Sections.Clear();
        // The sidebar is the OUTLINER — world things only, ONE list. The
        // library, the scene, and the environment left it in the
        // inspector-mode redesign: the first two are inspector panels,
        // the library is its own workspace.
        _vm.Sections.Add(_sceneSection);
        _sceneSection.Rows.Clear();
        _actorRows.Clear();

        bool filtering = filter.Length > 0;
        var snapshot = _scene.Snapshot.Actors;

        var order = _groups.RootOrder;

        // The USER'S order, kinds interleaved: a group head lists as a
        // folder row with its members nested one level in; every other
        // slot renders through the same constructions its grouped twin
        // uses.
        for (int s = 0; s < order.Count; s++)
        {
            var slot = order[s];
            if (!slot.IsGroup)
            {
                if (slot.Entity is { } entityId)
                    AddRootEntityRow(entityId, snapshot, filter, filtering);
                continue;
            }
            if (_groups.Find(slot.GroupId) is not { } group)
                continue;
            AddGroupRows(group, 0, snapshot, filter, filtering);
        }

        // A reference picture is an overlay by the same test the nodes are —
        // it is laid over the game rather than into the scene — so it closes
        // the list. It is not a scene entity: it carries no SelectionId,
        // holds no seat in the order, and its Tag is the session instance
        // itself, which is what every verb below dispatches on.
        AppendReferenceImageRows(filter, filtering);
    }

    /// <summary>One root entity's row(s) at depth 0 — the kind dispatch
    /// the old per-kind walks did, driven by the root order instead. The
    /// filter applies here, per row, exactly as those walks applied
    /// it.</summary>
    private void AddRootEntityRow(
        SelectionId id,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                foreach (var actor in snapshot)
                    if (actor.Id.Equals(actorId))
                    {
                        AddActorRows(
                            _sceneSection, actor, snapshot, filter, filtering,
                            0, RootTreeLines, true);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, prop.Name))
                            _sceneSection.Rows.Add(PropRow(prop, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                    {
                        if (!filtering
                            || MatchesSidebarFilter(filter, worldObject.Name))
                            _sceneSection.Rows.Add(WorldObjectRow(worldObject, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                foreach (var light in _scene.Snapshot.Lights)
                    if (light.Id.Equals(lightId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, light.Name))
                            _sceneSection.Rows.Add(LightRow(light, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, camera.Name))
                            _sceneSection.Rows.Add(CameraRow(camera, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                    {
                        if (!filtering
                            || MatchesSidebarFilter(filter, overlay.Name))
                            _sceneSection.Rows.Add(OverlayRow(overlay, 0));
                        return;
                    }
                return;
        }
    }

    /// <summary>The mark for one overlay kind. A dialogue panel, a bubble and
    /// a status line are three different things on screen, so they are three
    /// different marks in the tree.</summary>
    private static TablerIcon OverlayIcon(
        OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Collider => TablerIcon.Cube,
        OverlayNodeKind.Balloon =>
            TablerIcon.MessageCircle,
        OverlayNodeKind.Status => TablerIcon.Star,
        _ => TablerIcon.Message,
    };

    /// <summary>The mark for one light kind, shared by the sidebar rows and
    /// the lights header's type chooser: a kind means the same thing wherever
    /// it is shown, so it is drawn from one place.</summary>
    private static TablerIcon KindIcon(LightKind kind) => kind switch
    {
        LightKind.Directional => TablerIcon.Sun,
        LightKind.Point => TablerIcon.Bulb,
        LightKind.Area => TablerIcon.LightPanel,
        _ => TablerIcon.Spotlight,
    };

    /// <summary>
    /// The reference pictures, as overlays rows. The label is the file stem,
    /// deduped: the roster mints identity per add precisely so the same sheet
    /// can be placed twice, and two rows reading "sketch" would be two rows
    /// naming nothing. The second and later occurrences carry an ordinal, so
    /// the first one keeps the plain name.
    /// </summary>
    private void AppendReferenceImageRows(string filter, bool filtering)
    {
        var images = _referenceImages.Instances;
        if (images.Count == 0)
            return;
        _referenceStemCounts.Clear();
        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            string stem = image.Name;
            _referenceStemCounts.TryGetValue(stem, out int seen);
            _referenceStemCounts[stem] = seen + 1;
            string label = seen == 0
                ? stem
                : $"{stem} ({(seen + 1).ToString(CultureInfo.InvariantCulture)})";
            // The filter reads the displayed label.
            if (filtering && !MatchesSidebarFilter(filter, label))
                continue;
            _sceneSection.Rows.Add(new ShellSidebarRow
            {
                Label = label,
                Count = "",
                Icon = TablerIcon.Photo,
                Tag = image,
                LightActions = true,
                LightOn = !ReferenceImageSession.IsHidden(image),
            });
        }
    }

    /// <summary>Scratch for the stem dedupe; a sidebar rebuild must not mint a
    /// dictionary to count names.</summary>
    private readonly Dictionary<string, int> _referenceStemCounts = new();

    /// <summary>Flips one world class's handles. The glyph's own flag is
    /// restated immediately so it lights with the click rather than on the
    /// next refresh.</summary>
    /// <summary>
    /// The warm frame's entire sidebar cost: the retained rows' live flags.
    /// Nothing is created and no string is built — a display name that really
    /// changed re-arms the rebuild gate, and only while a filter is active,
    /// where the name decides whether the row is listed at all.
    /// </summary>
    private void RefreshSidebarFlags()
    {
        // ONE walk over the one section, dispatching on the tag. Every
        // state glyph reads the live object, never the descriptor: the
        // change moves the scene signature, and waiting for the republish
        // would leave the glyph behind the click that flipped it.
        //
        // The head and its children never light together: while the
        // selection IS the group, only the head row wears the pill — the
        // one exception is actor bones, whose dual highlight is the
        // posing tree's own rule.
        var matchedGroup = _groups.ActiveSelection(_selection.Selected);
        var rows = _sceneSection.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // A reference row carries the session instance, not a selection.
            // Its eye restates the session's own answer, live.
            if (row.Tag is ReferenceImageInstance rowImage)
            {
                row.LightOn = !ReferenceImageSession.IsHidden(rowImage);
                continue;
            }
            if (row.Tag is GroupRowTag tag)
            {
                row.Active = matchedGroup?.Id == tag.Id;
                continue;
            }
            // Category rows carry a string tag and own no selection state.
            if (row.Tag is not SelectionId id)
                continue;
            row.Active = row.GroupMember
                ? matchedGroup == null && _selection.IsSelected(id)
                : _selection.IsSelected(id);
            if (id.Camera is { } rowCameraId &&
                _cameraControl.Read(rowCameraId) is { } liveCamera)
            {
                row.CameraLive = liveCamera.IsLive;
                row.CameraLocked = liveCamera.IsLocked;
                // The seat retargets tracking onto the SELECTED actor —
                // it has work exactly when an actor is selected.
                row.CameraCanRecenter = _selection.Selected.Any(id => id.Kind == SceneEntityKind.Actor);
            }
            else if (id.Overlay is { } overlayId &&
                _overlayControl.Read(overlayId) is { } liveOverlay)
            {
                row.LightOn = liveOverlay.State.Visible;
                row.CollisionEnabled = liveOverlay.State.Collider?.Enabled ?? false;
                row.ColliderLocked = liveOverlay.State.Collider?.Locked ?? false;
            }
            else if (id.Prop is { } propId &&
                _objectControl.Read(propId) is { } prop)
                row.LightOn = prop.Visible;
            else if (id.WorldObject is { } borrowedId &&
                _objectControl.Read(borrowedId) is { } borrowed)
                row.LightOn = borrowed.Visible;
            else if (id.Light is { } lightId &&
                _lightControl.Read(lightId) is { } light)
                row.LightOn = light.IsOn;
        }

        // The game's target, once per frame: its row's crosshair stands at
        // full opacity while every other actor's fades — the live camera's
        // treatment.
        Guid? targetLineage = _viewportProjection.GameTarget?.LogicalId;
        for (int a = 0; a < _actorRows.Count; a++)
        {
            var state = _actorRows[a];
            var row = state.Row;
            row.ActorVisible = _entityCommands.ReadVisibility(SelectionId.ForActor(state.Id)) ?? !state.SnapshotHidden;
            // Pause offers while ANYTHING moves; Resume otherwise —
            // pause stops the entire stack, play overrides every
            // individual hold (ruled 2026-09-01).
            row.ActorPaused = !_animation.AnyPlaying(state.Id);
            row.ActorTargeted = targetLineage == state.Id.LogicalId;

            string label = ActorNames.Display(_configuration, state.Id, state.RawName);
            if (string.Equals(label, row.Label, StringComparison.Ordinal))
                continue;
            row.Label = label;
            // A rename can change what the filter matches, so the row set has
            // to be derived again; unfiltered, the new label is the whole
            // change and the row already carries it.
            if (_sidebarFilter.Length > 0)
                _sidebarBuilt = false;
        }
    }

    private static bool ContainsActor(
        IReadOnlyList<ActorDescriptor> snapshot,
        ActorId id)
    {
        foreach (var actor in snapshot)
            if (actor.Id.Equals(id))
                return true;
        return false;
    }

    private static bool IsOwnedBy(ActorDescriptor candidate, ActorDescriptor owner)
        => candidate.IsCompanion
            && candidate.OwnerActor is { } link
            && link.Equals(owner.Id)
            && !candidate.Id.Equals(owner.Id);

    /// <summary>Trunk flags for the children of a row: the row's own ancestor
    /// flags plus one for the row itself, set when siblings still follow it.</summary>
    private static bool[] Descend(bool[] lines, bool isLast)
    {
        var descended = new bool[lines.Length + 1];
        Array.Copy(lines, descended, lines.Length);
        descended[lines.Length] = !isLast;
        return descended;
    }


    /// <summary>One exact bone in the flat tracking picker.</summary>

    /// <summary>
    /// One actor's subtree: owned companions first, then bone categories, then
    /// auxiliary slots. Depth and trunk flags are inherited, so an attached
    /// companion draws the same tree one level in and keeps its own subtree.
    /// </summary>
    private ShellSidebarRow PropRow(PropDescriptor prop, int depth) => new()
    {
        Label = prop.Name,
        Draggable = true,
        Count = "",
        Icon = TablerIcon.Moneybag,
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForProp(prop.Id),
        LightActions = true,
        LightOn = prop.Visible,
    };

    private ShellSidebarRow WorldObjectRow(
        WorldObjectDescriptor worldObject, int depth)
    {
        bool isVfx = worldObject.Path.EndsWith(
            ".avfx", StringComparison.OrdinalIgnoreCase);
        bool isFurniture = worldObject.Path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase);
        return new ShellSidebarRow
        {
            Label = worldObject.Name,
            Draggable = true,
            Count = "",
            // World objects wear the plant row mark; a VFX burns instead.
            Icon = isVfx ? TablerIcon.Fire : isFurniture ? TablerIcon.Couch : TablerIcon.Plant,
            Depth = depth,
            ForceIcon = depth > 0,
            Tag = SelectionId.ForWorldObject(worldObject.Id),
            LightActions = true,
            LightOn = worldObject.Visible,
            // Effects play and pause; scenery switches day and night
            // (its animation pause, borrowed scenery only, lives on the
            // properties page).
            PauseAction = isVfx,
            Paused = worldObject.VfxPaused,
            NightAction = !isVfx && !isFurniture,
            Night = worldObject.Night,
        };
    }

    private ShellSidebarRow LightRow(LightDescriptor light, int depth) => new()
    {
        Label = light.Name,
        // A bone-attached light rides its bone — its place is not the
        // user's to move.
        Draggable = light.AttachedBone == null,
        Count = "",
        Icon = light.Ownership == LightOwnership.GPose
            ? TablerIcon.Camera : KindIcon(light.Kind),
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForLight(light.Id),
        LightActions = true,
        LightOn = light.IsOn,
    };

    private ShellSidebarRow CameraRow(CameraDescriptor camera, int depth) => new()
    {
        Label = camera.Name,
        Draggable = true,
        CameraMark = camera.IsDefault
            ? "M"
            : camera.Kind == CameraKind.Free ? "F" : "C",
        Icon = camera.Kind == CameraKind.Free
            ? TablerIcon.Video
            : TablerIcon.Camera,
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForCamera(camera.Id),
        CameraActions = true,
        CameraLive = camera.IsLive,
        CameraLocked = camera.IsLocked,
    };

    private ShellSidebarRow OverlayRow(
        OverlayDescriptor overlay, int depth) => new()
    {
        Label = overlay.Name,
        Draggable = true,
        Count = "",
        Icon = OverlayIcon(overlay.Kind),
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForOverlay(overlay.Id),
        LightActions = true,
        LightOn = overlay.Visible,
        ColliderActions = overlay.Kind == OverlayNodeKind.Collider,
        CollisionEnabled = _overlayControl.Read(overlay.Id)?.State.Collider?.Enabled ?? false,
        ColliderLocked = _overlayControl.Read(overlay.Id)?.State.Collider?.Locked ?? false,
    };

    /// <summary>One grouped member's row(s), nested one level in — the
    /// SAME constructions the kind walks use, so a grouped row never
    /// drifts from its ungrouped twin.</summary>
    /// <summary>A group head at <paramref name="depth"/>, its members one
    /// level in, then its subgroups the same way — to
    /// <see cref="global::Poser.Application.Scene.SceneGroups.MaxDepth"/>.</summary>
    private void AddGroupRows(
        global::Poser.Application.Scene.SceneGroup group,
        int depth,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        bool[]? lines = null,
        bool isLast = true)
    {
        string key = "group:" + group.Id;
        bool expanded = filtering || !_collapsedNodes.Contains(key);
        bool locked = _groups.IsLocked(group);
        lines ??= RootTreeLines;
        _sceneSection.Rows.Add(new ShellSidebarRow
        {
            Label = group.Name,
            Icon = TablerIcon.Folder,
            ForceIcon = true,
            Draggable = !locked,
            DropContainer = !locked,
            GroupActions = true,
            GroupLocked = group.Locked,
            GroupHidden = group.Hidden,
            GroupPaused = group.Paused,
            HasChildren = group.ItemCount > 0,
            Depth = depth,
            IsLastChild = isLast,
            TreeLines = lines,
            ExpandKey = key,
            Expanded = expanded,
            Tag = new GroupRowTag(group.Id),
        });
        if (!expanded)
            return;
        // The branch lines below this head: a trunk continues at this
        // level while a later sibling follows the group.
        // Index k of the lines is level k; a root head's children still
        // descend one level (index 0 is the root and draws nothing), or
        // every trunk below sits one level too far left.
        var childLines = Descend(lines, isLast);
        int memberStart = _sceneSection.Rows.Count;
        for (int m = 0; m < group.Members.Count; m++)
            AddGroupMemberRow(
                group.Members[m], snapshot, filter, filtering,
                isLast: m == group.Members.Count - 1 && group.Children.Count == 0,
                depth: depth + 1,
                lines: childLines);
        for (int r = memberStart; r < _sceneSection.Rows.Count; r++)
        {
            _sceneSection.Rows[r].GroupMember = true;
            if (locked)
                _sceneSection.Rows[r].Draggable = false;
        }
        for (int c = 0; c < group.Children.Count; c++)
            if (_groups.Find(group.Children[c]) is { } child)
                AddGroupRows(
                    child, depth + 1, snapshot, filter, filtering,
                    childLines, isLast: c == group.Children.Count - 1);
    }

    private void AddGroupMemberRow(
        SelectionId member,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        bool isLast,
        int depth = 1,
        bool[]? lines = null)
    {
        lines ??= RootTreeLines;
        switch (member)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                foreach (var actor in snapshot)
                    if (actor.Id.Equals(actorId))
                    {
                        AddActorRows(
                            _sceneSection, actor, snapshot, filter,
                            filtering, depth, lines, isLast);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                    {
                        var row = PropRow(prop, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                    {
                        var row = WorldObjectRow(worldObject, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                foreach (var light in _scene.Snapshot.Lights)
                    if (light.Id.Equals(lightId))
                    {
                        var row = LightRow(light, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                    {
                        var row = CameraRow(camera, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                    {
                        var row = OverlayRow(overlay, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
        }
    }


    private void AddActorRows(
        ShellSidebarSection section,
        ActorDescriptor actor,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        int depth,
        bool[] lines,
        bool isLast)
    {
        // Generation is part of the disclosure identity: a replacement actor
        // must not inherit the old generation's expanded/collapsed state.
        var actorKey = "actor:" + actor.Id;
        // The snapshot's raw name is fixed until the next revision, so the
        // object-index strip runs here and the warm-frame label refresh is
        // a pair of dictionary lookups.
        string rawName = ActorNames.Clean(actor.Name);
        string actorLabel = ActorNames.Display(_configuration, actor);

        List<ActorDescriptor>? companions = null;
        foreach (var candidate in snapshot)
        {
            if (IsOwnedBy(candidate, actor))
                (companions ??= new List<ActorDescriptor>()).Add(candidate);
        }

        var groups = new List<(Core.BoneInfo.BoneCategory Cat, List<BoneDescriptor> Bones)>();
        var skeleton = actor.CharacterSkeleton;
        if (skeleton != null)
        {
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone)) continue;
                var cat = Core.BoneInfo.BoneInfoService.GetCategory(bone.Id.CanonicalName);
                var slot = groups.FindIndex(g => g.Cat == cat);
                if (slot < 0) { groups.Add((cat, new List<BoneDescriptor>())); slot = groups.Count - 1; }
                groups[slot].Bones.Add(bone);
            }
            groups.Sort((a, b) => ((int)a.Cat).CompareTo((int)b.Cat));
        }

        // Present auxiliary slots become one additional group each under
        // the same actor row (slots are never separate actors).
        var auxSkeletons = actor.Skeletons
            .Where(s => s.Id.Slot != Domain.Identity.PoseSlot.Character)
            .OrderBy(s => (int)s.Id.Slot)
            .ToList();

        bool actorMatches = MatchesSidebarFilter(filter, actorLabel, actor.Name);
        // Category labels match the rows emitted below.
        bool hasMatchingBone = groups.Exists(group =>
            group.Bones.Exists(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)))
            || (groups.Count > 0 && KtisisCategoryLabelMatches(filter));
        bool hasMatchingAux = auxSkeletons.Exists(aux =>
            MatchesSidebarFilter(filter, SlotLabel(aux.Id.Slot))
            || aux.Bones.Any(bone => !bone.IsHidden && !IsBoneSuppressed(bone) &&
                MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)));
        var shownCompanions = companions;
        if (filtering && companions != null)
            shownCompanions = companions.FindAll(
                companion => ActorSubtreeMatches(companion, snapshot, filter));
        if (filtering && !actorMatches && !hasMatchingBone && !hasMatchingAux
            && (shownCompanions == null || shownCompanions.Count == 0))
            return;
        bool companionsFollow = shownCompanions is { Count: > 0 };
        bool categoriesFollow = skeleton != null && (!filtering || hasMatchingBone);
        bool auxFollows = auxSkeletons.Count > 0 && (!filtering || hasMatchingAux);

        // Actor roots first appear collapsed; lineage keys survive
        // refreshes, so a scene refresh cannot reset existing disclosure.
        // Only explicit disclosure clicks expand — external bone selection
        // (map, matrix, overlay, gizmo) never changes tree disclosure.
        SeedTreeNode(actorKey, _knownActorNodes);
        bool expanded = filtering || !_collapsedNodes.Contains(actorKey);
        var actorSelectionId = SelectionId.ForActor(actor.Id);
        var actorRow = new ShellSidebarRow
        {
            Label = actorLabel,
            Count = "",
            Icon = _actorIcon(actor),
            Depth = depth,
            ForceIcon = depth > 0,
            // An unresolved skeleton disables disclosure only when no
            // attached body or auxiliary slot still needs this parent row.
            HasChildren = true,
            ExpanderDisabled = skeleton == null && !companionsFollow && !auxFollows,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Tag = actorSelectionId,
            ExpandKey = actorKey,
            ActorActions = true,
            // An attached companion rides its owner — not the user's to
            // move while attached.
            Draggable = actor.OwnerActor == null,
        };
        section.Rows.Add(actorRow);
        // Selection, visibility, pause and the display name are stated by
        // the flag refresh — including for this frame.
        _actorRows.Add(new ActorRowState(
            actorRow, actor.Id, rawName, actor.IsHidden));
        if (!expanded)
            return;

        var childLines = Descend(lines, isLast);

        // A fixed-position gaze anchor is an actor child and is shown only
        // while its actor binding resolves.
        if (_gazeService.Read(actor.Id) is { Settings.Mode: GazeTargetMode.Position })
        {
            bool gazeLast = !companionsFollow && !categoriesFollow && !auxFollows;
            // Gaze rows start expanded; explicit disclosure clicks persist in
            // the same collapsed-node set as other hierarchy rows.
            var gazeKey = actorKey + "/gaze";
            bool gazeExpanded = filtering || !_collapsedNodes.Contains(gazeKey);
            section.Rows.Add(new ShellSidebarRow
            {
                Label = "Gaze control",
                Count = "",
                Depth = depth + 1,
                IconName = "eye",
                ForceIcon = true,
                // Like a merged category/bone row: the body still selects
                // the shared anchor (Tag) while the chevron toggles the
                // string key (ExpandKey).
                HasChildren = true,
                Expanded = gazeExpanded,
                IsLastChild = gazeLast,
                TreeLines = childLines,
                Tag = SelectionId.ForGazeTarget(actor.Id),
                ExpandKey = gazeKey,
            });
            // The gaze is three points, not one: eyes, head and body each
            // carry their own target, and each is separately selectable so
            // the world gizmo can grab one part alone.
            if (gazeExpanded)
            {
                var partLines = Descend(childLines, gazeLast);
                for (int p = 0; p < GazeParts.Length; p++)
                {
                    var (partLabel, partIcon, part) = GazeParts[p];
                    var partId = SelectionId.ForGazeTarget(actor.Id, part);
                    section.Rows.Add(new ShellSidebarRow
                    {
                        Label = partLabel,
                        Count = "",
                        Depth = depth + 2,
                        IconName = partIcon,
                        ForceIcon = true,
                        HasChildren = false,
                        IsLastChild = p == GazeParts.Length - 1,
                        TreeLines = partLines,
                        Active = _selection.IsSelected(partId),
                        Tag = partId,
                    });
                }
            }
        }

        // Attached companions lead the subtree: they are actors, and actors
        // read before the owner's own bones.
        if (shownCompanions != null)
        {
            for (int c = 0; c < shownCompanions.Count; c++)
                AddActorRows(
                    section, shownCompanions[c], snapshot, filter, filtering,
                    depth + 1, childLines,
                    c == shownCompanions.Count - 1
                        && !categoriesFollow && !auxFollows);
        }

        // The actor expands into nested bone categories; unclaimed bones use
        // the Other group.
        if (categoriesFollow)
        {
            // Preserve the skeleton enumeration order within each category.
            var byName = new Dictionary<string, (BoneDescriptor Bone, int Ordinal)>(
                StringComparer.Ordinal);
            int ordinal = 0;
            foreach (var (_, bones) in groups)
                foreach (var bone in bones)
                    byName[bone.Id.CanonicalName] = (bone, ordinal++);

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var built = new List<BuiltCategory>();
            foreach (var rootCategory in Core.BoneInfo.KtisisBoneCategories.Roots)
                if (BuildKtisisCategory(
                        rootCategory, byName, claimed, filter, filtering)
                    is { } presentRoot)
                    built.Add(presentRoot);

            // Whatever the tree left unclaimed — modded bones outside the
            // Unclaimed schema bones keep a home.
            var leftovers = new List<BoneDescriptor>();
            foreach (var (bone, _) in byName.Values)
                if (!claimed.Contains(bone.Id.CanonicalName)
                    && (!filtering || MatchesSidebarFilter(
                        filter, bone.DisplayName, bone.Id.CanonicalName)))
                    leftovers.Add(bone);
            if (leftovers.Count > 0)
                built.Add(new BuiltCategory(
                    "Other", "Other", leftovers, leftovers, []));

            // One skeleton row hosts the categories and their overlay state.
            if (built.Count > 0)
            {
                var skeletonKey = actorKey + "/skeleton";
                // The skeleton starts folded like the actor above it;
                // only a disclosure click, or the tree verbs, open it.
                SeedTreeNode(skeletonKey, _knownCategoryNodes);
                bool skeletonExpanded =
                    filtering || !_collapsedNodes.Contains(skeletonKey);
                bool skeletonLast = !auxFollows;
                var abdomen = ResolveCharacterRootBone(skeleton!.Bones);
                var allBoneIds = new BoneId[byName.Count];
                int i = 0;
                foreach (var (bone, _) in byName.Values)
                    allBoneIds[i++] = bone.Id;
                section.Rows.Add(new ShellSidebarRow
                {
                    Label = "Skeleton",
                    Count = "",
                    Icon = TablerIcon.Walk,
                    ForceIcon = true,
                    Depth = depth + 1,
                    HasChildren = true,
                    Expanded = skeletonExpanded,
                    IsLastChild = skeletonLast,
                    TreeLines = childLines,
                    Active = abdomen != null
                        && _selection.IsSelected(
                            SelectionId.ForBone(abdomen.Id)),
                    Tag = abdomen is { } rootBone
                        ? SelectionId.ForBone(rootBone.Id)
                        : null,
                    ExpandKey = skeletonKey,
                    SkeletonContext = skeleton!.Id,
                    OverlayMemoryKey = skeletonKey,
                    OverlayBones = allBoneIds,
                });
                if (skeletonExpanded)
                {
                    var categoryLines = Descend(childLines, skeletonLast);
                    for (int g = 0; g < built.Count; g++)
                        EmitKtisisCategory(
                            section, built[g], skeletonKey, depth + 2,
                            categoryLines,
                            g == built.Count - 1, filtering);
                }
            }
        }

        if (!filtering || hasMatchingAux)
            AddAuxiliarySlotGroups(
                section, actorKey, auxSkeletons, filter, filtering,
                depth + 1, childLines);
    }

    /// <summary>Every category label, flattened once, for the filter
    /// oracle: a query naming any category keeps the actor visible.</summary>
    private static string[]? _ktisisLabels;

    /// <summary>Whether an actor, any of its bones or slots, or any actor
    /// attached to it satisfies the sidebar filter.</summary>
    private bool ActorSubtreeMatches(
        ActorDescriptor actor,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter)
    {
        if (MatchesSidebarFilter(filter, ActorNames.Display(_configuration, actor), actor.Name))
            return true;

        foreach (var skeleton in actor.Skeletons)
        {
            bool character = skeleton.Id.Slot == Domain.Identity.PoseSlot.Character;
            if (!character && MatchesSidebarFilter(filter, SlotLabel(skeleton.Id.Slot)))
                return true;
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone)) continue;
                if (MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName))
                    return true;
                if (!character) continue;
                var cat = Core.BoneInfo.BoneInfoService.GetCategory(bone.Id.CanonicalName);
                if (MatchesSidebarFilter(
                        filter,
                        Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat),
                        cat.ToString()))
                    return true;
            }
        }

        foreach (var candidate in snapshot)
        {
            if (IsOwnedBy(candidate, actor)
                && ActorSubtreeMatches(candidate, snapshot, filter))
                return true;
        }
        return false;
    }

    internal static string SlotLabel(Domain.Identity.PoseSlot slot) => slot switch
    {
        Domain.Identity.PoseSlot.MainHand => "Main Hand",
        Domain.Identity.PoseSlot.OffHand => "Off Hand",
        Domain.Identity.PoseSlot.Prop => "Prop",
        Domain.Identity.PoseSlot.Ornament => "Ornament",
        _ => slot.ToString(),
    };

    /// <summary>
    /// One collapsed group per present auxiliary slot showing that slot's
    /// real parent/child bone hierarchy. Group rows are navigation-only;
    /// bone rows carry exact slot-qualified stable ids, and a filtered view
    /// lists matching bones flat without persisting disclosure.
    /// </summary>
    private void AddAuxiliarySlotGroups(
        ShellSidebarSection section,
        string actorKey,
        List<SkeletonDescriptor> auxSkeletons,
        string filter,
        bool filtering,
        int depth,
        bool[] lines)
    {
        var shown = new List<(SkeletonDescriptor Aux, List<BoneDescriptor> Visible, List<BoneDescriptor> Matching, bool GroupMatches)>();
        foreach (var aux in auxSkeletons)
        {
            var visible = aux.Bones
                .Where(bone => !bone.IsHidden && !IsBoneSuppressed(bone))
                .ToList();
            if (visible.Count == 0)
                continue;
            bool groupMatches = MatchesSidebarFilter(filter, SlotLabel(aux.Id.Slot));
            var matching = filtering && !groupMatches
                ? visible.FindAll(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName))
                : visible;
            if (filtering && !groupMatches && matching.Count == 0)
                continue;
            shown.Add((aux, visible, matching, groupMatches));
        }

        for (int a = 0; a < shown.Count; a++)
        {
            var (aux, visible, matching, groupMatches) = shown[a];
            string slotLabel = SlotLabel(aux.Id.Slot);
            var slotKey = actorKey + "/slot:" + aux.Id.Slot;
            SeedTreeNode(slotKey, _knownCategoryNodes);
            bool slotExpanded = filtering || !_collapsedNodes.Contains(slotKey);
            bool groupLast = a == shown.Count - 1;
            section.Rows.Add(new ShellSidebarRow
            {
                Label = slotLabel,
                Count = "",
                Depth = depth,
                HasChildren = true,
                Expanded = slotExpanded,
                IsLastChild = groupLast,
                TreeLines = lines,
                ExpandKey = slotKey,
                SkeletonContext = aux.Id,
                OverlayMemoryKey = slotKey,
                OverlayBones = visible.Select(bone => bone.Id).ToArray(),
            });
            if (!slotExpanded)
                continue;

            var slotLines = Descend(lines, groupLast);
            if (filtering && !groupMatches)
            {
                // Temporary filtered reveal: matching bones flat.
                for (int b = 0; b < matching.Count; b++)
                    section.Rows.Add(BoneRow(
                        matching[b], depth + 1, b == matching.Count - 1,
                        slotLines, hasChildren: false,
                        expanded: false, expandKey: null));
                continue;
            }

            // Real hierarchy: children map from slot-qualified parent ids;
            // parent traversal never leaves this slot's descriptor set.
            var inSlot = visible.ToDictionary(bone => bone.Id);
            var children = new Dictionary<BoneId, List<BoneDescriptor>>();
            var roots = new List<BoneDescriptor>();
            foreach (var bone in visible)
            {
                if (bone.Parent is { } parent && inSlot.ContainsKey(parent))
                {
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = new List<BoneDescriptor>();
                    list.Add(bone);
                }
                else
                {
                    roots.Add(bone);
                }
            }

            void Emit(BoneDescriptor bone, int boneDepth, bool isLast, bool[] boneLines, string parentKey)
            {
                bool hasKids = children.ContainsKey(bone.Id);
                var boneKey = parentKey + "/bone:" + bone.Id.PartialId + ":" + bone.Id.BoneIndex;
                // Every disclosure seeds collapsed, hierarchy nodes included.
                if (hasKids)
                    SeedTreeNode(boneKey, _knownCategoryNodes);
                bool boneExpanded = !_collapsedNodes.Contains(boneKey);
                section.Rows.Add(BoneRow(
                    bone, boneDepth, isLast, boneLines,
                    hasKids, boneExpanded, hasKids ? boneKey : null));
                if (!hasKids || !boneExpanded)
                    return;
                var kids = children[bone.Id];
                var kidLines = Descend(boneLines, isLast);
                for (int k = 0; k < kids.Count; k++)
                    Emit(kids[k], boneDepth + 1, k == kids.Count - 1, kidLines, boneKey);
            }

            for (int r = 0; r < roots.Count; r++)
                Emit(roots[r], depth + 1, r == roots.Count - 1, slotLines, slotKey);
        }
    }

    private ShellSidebarRow BoneRow(
        BoneDescriptor bone,
        int depth,
        bool isLast,
        bool[] lines,
        bool hasChildren,
        bool expanded,
        string? expandKey)
    {
        var selectionId = SelectionId.ForBone(bone.Id);
        return new ShellSidebarRow
        {
            Label = bone.DisplayName,
            Count = "",
            Depth = depth,
            HasChildren = hasChildren,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Active = _selection.IsSelected(selectionId),
            Tag = selectionId,
            ExpandKey = expandKey,
            OverlayMemoryKey = "bone:" + bone.Id,
            OverlayBones = new[] { bone.Id },
        };
    }

    private static bool MatchesSidebarFilter(string filter, params string?[] values)
    {
        if (filter.Length == 0) return true;
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Extended/IVCS bones are display-suppressed while
    /// Display.ShowNsfwBones is off. Read live per build: the snapshot's own
    /// IsHidden and every selection path are untouched.</summary>
    public bool IsBoneSuppressed(BoneDescriptor bone)
        => !_configuration.Config.Display.ShowNsfwBones
            && Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName);

}
