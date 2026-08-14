using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Game.Types;
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
    private const int RowLightSpot = 4;
    private const int RowLightPoint = 5;
    private const int RowLightArea = 6;
    private const int RowLightDirectional = 7;
    private const int RowLightFromFile = 8;
    private const int RowWorldLight = 9;
    private const int RowCameraGame = 10;
    private const int RowCameraFree = 11;
    private const int RowCameraFromFile = 12;
    private const int ActionRows = 13;

    /// <summary>Double-click is a supported gesture on a single-click list, so
    /// a second activation of the SAME row inside this window is swallowed
    /// rather than spawning twice.</summary>
    private const double ReactivationSwallow = 0.35;

    private const string SpawnFailedNote =
        "The spawn failed — GPose may be full or unavailable.";

    private const string NoWorldLightsNote =
        "No overworld light is close enough to capture — capture works in "
        + "GPose, near a light the world itself places.";

    private const string NoWorldActorsNote =
        "No visible overworld actor is nearby — discovery works in GPose, "
        + "and lists only characters the world is currently drawing.";

    private static readonly string[] KindBadges = ["Minion", "Mount", "Accessory"];

    private readonly IActorSpawnService _spawnService;
    private readonly Game.WorldActorDiscovery _worldActors;
    private readonly Game.PropSpawnService _propService;
    private readonly ILightingService _lightingService;
    private readonly LightPane _lightPane;
    private readonly IVirtualCameraService _cameraService;
    private readonly CameraPane _cameraPane;
    private readonly ISpawnCatalogService _catalog;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly ITextureProvider _textures;
    private readonly HashSet<uint> _missingIcons = new();
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

    /// <summary>Where the world-actor rows begin in the row list. Everything
    /// from here on is the snapshot region RefreshWorldActors rebuilds; the
    /// rows before it are minted once per session.</summary>
    private int _worldRowStart;

    /// <summary>The candidates behind the world rows, parallel to the rows
    /// after the tab's refresh row. Pointer-free snapshots — a stale one is
    /// refused by the discovery service, never dereferenced.</summary>
    private readonly List<WorldActorCandidate> _worldCandidates = new();

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
    private int _captionCount = -1;
    private bool _captionFiltered;

    /// <summary>Why the last activation did nothing, or null. Cleared by the
    /// next activation and by any query change.</summary>
    private string? _note;

    private int _lastRow = -1;
    private double _lastActivatedAt;
    private IActor? _pendingSelectSpawned;
    private ILight? _pendingSelectSpawnedLight;

    public SpawnBrowserWindow(
        IActorSpawnService spawnService,
        Game.WorldActorDiscovery worldActors,
        Game.PropSpawnService propService,
        ILightingService lightingService,
        LightPane lightPane,
        IVirtualCameraService cameraService,
        CameraPane cameraPane,
        ISpawnCatalogService catalog,
        SelectionSession selection,
        StableBindingRegistry bindings,
        ITextureProvider textures)
        : base($"Add to scene###{PluginConstants.PluginName}_spawn_browser",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _spawnService = spawnService;
        _worldActors = worldActors;
        _propService = propService;
        _lightingService = lightingService;
        _lightPane = lightPane;
        _cameraService = cameraService;
        _cameraPane = cameraPane;
        _catalog = catalog;
        _selection = selection;
        _bindings = bindings;
        _textures = textures;

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnActivate = Activate;
        _vm.OnClose = () => IsOpen = false;
        _vm.ResolveIcon = ResolveIcon;
        _vm.OnTab = next =>
        {
            if (_vm.Tab == next)
                return;
            _vm.Tab = next;
            _note = null;
            // The world listing is a snapshot; entering its tab is one of
            // the two implicit refresh points (the other is window open).
            if ((SpawnBrowserTab)next == SpawnBrowserTab.World)
                RefreshWorldActors();
            _refilter = true;
        };
        _vm.OnPinToggle = () => _pinned = !_pinned;
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
    }

    public override void OnOpen()
    {
        BuildRows();
        RefreshWorldLights();
        RefreshWorldActors();
        // The query is a DRAFT: it means nothing outside the open surface, so
        // each open starts on the whole list.
        _vm.Query = string.Empty;
        _note = null;
        _lastRow = -1;
        _hadFocus = false;
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
        ReconcilePendingSpawn();
        SyncQuery();
        if (_refilter)
            Refilter();
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
            _hadFocus = true;
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
            _note = "The world light could not be captured.";
            return;
        }
        _pendingSelectSpawnedLight = captured;
        _note = null;
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
            "##spawn-new-actor", "New actor", TablerIcon.UserPlus));
        rows.Add(ActionRow(
            "##spawn-new-actor-companion",
            "New actor with companion slot",
            TablerIcon.Paw));
        rows.Add(ActionRow(
            "##spawn-clone-actor", "Clone selected actor", TablerIcon.Stack2));
        rows.Add(ActionRow("##spawn-prop", "Prop", TablerIcon.Diamond));
        // Both light entries need the native lighting signatures; without them
        // a spawn is a silent no-op, so they read as disabled rather than
        // doing nothing. Availability is fixed for the session.
        bool noLights = !_lightingService.IsAvailable;
        rows.Add(ActionRow(
            "##spawn-light-spot", "New spot light", TablerIcon.Spotlight,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-point", "New point light", TablerIcon.Bulb,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-area", "New area light", TablerIcon.LightPanel,
            noLights));
        rows.Add(ActionRow(
            "##spawn-light-directional", "New directional light",
            TablerIcon.Sun, noLights));
        rows.Add(ActionRow(
            "##spawn-light-file", "New light from file", TablerIcon.File,
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
            "##spawn-camera", "New camera", TablerIcon.Camera, noCameras,
            help: "A second view over the game camera, switchable any time"));
        rows.Add(ActionRow(
            "##spawn-camera-free", "New free camera", TablerIcon.Video,
            noCameras,
            help: "A camera that flies free of the orbit, on WASD and "
                + "right-drag"));
        rows.Add(ActionRow(
            "##spawn-camera-file", "New camera from file", TablerIcon.File,
            noCameras));

        // Tab per action row, by the fixed row order above. The prop entry
        // is its own tab (a prop catalog arrives later); everything the
        // companion catalog spawns is an ACTOR, so it files under Actors.
        _rowTabs.Clear();
        for (int i = 0; i < ActionRows; i++)
            _rowTabs.Add(i == RowProp
                ? SpawnBrowserTab.Props
                : i < RowProp
                    ? SpawnBrowserTab.Actors
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
        for (int i = 0; i < models.Count; i++)
        {
            rows.Add(new SpawnBrowserRow(
                "##spawn-prop-" + i.ToString(CultureInfo.InvariantCulture),
                models[i].Name,
                models[i].Name.ToLowerInvariant(),
                TablerIcon.Diamond,
                0u,
                "Prop",
                false));
            _rowTabs.Add(SpawnBrowserTab.Props);
        }

        // The world-actor snapshot region begins after every once-per-session
        // row; RefreshWorldActors rebuilds everything from here on.
        _worldRowStart = rows.Count;
        _refilter = true;
    }

    /// <summary>Re-reads the visible overworld actors and rebuilds the world
    /// rows: the tab's refresh row first, then one row per candidate, nearest
    /// first. A candidate is a snapshot of this moment — activation refuses
    /// stale ones — so the list refreshes on open, on entering the tab, and
    /// on the refresh row, never per frame.</summary>
    private void RefreshWorldActors()
    {
        if (!_built)
            return;
        _vm.Rows.RemoveRange(
            _worldRowStart, _vm.Rows.Count - _worldRowStart);
        _rowTabs.RemoveRange(
            _worldRowStart, _rowTabs.Count - _worldRowStart);
        _worldCandidates.Clear();
        _worldCandidates.AddRange(_worldActors.RefreshCandidates());

        _vm.Rows.Add(ActionRow(
            "##spawn-world-actors-refresh",
            "Refresh nearby actors",
            TablerIcon.Refresh,
            help: "Re-scan the world for visible actors to clone"));
        _rowTabs.Add(SpawnBrowserTab.World);

        if (_worldCandidates.Count == 0)
        {
            _vm.Rows.Add(ActionRow(
                "##spawn-world-actors-empty",
                "No nearby actors",
                TablerIcon.User,
                disabled: true,
                help: NoWorldActorsNote));
            _rowTabs.Add(SpawnBrowserTab.World);
        }
        else
        {
            for (int i = 0; i < _worldCandidates.Count; i++)
            {
                var candidate = _worldCandidates[i];
                _vm.Rows.Add(new SpawnBrowserRow(
                    "##spawn-world-actor-"
                        + i.ToString(CultureInfo.InvariantCulture),
                    candidate.Name,
                    candidate.Name.ToLowerInvariant(),
                    WorldGlyph(candidate.Kind),
                    0u,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "{0:0.0}m",
                        candidate.DistanceFromPlayer),
                    false,
                    "Clone this actor into the scene — the original is "
                        + "left untouched"));
                _rowTabs.Add(SpawnBrowserTab.World);
            }
        }
        _refilter = true;
    }

    private static TablerIcon WorldGlyph(WorldActorKind kind) => kind switch
    {
        WorldActorKind.Player => TablerIcon.User,
        WorldActorKind.Retainer => TablerIcon.UserCircle,
        WorldActorKind.Companion => TablerIcon.Paw,
        _ => TablerIcon.Walk,
    };

    /// <summary>How many creature-catalog rows precede the prop library in
    /// the row list; activation splits the shared range on it.</summary>
    private int _actorEntryCount;

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
        _note = null;
        _refilter = true;
    }

    /// <summary>The visible list, refilled in place. A keystroke runs THIS and
    /// nothing else; no cap, because the clipper makes the full list cheap.
    /// </summary>
    private void Refilter()
    {
        _refilter = false;
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
        if (_note is { } note)
        {
            _vm.Status = note;
            return;
        }
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
        _note = null;

        switch (index)
        {
            case RowNewActor:
                SelectSpawned(
                    _spawnService.SpawnNewActor(reserveCompanionSlot: false));
                return;
            case RowNewActorCompanion:
                SelectSpawned(
                    _spawnService.SpawnNewActor(reserveCompanionSlot: true));
                return;
            case RowCloneActor:
                if (SelectedActor() is { } source)
                    SelectSpawned(_spawnService.CloneActor(source));
                return;
            case RowProp:
                _propService.SpawnProp();
                return;
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
                if (_lightingService.SpawnLight(kind) is { } light)
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
                var created = _cameraService.CreateCamera(
                    index == RowCameraFree ? CameraKind.Free : CameraKind.Game);
                if (created == null)
                {
                    _note = "The camera could not be created — cameras exist "
                        + "only inside GPose.";
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
        }

        // World rows are the snapshot region at the end of the list: the
        // refresh row first, then one row per listed candidate.
        if (index >= _worldRowStart)
        {
            if (index == _worldRowStart)
            {
                RefreshWorldActors();
                return;
            }
            int candidate = index - _worldRowStart - 1;
            if (candidate >= 0 && candidate < _worldCandidates.Count)
                CloneWorldActor(_worldCandidates[candidate]);
            return;
        }

        // Prop library rows follow the creature catalog: each spawns its
        // weapon model as a scene prop, listed under the PROPS section.
        if (index - ActionRows >= _actorEntryCount)
        {
            var models = _propService.Catalog;
            int modelIndex = index - ActionRows - _actorEntryCount;
            if (modelIndex >= 0 && modelIndex < models.Count &&
                _propService.SpawnProp(models[modelIndex]) == null)
                _note = SpawnFailedNote;
            return;
        }

        // Catalog rows spawn the entry as its OWN actor, classified by kind
        // at spawn — never attached to an owner's slot, and with no
        // post-spawn model surface anywhere.
        var entry = _catalog.Entries[index - ActionRows];
        var spawned = _spawnService.SpawnCatalogActor(entry);
        if (spawned == null)
        {
            _note = SpawnFailedNote;
            return;
        }
        SelectSpawned(spawned);
    }

    /// <summary>Clones a listed world actor through the typed import. Success
    /// selects the clone through the same pending-select every spawn row
    /// uses; a stale refusal states why and re-lists, so the rows never keep
    /// naming something that is gone.</summary>
    private void CloneWorldActor(WorldActorCandidate candidate)
    {
        var result = _worldActors.CloneCandidate(candidate.Id, out var spawned);
        switch (result.Status)
        {
            case WorldActorImportStatus.Success:
                SelectSpawned(spawned);
                return;
            case WorldActorImportStatus.StaleCandidate:
                RefreshWorldActors();
                _note = "That actor is no longer there — the list was "
                    + "refreshed.";
                return;
            case WorldActorImportStatus.SpawnFailed:
                _note = result.Detail ?? SpawnFailedNote;
                return;
            default:
                _note = result.Detail
                    ?? "Cloning a world actor works only inside GPose.";
                return;
        }
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
    }

    /// <summary>
    /// Resolves a row's game icon to an ImGui handle, or 0 when there is none.
    /// Sheet icon ids are not guaranteed to exist and GetFromGameIcon THROWS for
    /// those, so this uses the try-variant, catches anyway, and remembers the
    /// failures. The WRAP is never cached: shared textures must be re-resolved
    /// each frame.
    /// </summary>
    private nint ResolveIcon(uint iconId)
    {
        if (iconId == 0 || _missingIcons.Contains(iconId))
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            if (_textures.TryGetFromGameIcon(
                    new GameIconLookup(iconId), out var shared))
                wrap = shared.GetWrapOrDefault();
            else
                _missingIcons.Add(iconId);
        }
        catch (Exception)
        {
            _missingIcons.Add(iconId);
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }
}
