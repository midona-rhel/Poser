using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Application.Animation;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binder for <see cref="SpawnBrowserView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): owns the flat row list, the filter
/// cache, the footer caption and every spawn/attach call the rows make.
///
/// <para>ONE surface answers "add something to the scene": the creation
/// actions and every minion, mount and fashion accessory the game declares, in
/// one searchable list. References stay absent (not disabled) until their
/// runtime entity type exists; lights and cameras have theirs, so they are
/// here — disabled only when their native signatures are missing, where a
/// spawn would be a silent no-op.</para>
/// </summary>
public sealed class SpawnBrowserWindow : Window
{
    // Fixed row order: the actions lead the list, so an empty query shows them
    // on top and a query that matches one keeps it above the catalog.
    private const int RowNewActor = 0;
    private const int RowNewActorCompanion = 1;
    private const int RowCloneActor = 2;
    private const int RowProp = 3;
    private const int RowOverlayTalk = 4;
    private const int RowOverlayBalloon = 5;
    private const int RowOverlayStatus = 6;
    private const int RowLightSpot = 7;
    private const int RowLightPoint = 8;
    private const int RowLightArea = 9;
    private const int RowLightDirectional = 10;
    private const int RowLightFromFile = 11;
    private const int RowWorldLight = 12;
    private const int RowCameraGame = 13;
    private const int RowCameraFree = 14;
    private const int RowCameraFromFile = 15;
    private const int RowReferenceImage = 16;
    private const int ActionRows = 17;

    /// <summary>Double-click is a supported gesture on a single-click list, so
    /// a second activation of the SAME row inside this window is swallowed
    /// rather than spawning twice.</summary>
    private const double ReactivationSwallow = 0.35;

    private const string SpawnFailedNote =
        "The spawn failed — GPose may be full or unavailable.";

    private const string NoWorldLightsNote =
        "No overworld light is close enough to capture — capture works in "
        + "GPose, near a light the world itself places.";

    private static readonly string[] KindBadges = ["Minion", "Mount", "Accessory"];

    private readonly IActorSpawnService _spawnService;
    private readonly Game.PropSpawnService _propService;
    private readonly Game.Overlays.OverlayNodeService _overlayService;
    private readonly OverlayPane _overlayPane;
    private readonly ILightingService _lightingService;
    private readonly LightPane _lightPane;
    private readonly IVirtualCameraService _cameraService;
    private readonly CameraPane _cameraPane;
    private readonly ISpawnCatalogService _catalog;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly AnimationSession _animation;
    private readonly ConfigurationService _configuration;
    private readonly ReferenceImageSession _referenceImages;

    /// <summary>Every entity this browser adds goes through the lifecycle
    /// seam, so the add lands in the shell's undo history.</summary>
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly GameIconResolver _icons;
    private readonly SpawnBrowserViewModel _vm = new();

    /// <summary>The capture surface. Candidates are a snapshot of the moment
    /// the window opened — a handle is only valid until the light list next
    /// changes — so the list is refreshed on open, not per frame.</summary>
    private readonly Crystarium.SearchPicker<WorldLightChoice> _worldPicker =
        new("spawn-world-light");

    private readonly List<WorldLightChoice> _worldLights = new();

    /// <summary>One capturable overworld light, as a row. The picker takes
    /// reference types, and the candidate is a struct; the label and the ImGui
    /// key are minted here rather than per frame.</summary>
    private sealed record WorldLightChoice(
        WorldLightCandidate Candidate, string Label, string Key);

    private bool _built;
    private string _query = string.Empty;
    private string _queryLower = string.Empty;
    private bool _refilter = true;

    /// <summary>Tab per row, parallel to the VM's row list. Filled with the
    /// rows, read by every refilter.</summary>
    private readonly List<SpawnBrowserTab> _rowTabs = new();

    /// <summary>Pinned survives across opens within the session; the window
    /// closes on focus loss only while unpinned.</summary>
    private bool _pinned;

    /// <summary>Focus-loss closing arms only after the window has actually
    /// held focus once — a freshly opened window is unfocused for a frame.
    /// </summary>
    private bool _hadFocus;

    /// <summary>Where the invoking plus wants the window, applied by the
    /// next PreDraw and cleared.</summary>
    private Vector2? _pendingAnchor;

    // The caption is a STRING PER COUNT, not per frame: it is rebuilt only when
    // the number it states or the mode it states it in changes.
    private string _caption = string.Empty;

    /// <summary>Why an activation did nothing. A spawn row's refusal is a
    /// TRANSIENT outcome and this window closes on focus loss, so it goes to
    /// the notification channel — a caption in a surface that is already gone
    /// says nothing to anybody.</summary>
    private readonly UserNotices _notices;
    private int _captionCount = -1;
    private bool _captionFiltered;


    private int _lastRow = -1;
    private double _lastActivatedAt;
    private IActor? _pendingSelectSpawned;
    private ILight? _pendingSelectSpawnedLight;

    public SpawnBrowserWindow(
        IActorSpawnService spawnService,
        Game.PropSpawnService propService,
        Game.Overlays.OverlayNodeService overlayService,
        OverlayPane overlayPane,
        ILightingService lightingService,
        LightPane lightPane,
        IVirtualCameraService cameraService,
        CameraPane cameraPane,
        ISpawnCatalogService catalog,
        SelectionSession selection,
        StableBindingRegistry bindings,
        AnimationSession animation,
        ConfigurationService configuration,
        Game.Scene.SceneLifecycleHistory lifecycle,
        ITextureProvider textures,
        UserNotices notices,
        ReferenceImageSession referenceImages,
        global::Poser.Library.IPoseLibraryService library,
        Game.Scene.SceneWorkflow scenes,
        Game.Scene.PlacementAnchorSource anchors)
        : base($"Add to scene###{PluginConstants.PluginName}_spawn_browser",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _spawnService = spawnService;
        _propService = propService;
        _overlayService = overlayService;
        _overlayPane = overlayPane;
        _lightingService = lightingService;
        _lightPane = lightPane;
        _cameraService = cameraService;
        _cameraPane = cameraPane;
        _catalog = catalog;
        _selection = selection;
        _bindings = bindings;
        _animation = animation;
        _configuration = configuration;
        _lifecycle = lifecycle;
        _notices = notices;
        _referenceImages = referenceImages;
        _library = library;
        _scenes = scenes;
        _anchors = anchors;
        _icons = new GameIconResolver(textures);

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnActivate = Activate;
        _vm.OnClose = () => IsOpen = false;
        _vm.ResolveIcon = _icons.Resolve;
        _vm.OnTab = next =>
        {
            if (_vm.Tab == next)
                return;
            _vm.Tab = next;
            _refilter = true;
        };
        _vm.OnPinToggle = () => _pinned = !_pinned;
        // The toggle IS the setting: there is no second home for it in
        // Settings, so flipping it here persists immediately.
        _vm.Frozen = _configuration.Config.SpawnFrozen;
        _vm.OnFrozenToggle = () =>
        {
            _vm.Frozen = !_vm.Frozen;
            _configuration.Config.SpawnFrozen = _vm.Frozen;
            _configuration.ApplyChange();
        };
    }

    /// <summary>Opens (or moves) the window AT the invoking affordance — the
    /// titlebar plus or a section header's — on the tab that affordance
    /// answers for. The anchor is clamped so the window stays on screen.
    /// </summary>
    public void OpenAt(Vector2 anchor, SpawnBrowserTab tab)
    {
        _pendingAnchor = anchor;
        if (_vm.Tab != (int)tab)
        {
            _vm.Tab = (int)tab;
            _refilter = true;
        }
        IsOpen = true;
        BringToFront();
        // Type immediately: the search takes the keyboard at open.
        _vm.FocusSearch = true;
    }

    public override void OnOpen()
    {
        // No rescan here: the index scans once at startup and every save
        // tells it — the Draw revision check re-lists whenever it moves.
        BuildRows();
        RefreshWorldLights();
        // The query is a DRAFT: it means nothing outside the open surface, so
        // each open starts on the whole list.
        _vm.Query = string.Empty;
        _lastRow = -1;
        _hadFocus = false;
        // Re-read rather than trust the cached toggle: a config reset is not
        // routed through this window.
        _vm.Frozen = _configuration.Config.SpawnFrozen;
    }

    public override void PreDraw()
    {
        // The view IS the window chrome — the ImGui host must contribute
        // nothing. Without this the host's inner clip rect insets by half
        // its WindowPadding and every full-bleed fill and rule is cut off
        // the edges (user 2026-08-11: "nothing is really reaching the
        // edge"). Same contract as MainWindow's shell.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        float width = SpawnBrowserView.MeasureWidth();
        Size = new Vector2(width, SpawnBrowserView.DesignHeight);
        SizeCondition = ImGuiCond.Always;
        if (_pendingAnchor is { } anchor)
        {
            _pendingAnchor = null;
            var scaled = new Vector2(width, SpawnBrowserView.DesignHeight)
                * ImGuiHelpers.GlobalScale;
            var viewport = ImGui.GetMainViewport();
            Position = Vector2.Max(
                viewport.WorkPos,
                Vector2.Min(
                    anchor, viewport.WorkPos + viewport.WorkSize - scaled));
            PositionCondition = ImGuiCond.Always;
        }
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        // A scan that finished while the window is open re-lists the saved
        // objects live — one int compare per frame.
        if (_library.Snapshot.Revision != _libraryRevision)
        {
            _built = false;
            _vm.Rows.Clear();
            _rowTabs.Clear();
            BuildRows();
        }
        ReconcilePendingSpawn();
        SyncQuery();
        if (_refilter)
            Refilter();
        if (_reseatHighlight)
        {
            _reseatHighlight = false;
            // The highlight re-seats on the first match after any filter
            // change, so Enter always answers what the list shows first.
            _vm.HighlightRow =
                _vm.Visible.Count > 0 ? _vm.Visible[0] : -1;
        }
        SyncCloneRow();
        SyncStatus();
        _vm.Pinned = _pinned;

        // Menu semantics unless pinned: the window closes when focus leaves
        // it. Armed only after it has HELD focus (a fresh open is unfocused
        // for a frame), and held while the world-light picker owns focus —
        // the picker is pumped from this Draw and would die with it.
        bool focused = ImGui.IsWindowFocused(
            ImGuiFocusedFlags.RootAndChildWindows);
        if (focused)
        {
            _hadFocus = true;
            // Arrow keys walk the visible rows, wrapping at the ends; the
            // search keeps the keyboard the whole time (up/down mean
            // nothing to a one-line input).
            if (_vm.Visible.Count > 0)
            {
                int at = _vm.Visible.IndexOf(_vm.HighlightRow);
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                    at = at < 0 ? 0 : (at + 1) % _vm.Visible.Count;
                else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                    at = at <= 0 ? _vm.Visible.Count - 1 : at - 1;
                if (at >= 0 && at < _vm.Visible.Count)
                    _vm.HighlightRow = _vm.Visible[at];
            }
            else
            {
                _vm.HighlightRow = -1;
            }
        }
        else if (_hadFocus && !_pinned && !_worldPicker.IsOpen)
        {
            IsOpen = false;
            return;
        }

        // The view paints its own chassis (frame + chrome); the host window is
        // an undecorated, transparent shell that only supplies position + input.
        var min = ImGui.GetWindowPos();
        var owner = Interactive.BeginOwner(
            "poser-spawn-browser",
            InteractionLayer.Window,
            min,
            min + ImGui.GetWindowSize());
        try
        {
            SpawnBrowserView.Draw(_vm, min);

            // The footer band is the window's GRAB: pinned, the portal is
            // a palette, and a palette must be movable.
            var footer = _vm.FooterRect;
            if (footer.Size.Y > 0f)
            {
                ImGui.SetCursorScreenPos(footer.Min);
                ImGui.InvisibleButton("##portal-drag", footer.Size);
                if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                if (ImGui.IsItemActive())
                {
                    // The window's position is Dalamud-held (Always, from
                    // the open's anchor), so the drag moves THAT — an
                    // ImGui-side move was re-imposed away every frame.
                    var dragDelta = ImGui.GetIO().MouseDelta;
                    if (dragDelta != Vector2.Zero && Position is { } held)
                        Position = held + dragDelta;
                }
            }
        }
        finally
        {
            Interactive.EndOwner(owner);
        }

        // Pumped after the list: the surface a row opened has to outlive that
        // row's own draw call.
        if (_worldPicker.Draw() is { } chosen)
            CaptureWorldLight(chosen.Item);
    }

    /// <summary>Capture spawns an owned copy and suppresses the original. The
    /// scene has not rescanned yet, so the copy is selected on the next refresh
    /// through the same reconcile a spawned light uses.</summary>
    private void CaptureWorldLight(WorldLightChoice choice)
    {
        var captured = _lightingService.CaptureWorldLight(choice.Candidate);
        if (captured == null)
        {
            _notices.Failed("The world light could not be captured.");
            return;
        }
        _pendingSelectSpawnedLight = captured;
    }

    // ── the list ─────────────────────────────────────────────────────────

    /// <summary>Every row, minted once: the catalog is the sheets' whole
    /// admissible set and cannot change inside a session.</summary>
    private void BuildRows()
    {
        if (_built)
            return;
        _built = true;

        var rows = _vm.Rows;
        rows.Add(ActionRow(
            "##spawn-new-actor", "Actor", TablerIcon.UserPlus));
        rows.Add(ActionRow(
            "##spawn-new-actor-companion",
            "Actor with companion slot",
            TablerIcon.Paw));
        rows.Add(ActionRow(
            "##spawn-clone-actor", "Clone selected actor", TablerIcon.Copy));
        rows.Add(ActionRow("##spawn-prop", "Object", TablerIcon.Diamond));
        // The three game-UI overlays. Without the node library a create is a
        // silent no-op, so they read as disabled rather than doing nothing.
        bool noOverlays = !_overlayService.IsAvailable;
        rows.Add(ActionRow(
            "##spawn-overlay-talk", "Dialogue box", TablerIcon.Message,
            noOverlays,
            help: "The game's own NPC dialogue panel, with a speaker and a "
                + "line of your own"));
        rows.Add(ActionRow(
            "##spawn-overlay-balloon", "Chat bubble", TablerIcon.MessageCircle,
            noOverlays,
            help: "The game's own chat bubble, in any channel's colours"));
        rows.Add(ActionRow(
            "##spawn-overlay-status", "Status line", TablerIcon.Star,
            noOverlays,
            help: "One line of the status bar: an icon and an effect name"));
        // Both light entries need the native lighting signatures; without them
        // a spawn is a silent no-op, so they read as disabled rather than
        // doing nothing. Availability is fixed for the session.
        bool noLights = !_lightingService.IsAvailable;
        rows.Add(ActionRow(
            "##spawn-light-spot", "Spot light", TablerIcon.Spotlight,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-point", "Point light", TablerIcon.Bulb,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-area", "Area light", TablerIcon.LightPanel,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-directional", "Directional light",
            TablerIcon.Sun, noLights));
        rows.Add(ActionRow(
            "##spawn-light-file", "Light from file", TablerIcon.File,
            noLights));
        // Capture takes a copy of a light the world itself placed and
        // suppresses the original; availability moves with the player, so this
        // row is re-stated on every open.
        rows.Add(ActionRow(
            "##spawn-world-light", "World light", TablerIcon.BuildingStore,
            noLights,
            help: "Copy a light the world places here and edit it"));
        // The camera entries follow the light rule: without the native camera
        // signature a create is a silent no-op, so they read as disabled.
        bool noCameras = !_cameraService.IsAvailable;
        rows.Add(ActionRow(
            "##spawn-camera", "Camera", TablerIcon.Camera, noCameras,
            help: "A second view over the game camera, switchable any time"));
        rows.Add(ActionRow(
            "##spawn-camera-free", "Free camera", TablerIcon.Video,
            noCameras,
            help: "A camera that flies free of the orbit, on WASD and "
                + "right-drag"));
        rows.Add(ActionRow(
            "##spawn-camera-file", "Camera from file", TablerIcon.File,
            noCameras));
        // A reference picture needs no native signature and no scene entity,
        // so it is never disabled.
        rows.Add(ActionRow(
            "##spawn-reference-image", "Reference image", TablerIcon.Photo,
            help: "Pin a picture over the game to pose against — it keeps "
                + "its place across GPose and reloads"));

        // Tab per action row, by the fixed row order above. The prop entry
        // is its own tab (a prop catalog arrives later); everything the
        // companion catalog spawns is an ACTOR, so it files under Actors; and
        // the reference picture files under Overlays with the game-UI nodes,
        // because what it adds is laid OVER the game rather than into the
        // scene. It keeps its place at the end of the fixed row order, so in
        // All it still reads last.
        _rowTabs.Clear();
        for (int i = 0; i < ActionRows; i++)
            _rowTabs.Add(i == RowProp
                ? SpawnBrowserTab.Props
                : i < RowProp
                    ? SpawnBrowserTab.Actors
                    : i <= RowOverlayStatus || i == RowReferenceImage
                        ? SpawnBrowserTab.Overlays
                        : i <= RowWorldLight
                            ? SpawnBrowserTab.Lights
                            : SpawnBrowserTab.Cameras);

        var entries = _catalog.Entries;
        _actorEntryCount = entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            rows.Add(new SpawnBrowserRow(
                "##spawn-catalog-" + i.ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.NameLower,
                TablerIcon.Circle,
                entry.IconId,
                Badge(entry.Kind),
                false));
            _rowTabs.Add(SpawnBrowserTab.Actors);
        }

        // The prop library follows: every spawnable weapon-model prop, filed
        // under Props beside the plain test prop above.
        var models = _propService.Catalog;
        _propEntryCount = models.Count;
        for (int i = 0; i < models.Count; i++)
        {
            rows.Add(new SpawnBrowserRow(
                "##spawn-prop-" + i.ToString(CultureInfo.InvariantCulture),
                models[i].Name,
                models[i].Name.ToLowerInvariant(),
                TablerIcon.Diamond,
                0u,
                "Object",
                false));
            _rowTabs.Add(SpawnBrowserTab.Props);
        }

        // The SAVED entries close the list: everything the library holds
        // that can come back into the scene — actors, groups, objects,
        // lights, cameras, overlays — spawnable from here by the name it
        // was saved under. That is the point of saving: press plus and
        // search anything you kept.
        var snapshot = _library.Snapshot;
        _libraryRevision = snapshot.Revision;
        _savedObjects.Clear();
        for (int i = 0; i < snapshot.Entries.Count; i++)
        {
            var entry = snapshot.Entries[i];
            (TablerIcon glyph, SpawnBrowserTab tab)? seat = entry.Kind switch
            {
                global::Poser.Library.PoseLibraryEntryKind.Actor =>
                    (TablerIcon.User, SpawnBrowserTab.Actors),
                global::Poser.Library.PoseLibraryEntryKind.Group =>
                    (TablerIcon.Folder, SpawnBrowserTab.Actors),
                global::Poser.Library.PoseLibraryEntryKind.WorldObject =>
                    (TablerIcon.Square, SpawnBrowserTab.Props),
                global::Poser.Library.PoseLibraryEntryKind.Light =>
                    (TablerIcon.Bulb, SpawnBrowserTab.Lights),
                global::Poser.Library.PoseLibraryEntryKind.Camera =>
                    (TablerIcon.Camera, SpawnBrowserTab.Cameras),
                global::Poser.Library.PoseLibraryEntryKind.Overlay =>
                    (TablerIcon.Message, SpawnBrowserTab.Overlays),
                _ => null,
            };
            if (seat is not { } placed)
                continue;
            _savedObjects.Add((entry.Name, entry.FilePath, entry.Kind));
            rows.Add(new SpawnBrowserRow(
                "##spawn-saved-" + i.ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.NameLower,
                placed.glyph,
                0u,
                "Saved",
                false));
            _rowTabs.Add(placed.tab);
        }

        _refilter = true;
    }

    /// <summary>How many creature-catalog rows precede the prop library in
    /// the row list; activation splits the shared range on it.</summary>
    private int _actorEntryCount;

    private readonly global::Poser.Library.IPoseLibraryService _library;
    private readonly Game.Scene.SceneWorkflow _scenes;
    private readonly Game.Scene.PlacementAnchorSource _anchors;

    /// <summary>Every SAVED library entry the row list carries after the
    /// prop models, parallel by index — actors, groups, objects, lights,
    /// cameras and overlays: press plus, search anything you saved. The
    /// library revision gates the rebuild.</summary>
    private readonly List<(
        string Label,
        string Path,
        global::Poser.Library.PoseLibraryEntryKind Kind)> _savedObjects = new();
    private int _propEntryCount;
    private int _libraryRevision = -1;
    private bool _reseatHighlight;

    /// <summary>Spawns one saved entry where the player stands, each kind
    /// through the same route the library's own activation uses. No anchor
    /// falls back to the entry's saved placement.</summary>
    private void SpawnSavedObject(
        (string Label,
         string Path,
         global::Poser.Library.PoseLibraryEntryKind Kind) saved)
    {
        switch (saved.Kind)
        {
            case global::Poser.Library.PoseLibraryEntryKind.Overlay:
                // Screen-space: placement modes do not apply; the stored
                // centre-relative position re-attaches inside the load.
                var overlayLoad = _scenes.BeginLoad(
                    saved.Path,
                    new Game.Scene.SceneLoadOptions
                    {
                        IncludeActors = false,
                        IncludeProps = false,
                        IncludeLights = false,
                        IncludeCameras = false,
                        IncludeEnvironment = false,
                    });
                if (!overlayLoad.Success)
                    _notices.Failed(
                        overlayLoad.Detail
                        ?? $"'{saved.Label}' could not be staged.");
                return;
        }
        // The configured default rules here — the portal has no
        // placement dropdown of its own.
        var mode = _configuration.Config.DefaultSpawnPlacement;
        var options = new Game.Scene.SceneLoadOptions();
        if (mode != global::Poser.Files.ObjectPlacementMode.AsSaved
            && _anchors.TryCurrentFor(
                mode, out var anchorPosition, out var anchorYaw, out _))
            options = new Game.Scene.SceneLoadOptions
            {
                Placement = mode,
                PlacementPosition = anchorPosition,
                PlacementYaw = anchorYaw,
            };
        var started = _scenes.BeginLoad(saved.Path, options);
        if (!started.Success)
            _notices.Failed(
                started.Detail
                ?? $"'{saved.Label}' could not be spawned.");
    }

    private static SpawnBrowserRow ActionRow(
        string id, string label, TablerIcon glyph, bool disabled = false,
        string? help = null) =>
        new(id, label, label.ToLowerInvariant(), glyph, 0u, null, disabled,
            help);

    /// <summary>Re-reads the capturable overworld lights and re-states the row
    /// they feed. A candidate's handle dies with the next light-list change, so
    /// nothing here is kept across an open.</summary>
    private void RefreshWorldLights()
    {
        _worldLights.Clear();
        if (_lightingService.IsAvailable)
        {
            var candidates = _lightingService.GetWorldLightCandidates();
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                _worldLights.Add(new WorldLightChoice(
                    candidate,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "World light — {0:0.0}m",
                        candidate.DistanceFromPlayer),
                    i.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (!_built)
            return;
        bool disabled = _worldLights.Count == 0;
        var row = _vm.Rows[RowWorldLight];
        if (row.Disabled == disabled)
            return;
        _vm.Rows[RowWorldLight] = row with
        {
            Disabled = disabled,
            Help = disabled
                ? NoWorldLightsNote
                : "Copy a light the world places here and edit it",
        };
    }

    private static string? Badge(CompanionKind kind) => kind switch
    {
        CompanionKind.Companion => KindBadges[0],
        CompanionKind.Mount => KindBadges[1],
        CompanionKind.Ornament => KindBadges[2],
        _ => null,
    };

    private void SyncQuery()
    {
        if (string.Equals(_query, _vm.Query, StringComparison.Ordinal))
            return;
        _query = _vm.Query;
        // Lowercased ONCE per query change; the scan below compares ordinal
        // against names that were lowercased when the catalog was built.
        _queryLower = _query.Trim().ToLowerInvariant();
        _refilter = true;
    }

    /// <summary>The visible list, refilled in place. A keystroke runs THIS and
    /// nothing else; no cap, because the clipper makes the full list cheap.
    /// </summary>
    private void Refilter()
    {
        _refilter = false;
        _reseatHighlight = true;
        var visible = _vm.Visible;
        var rows = _vm.Rows;
        var tab = (SpawnBrowserTab)_vm.Tab;
        visible.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            if (tab != SpawnBrowserTab.All && _rowTabs[i] != tab)
                continue;
            if (_queryLower.Length == 0
                || rows[i].LabelLower.Contains(
                    _queryLower, StringComparison.Ordinal))
                visible.Add(i);
        }
    }

    /// <summary>Clone is the one row whose availability moves with the
    /// selection, so it is the one row rewritten per frame.</summary>
    private void SyncCloneRow()
    {
        bool disabled = SelectedActor() is null;
        var row = _vm.Rows[RowCloneActor];
        if (row.Disabled != disabled)
            _vm.Rows[RowCloneActor] = row with { Disabled = disabled };
    }

    private void SyncStatus()
    {
        bool filtered = _queryLower.Length > 0;
        if (_captionCount != _vm.Visible.Count || _captionFiltered != filtered)
        {
            _captionCount = _vm.Visible.Count;
            _captionFiltered = filtered;
            _caption = _captionCount.ToString(CultureInfo.InvariantCulture)
                + (filtered ? " matches" : " spawnables");
        }
        _vm.Status = _caption;
    }

    // ── activation ───────────────────────────────────────────────────────

    private void Activate(int index)
    {
        double now = ImGui.GetTime();
        if (index == _lastRow && now - _lastActivatedAt < ReactivationSwallow)
            return;
        _lastRow = index;
        _lastActivatedAt = now;

        switch (index)
        {
            case RowNewActor:
                SelectSpawned(_lifecycle.SpawnActor(
                    "Add actor",
                    () => _spawnService.SpawnNewActor(
                        reserveCompanionSlot: false)));
                return;
            case RowNewActorCompanion:
                SelectSpawned(_lifecycle.SpawnActor(
                    "Add actor with companion slot",
                    () => _spawnService.SpawnNewActor(
                        reserveCompanionSlot: true)));
                return;
            case RowCloneActor:
                if (SelectedActor() is { } source)
                    SelectSpawned(_lifecycle.SpawnActor(
                        "Clone actor",
                        () => _spawnService.CloneActor(source)));
                return;
            case RowProp:
                if (_lifecycle.SpawnProp() == null)
                    _notices.Failed(SpawnFailedNote);
                return;
            case RowOverlayTalk:
            case RowOverlayBalloon:
            case RowOverlayStatus:
            {
                var overlayKind = index switch
                {
                    RowOverlayBalloon => OverlayNodeKind.Balloon,
                    RowOverlayStatus => OverlayNodeKind.Status,
                    _ => OverlayNodeKind.Talk,
                };
                if (_lifecycle.SpawnOverlay(overlayKind)
                    is Game.Overlays.OverlayNodeHandle staged)
                {
                    // The pane owns the pending select and is pumped by the
                    // main window every frame, so the selection lands however
                    // this window is dismissed — the camera row's rule.
                    _overlayPane.SelectWhenBound(staged);
                    return;
                }
                _notices.Failed(
                    "The overlay could not be staged — the game's "
                    + "interface would not take it.");
                return;
            }
            case RowLightSpot:
            case RowLightPoint:
            case RowLightArea:
            case RowLightDirectional:
                var kind = index switch
                {
                    RowLightPoint => LightKind.Point,
                    RowLightArea => LightKind.Area,
                    RowLightDirectional => LightKind.Directional,
                    _ => LightKind.Spot,
                };
                if (_lifecycle.SpawnLight(kind) is { } light)
                    _pendingSelectSpawnedLight = light;
                return;
            case RowLightFromFile:
                // The pane owns the dialog and the import's own selection; it
                // is pumped by the main window every frame, so the dialog
                // outlives this window.
                _lightPane.OpenLoad();
                return;
            case RowWorldLight:
                // The row just reserved is the anchor the surface opens off.
                RefreshWorldLights();
                _worldPicker.Open(
                    "world-light",
                    _worldLights,
                    static choice => choice.Label,
                    static choice => choice.Key,
                    options: new PickerOptions<WorldLightChoice>
                    {
                        Glyph = static _ => TablerIcon.Bulb,
                    });
                return;
            case RowCameraGame:
            case RowCameraFree:
            {
                var created = _lifecycle.CreateCamera(
                    index == RowCameraFree ? CameraKind.Free : CameraKind.Game);
                if (created == null)
                {
                    _notices.Failed(
                        "The camera could not be created — cameras exist "
                        + "only inside GPose.");
                    return;
                }
                // The camera pane owns the pending select; it is pumped by
                // the main window every frame, so the selection lands however
                // this window is dismissed.
                _cameraPane.SelectWhenBound(created);
                return;
            }
            case RowCameraFromFile:
                // The pane owns the dialog and the import's own selection,
                // exactly like the light file row above.
                _cameraPane.OpenLoad();
                return;
            case RowReferenceImage:
                // The session owns the picker for the same reason the panes
                // own theirs: it is pumped from the UI root every frame, so
                // the dialog outlives this window closing on focus loss.
                _referenceImages.OpenAddDialog();
                return;
        }

        // Prop library rows follow the creature catalog: each spawns its
        // weapon model as a scene prop, listed under the PROPS section.
        if (index - ActionRows >= _actorEntryCount)
        {
            int modelIndex = index - ActionRows - _actorEntryCount;
            // Saved objects follow the prop models: each spawns its .xivw
            // through the same placement-anchored load the library uses,
            // standing where you are.
            if (modelIndex >= _propEntryCount)
            {
                int savedIndex = modelIndex - _propEntryCount;
                if (savedIndex >= 0 && savedIndex < _savedObjects.Count)
                    SpawnSavedObject(_savedObjects[savedIndex]);
                return;
            }
            var models = _propService.Catalog;
            if (modelIndex >= 0 && modelIndex < models.Count &&
                _lifecycle.SpawnProp(models[modelIndex]) == null)
                _notices.Failed(SpawnFailedNote);
            return;
        }

        // Catalog rows spawn the entry as its OWN actor, classified by kind
        // at spawn — never attached to an owner's slot, and with no
        // post-spawn model surface anywhere.
        var entry = _catalog.Entries[index - ActionRows];
        var spawned = _lifecycle.SpawnActor(
            $"Add {entry.Name}", () => _spawnService.SpawnCatalogActor(entry));
        if (spawned == null)
        {
            _notices.Failed(SpawnFailedNote);
            return;
        }
        SelectSpawned(spawned);
    }

    /// <summary>The selection's actor — a bone selection resolves to the actor
    /// that owns it — as a live actor, or null when nothing resolves.</summary>
    private IActor? SelectedActor()
    {
        var actorId = _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
                bone.Skeleton.Actor,
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeActor } =>
                gazeActor,
            _ => (ActorId?)null,
        };
        if (actorId is not { } id)
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>Selects a freshly spawned actor so the thing just created is
    /// the thing being edited. The scene has not rescanned yet, so the id is
    /// resolved on the next refresh rather than here.</summary>
    private void SelectSpawned(IActor? spawned)
    {
        if (spawned == null)
            return;
        _pendingSelectSpawned = spawned;
    }

    /// <summary>Second half of <see cref="SelectSpawned"/> and of the light
    /// spawn: once the scene refresh has bound the new entity, select it and
    /// forget it.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingSelectSpawnedLight is { } spawnedLight &&
            _bindings.GetLightId(spawnedLight) is { } lightId)
        {
            _selection.Select(SelectionId.ForLight(lightId));
            _pendingSelectSpawnedLight = null;
        }

        if (_pendingSelectSpawned is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
            return;
        _selection.Select(SelectionId.ForActor(id));
        _pendingSelectSpawned = null;
        FreezeIfRequested(id);
    }

    /// <summary>
    /// Brio's spawn-frozen: an actor added while the toggle is on stops on its
    /// first frame instead of playing its idle (Brio waits for the character to
    /// be ready to draw, then writes an overall speed of zero —
    /// <c>Brio/IPC/API/ActorAPI.cs:87-95</c>). This runs at the SAME seam Brio's
    /// wait resolves to: the actor is bound here, which is strictly after the
    /// scene has seen it, so the speed write has a target. Every actor-producing
    /// row reaches it, because they all park on the one pending-select. A failure is reported, not swallowed: an actor
    /// that silently kept playing would read as the toggle doing nothing.
    /// </summary>
    private void FreezeIfRequested(ActorId actor)
    {
        if (!_configuration.Config.SpawnFrozen)
            return;
        var result = _animation.Pause(actor);
        if (!result.Success)
            _notices.Failed(
                result.Detail ?? "The new actor could not be frozen.");
    }
}
