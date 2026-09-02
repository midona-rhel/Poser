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

/// <summary>
/// Binder for <see cref="PoseLibraryView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): owns the rail, the tile list, the filter
/// cache, the footer caption and every apply/spawn call the grid makes.
///
/// <para>The scan lives in <see cref="IPoseLibraryService"/> and publishes one
/// immutable snapshot; this rebuilds its rows only when the revision moves, so
/// a warm frame reads rows it minted at scan time and allocates nothing.</para>
///
/// <para>The library is a MODE of the shell workspace rather than a window, so
/// <see cref="Tick"/> runs every frame whether or not the mode is showing: a
/// spawn started here has to complete even after the user has gone back to an
/// actor.</para>
/// </summary>
public sealed class PoseLibraryPane
{
    /// <summary>Double-click applies, and the action row's primary applies the
    /// same tile: a second apply of the SAME tile is swallowed rather than
    /// importing twice.</summary>
    private const double ReactivationSwallow = 0.35;

    /// <summary>A spawned actor binds on a later scene refresh. Past this many
    /// frames it never will, and the queued pose is dropped rather than held
    /// forever.</summary>
    private const int PendingSpawnFrames = 120;

    /// <summary>Logical pixels between reflows while the workspace is being
    /// resized.</summary>
    private const float ResizeStep = 32f;

    /// <summary>Hysteresis on BOTH edges of stepping: consecutive frames the
    /// size must MOVE before stepping engages, and consecutive frames it must
    /// HOLD before the exact size is adopted. Pointer deltas arrive in
    /// bursts, so a single delta-free frame mid-drag is not a release.
    /// </summary>
    private const int DragStreakFrames = 3;

    private const string AllKey = "##pose-library-all";
    private const string FavoritesKey = "##pose-library-favorites";
    private const string AllLabel = "All poses";
    private const string AllFilesLabel = "All character files";
    private const string AllScenesLabel = "All scenes";
    private const string AllAutoSavesLabel = "All auto-saves";
    private const string FavoritesLabel = "Favorites";

    /// <summary>The auto-save folder name's own format
    /// (<c>AutoSaveService.CreateSnapshotFolder</c>), which is UTC.</summary>
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";

    /// <summary>The stamp every tile shows —
    /// <see cref="LibraryStamp.DateTimeFormat"/>, the same one the scan mints
    /// <c>ModifiedText</c> with, so an auto-save entry and a pose tile read
    /// alike.</summary>
    private const string StampFormat = LibraryStamp.DateTimeFormat;

    /// <summary>The day part of an auto-save rail row and of a scene section.
    /// DISPLAY only — never used to read a name off disk.</summary>
    private const string DayFormat = LibraryStamp.DateFormat;

    /// <summary>The auto-save DAY folder's own name
    /// (<c>AutoSaveService.DayFolderFormat</c>). A stored name is parsed back,
    /// so it keeps its century where the caption drops it — and it is a
    /// separate constant from <see cref="DayFormat"/> for exactly that reason.
    /// </summary>
    private const string SnapshotDayFolderFormat = "yyyy-MM-dd";

    /// <summary>What joins a day to the place it was taken in. One separator
    /// for both surfaces: a scene section heading reads "place – day", an
    /// auto-save rail row "day – place".</summary>
    private const string PlaceSeparator = " – ";

    /// <summary>Separates the two halves of an auto-save rail row's KEY. A
    /// unit separator cannot occur in a day or a place name, so a key is
    /// unambiguous even where a place name contains the visible separator.
    /// </summary>
    private const char KeySeparator = '\u001f';

    private const string PoseExtension = ".pose";

    /// <summary>The one word an outstanding enumeration is stated with —
    /// the footer caption on either tab, and the auto-save grid's empty line
    /// on a first entry that has no rows to leave standing.</summary>
    private const string ScanningText = "Scanning";

    /// <summary>The auto-save grid's standing empty answer, once a pass has
    /// actually landed and found nothing.</summary>
    private const string NoAutoSavesText = "No auto-saves yet.";

    /// <summary>The band's type tabs, positional against
    /// <see cref="PoseLibraryView.TypeLabels"/>.</summary>
    internal enum LibraryType
    {
        Poses,
        AutoSaves,
        Mcdf,
        Scenes,

        /// <summary>Everything that is not a pose, a scene or a character
        /// file: actor, light and camera entries, one tab because they share
        /// the one Objects home.</summary>
        Objects,
    }

    private readonly ConfigurationService _config;
    private readonly IPoseLibraryService _library;
    private readonly PoseThumbnailCache _thumbs;
    private readonly CleanPoseFacade _poseFacade;
    private readonly IActorSpawnService _spawnService;
    private readonly SceneWorkflow _scenes;
    private readonly LightPane _lightPane;
    private readonly CameraPane _cameraPane;
    private readonly Game.Scene.PlacementAnchorSource _anchors;
    private readonly ObjectPlacementPreferences _placement;
    private readonly IEnvironmentService _environment;

    /// <summary>Positional against <see cref="ObjectPlacementMode"/>.</summary>
    private static readonly string[] PlacementModeLabels =
        ["As saved", "Relative to camera", "Relative to actor",
         "In front of camera"];
    private readonly ICameraFileService _cameraFiles;
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;

    /// <summary>The standing load options, so a scene started from a TILE is
    /// the same load the scene workspace's dialog would have run.</summary>
    private readonly SceneLoadPreferences _sceneOptions;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly ActorIntegrationSession _integration;
    private readonly IAutoSaveService _autoSave;
    private readonly PoseFileInspectorSection _files;
    private readonly IActorManager _actors;

    /// <summary>Where every verb's OUTCOME goes. A file verb, an apply and a
    /// spawn all answer after the click that started them; they are not the
    /// grid's standing state, so they do not take a band of it.</summary>
    private readonly UserNotices _notices;
    private readonly PoseLibraryViewModel _vm = new();
    private bool _applyMenuRequested;

    /// <summary>Where the actor picker opens. The FOOTER button has a seat and
    /// hands it over; a tile double-click, a submit key and the tile menu's
    /// own row have none, so those keep the pointer.</summary>
    private Vector2 _applyMenuAnchor;
    private readonly List<IActor> _applyTargets = new();

    /// <summary>Which library the tabs are showing. SESSION state: it is a
    /// browsing mode, not a preference, so it is never persisted and every
    /// entry starts on the poses.</summary>
    private LibraryType _type;

    /// <summary>The Objects tab's KIND filter: the ADMITTED kinds. It
    /// starts full — every kind allowed — and each strip toggle removes
    /// or re-adds its kind (ruled 2026-09-01).</summary>
    private readonly HashSet<PoseLibraryEntryKind> _kindFilter =
        new(Enum.GetValues<PoseLibraryEntryKind>());

    public bool KindFilterContains(PoseLibraryEntryKind kind) =>
        _kindFilter.Contains(kind);

    public void ToggleKindFilter(PoseLibraryEntryKind kind)
    {
        if (!_kindFilter.Add(kind))
            _kindFilter.Remove(kind);
        RebuildAfterFilterChange();
    }

    /// <summary>The union toggle's off half: nothing admitted, so the
    /// tab shows nothing until a kind comes back.</summary>
    public void SetKindFilterNone()
    {
        if (_kindFilter.Count == 0)
            return;
        _kindFilter.Clear();
        RebuildAfterFilterChange();
    }

    /// <summary>The union toggle: every kind admitted again — this IS
    /// the neutral state, so there is no separate reset.</summary>
    public void SetKindFilterAll()
    {
        foreach (var kind in Enum.GetValues<PoseLibraryEntryKind>())
            _kindFilter.Add(kind);
        RebuildAfterFilterChange();
    }

    /// <summary>The portal's from-library rows: exactly one kind shown,
    /// or none for the whole tab.</summary>
    public void SetOnlyKindFilter(PoseLibraryEntryKind? kind)
    {
        _kindFilter.Clear();
        if (kind is { } stated)
            _kindFilter.Add(stated);
        else
            foreach (var all in Enum.GetValues<PoseLibraryEntryKind>())
                _kindFilter.Add(all);
        RebuildAfterFilterChange();
    }

    private void RebuildAfterFilterChange()
    {
        _lastAppliedTile = -1;
        ClearTileSelection();
        _seenRevision = -1;
        _autoDirty = true;
        _refilter = true;
    }

    /// <summary>Whether the kind passes the Objects tab's toggle filter.
    /// Every other tab is one kind and ignores it.</summary>
    private bool KindAdmitted(
        PoseLibraryEntryKind entryKind, PoseLibraryEntryKind primary) =>
        primary != PoseLibraryEntryKind.Actor
        || _kindFilter.Contains(entryKind);

    /// <summary>The action row's import components, one set per tab. SESSION
    /// state like the FILES section's own toggles. The poses tab starts
    /// rotation-only — the pose import default; the auto-save tab starts with
    /// all three, because a restore reproduces what was saved. The MCDF tab
    /// has no set: character files never travel the pose import pipeline.
    /// </summary>
    /// <summary>Collapsed group keys. Session state, cleared on entry; poses
    /// and character files share a folder's key deliberately — it is the same
    /// folder either way.</summary>
    private readonly Dictionary<string, bool> _collapsed =
        new(StringComparer.Ordinal);

    /// <summary>Snapshot folder index to <see cref="PoseLibraryViewModel.Folders"/>
    /// row, or -1 for a folder the active type dropped. The rail is built per
    /// type, so the entry's own folder index is no longer the row's.</summary>
    private int[] _folderRows = [];

    /// <summary>The auto-save tab reads the disk itself; this is what makes it
    /// re-enumerate.</summary>
    private bool _autoDirty = true;

    /// <summary>Whether the STANDING rows were minted by the auto-save path. A
    /// kick that finds the other library's rows in place has to clear them —
    /// its own answer is a frame or more away, and the poses' tiles under a
    /// rail-less tab are not what the auto-save tab may draw meanwhile.
    /// </summary>
    private bool _autoRows;

    /// <summary>Whether an auto-save enumeration is outstanding, as the DRAW
    /// thread sees it: set when a kick starts one, cleared when its result is
    /// minted. This is the tab's own <c>IsScanning</c> — the disabled rescan
    /// and the footer caption read it, never the worker's flags.</summary>
    private bool _autoPending;

    /// <summary>Guards the worker's coalescing state, exactly as
    /// <c>PoseLibraryService</c> guards a scan: a re-dirty raised while a pass
    /// runs queues exactly ONE more rather than stacking workers.</summary>
    private readonly object _autoSync = new();
    private bool _autoScanning;
    private bool _autoQueued;

    /// <summary>The completed enumeration awaiting a mint, or null — the only
    /// cross-thread channel. The worker publishes a DETACHED result: strings
    /// and lists it owns alone, pointing at no view row, so one arriving after
    /// the mode is left simply sits here until the next entry.</summary>
    private List<AutoSaveFolder>? _autoResult;

    /// <summary>Lower-cased tags per TILE. Tiles are a filtered view of the
    /// snapshot's entries, so the tag test can no longer index the entries by
    /// tile index; this is the parallel the pass reads instead.</summary>
    private readonly List<IReadOnlyList<string>> _tileTags = [];

    /// <summary>Lower-cased author per TILE, empty when the file names none —
    /// the search matches it alongside the name and the tags.</summary>
    private readonly List<string> _tileAuthors = [];

    /// <summary>Typed metadata status per TILE. The context menu reads it to
    /// decide which recovery verbs an entry qualifies for.</summary>
    private readonly List<PoseLibraryMetadataStatus> _tileStatus = [];

    /// <summary>Each tile's entry kind, parallel to the tiles — the Objects
    /// tab holds three kinds and its activation dispatches on this.</summary>
    private readonly List<PoseLibraryEntryKind> _tileKinds = [];

    /// <summary>The snapshot revision the rows were built from. A type switch
    /// resets it: each type builds its own rail and tiles from the same
    /// snapshot.</summary>
    private int _seenRevision = -1;

    private string _query = string.Empty;
    private string _queryLower = string.Empty;
    private string? _tagLower;
    private bool _refilter = true;

    /// <summary>The selected rail row's contiguous descendant span in VIEW row
    /// indices, or -1 when the selection is not a folder. The tree is flattened
    /// depth-first, so a subtree is exactly the rows up to the next row at the
    /// same or shallower depth.</summary>
    private int _rangeStart = -1;
    private int _rangeEnd = -1;

    // Resize stepping state (StepResize). Only the WIDTH is ever stepped, so
    // the standing layout is a single X.
    private Vector2 _handedSize;
    private float _steppedX;
    private float _layoutX;
    private int _changedStreak;
    private int _stillStreak;
    private bool _stepping;

    /// <summary>Whether the standing tiles were minted with extensions on
    /// their labels; a Settings flip forces a remint.</summary>
    private bool _builtExtensions;


    private int _lastAppliedTile = -1;
    private double _lastAppliedAt;

    private bool _iconSizeDirty;

    // ── the tile context menu and its file actions ───────────────────────
    // The BINDER owns the tile menu now: its rows depend on the tab, the
    // entry's typed metadata status, and which authoring/recovery verbs
    // apply — none of which the view knows. Disk actions go through the
    // typed PoseLibraryFileActions verbs; every outcome is a notification and
    // a successful mutation requests a rescan, never edits the snapshot.

    private const string TileMenuId = "##pose-library-tile-menu";

    /// <summary>What each context-menu row DOES; separators carry
    /// <see cref="TileMenuAction.None"/> so the row indices stay aligned with
    /// the clicked index the menu answers.</summary>
    private enum TileMenuAction
    {
        None,
        Apply,
        Spawn,
        Favorite,
        Retry,
        Quarantine,
        EditMetadata,
        Rename,
        MoveTo,
        Reveal,
        Delete,
    }

    private readonly List<ContextMenuItem> _menuItems = [];
    private readonly List<TileMenuAction> _menuActionRows = [];

    /// <summary>The move-to menu's destinations, parallel to its rows, and
    /// the file it would move — both frozen at the submenu's open.</summary>
    private readonly List<string> _moveDestinations = [];
    private string? _movePath;

    // The three file modals. Each freezes its target PATH at open: tile
    // indices do not survive a refilter, paths do.
    private readonly global::Poser.UI.Controls.EntityNameModal _renameModal = new();

    private bool _metaOpen;
    private string _metaPath = string.Empty;
    private string _metaAuthor = string.Empty;
    private string _metaTags = string.Empty;
    private string _metaDescription = string.Empty;

    /// <summary>Whether the file carried a preview image when the modal
    /// opened, and what the modal has decided to do about it. Both halves are
    /// needed: the caption states what IS stored, and the edit states what
    /// Save will write, which are different things until Save runs.</summary>
    private bool _metaHadImage;
    private PosePreviewImageEdit _metaImage = PosePreviewImageEdit.Keep;

    /// <summary>The picker for a preview image. Its own dialog rather than the
    /// pose browser's: the extensions differ, and a dialog remembers the
    /// folder it was last in.</summary>
    private readonly Crystarium.FileDialog _metaImageBrowser =
        new("Preview image", new[] { ".png", ".jpg", ".jpeg" });

    /// <summary>Where the image picker last landed, so a second edit opens
    /// where the first one left off.</summary>
    private string _lastImageFolder = string.Empty;

    private bool _deleteOpen;
    private string _deletePath = string.Empty;
    private string _deleteName = string.Empty;

    /// <summary>The auto-save tab's minted recovery line and the observation
    /// key it was minted from — one format per CHANGE, not per frame.</summary>
    private string _autoStatusText = string.Empty;
    private (DateTime? Updated, AutoSaveHealthStatus? Health,
        AutoSaveTerminalStatus Terminal) _autoStatusKey;

    /// <summary>This pane's drive of the ONE shared preview: whose appearance,
    /// which file, which options, and the compare that re-poses it when any of
    /// the three moves.</summary>
    private readonly PosePreviewBinder _previewBinder;

    /// <summary>The last value of <see cref="PoseFileInspectorSection.
    /// TargetPoseRevision"/> this pane acted on — the edge on which the
    /// preview's rebase baseline is captured again.</summary>
    private int _seenPoseRevision;

    /// <summary>Whether the pane is the workspace's current content. The first
    /// draw after it becomes true is the old window's OnOpen.</summary>
    private bool _showing;

    private IActor? _pendingActor;
    private string? _pendingPath;
    private PoseImportOptions? _pendingOptions;
    private int _pendingFrames;

    /// <summary>Raised by the no-sources empty state and by the action row's
    /// "Add source…"; the UI manager owns the settings window.</summary>
    public event Action? OnSettingsRequested;

    /// <summary>Raised by the scenes tab's save affordance. The scene
    /// workspace owns the destination dialog and the description that goes
    /// into the file, so it is asked rather than duplicated here.</summary>
    public event Action? OnSaveSceneRequested;

    public PoseLibraryPane(
        ConfigurationService config,
        IPoseLibraryService library,
        PoseThumbnailCache thumbs,
        CleanPoseFacade poseFacade,
        IActorSpawnService spawnService,
        SelectionSession selection,
        StableBindingRegistry bindings,
        ActorIntegrationSession integration,
        IAutoSaveService autoSave,
        PoseFileInspectorSection files,
        IActorManager actors,
        PosePreviewService preview,
        SceneWorkflow scenes,
        SceneLoadPreferences sceneOptions,
        LightPane lightPane,
        CameraPane cameraPane,
        Game.Scene.PlacementAnchorSource anchors,
        ICameraFileService cameraFiles,
        ObjectPlacementPreferences placement,
        IEnvironmentService environment,
        Game.Scene.SceneLifecycleHistory lifecycle,
        UserNotices notices)
    {
        _lifecycle = lifecycle;
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _poseFacade = poseFacade;
        _spawnService = spawnService;
        _scenes = scenes;
        _lightPane = lightPane;
        _cameraPane = cameraPane;
        _anchors = anchors;
        _cameraFiles = cameraFiles;
        _placement = placement;
        _environment = environment;
        _sceneOptions = sceneOptions;
        _selection = selection;
        _bindings = bindings;
        _integration = integration;
        _autoSave = autoSave;
        _files = files;
        _actors = actors;
        _notices = notices;
        _previewBinder = new PosePreviewBinder(preview, poseFacade);

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnSelectFolder = SelectFolder;
        _vm.OnToggleGroup = ToggleGroup;
        _vm.OnSelect = Select;
        _vm.OnSelectWith = SelectWith;
        _vm.OnMarquee = MarqueeSelect;
        _vm.OnApplyTarget = index =>
            _applyChoice = index >= 0 && index < _applyTargets.Count
                ? _applyTargets[index]
                : null;
        // Every apply that HAS a target goes through the actor picker — one
        // workflow, the target always explicit (a lone eligible actor skips
        // the menu). A scene has no target and loads outright.
        _vm.OnApplyTile = ActivateTile;
        _vm.Footer = DrawFooterLead;
        _vm.OnSpawnTile = Spawn;
        _vm.OnToggleFavorite = ToggleFavorite;
        _vm.OnTagFilter = TagFilter;
        _vm.OnIconSize = SetIconSize;
        _vm.OnRefresh = Refresh;
        // The two Brio menus, opened from the action row; the shared state
        // lives on the FILES section so both surfaces read one filter. The
        // library mount opens the import menu WITHOUT presets — rest poses
        // belong to the actor part (user rule).
        // The rail's "Options" is a BUTTON, so the menu hangs off it — the
        // same seat rule the Apply menu below already follows.
        _vm.OnImportMenu = () => _files.RequestImportMenu(
            withPresets: false, anchor: Crystarium.ButtonSeat);
        _vm.OnBoneFilterMenu = () => _files.RequestBoneFilterMenu();
        _vm.OnApplyMenu = () =>
        {
            if (_type == LibraryType.Scenes)
            {
                LoadScene(_vm.Selected);
                return;
            }
            // An object tile's action is its own (spawn or apply by what the
            // file is); the actor-target picker is for poses and character
            // files only.
            if (_type == LibraryType.Objects)
            {
                ActivateObject(_vm.Selected);
                return;
            }
            ApplyToChosen(_vm.Selected);
        };
        // The character-file apply is the one long transaction this pane
        // starts, so this pane also carries its stop — the same cooperative
        // cancel the appearance pane's progress row calls.
        _vm.OnCancelImport = _integration.CancelMcdf;
        _vm.OnSaveScene = () => OnSaveSceneRequested?.Invoke();
        _vm.OnEditMetadata = OpenMetadataEditor;
        _vm.OnOpenSettings = () => OnSettingsRequested?.Invoke();
        _vm.ResolveThumbnail = ResolveThumbnail;
        // Spawning needs no selection and no scene state; the service answers
        // null when the game refuses, which is a note rather than a gate.
        _vm.CanSpawn = true;
    }

    private Action<OperationReceipt> TrackImport(ActorId expectedActor)
    {
        Guid? operation = null;
        return receipt =>
        {
            if (receipt.TargetActorId != expectedActor)
                return;
            if (receipt.State == OperationReceiptState.Pending)
            {
                operation = receipt.OperationId;
                return;
            }
            if (operation != receipt.OperationId)
                return;
            operation = null;
            if (receipt.State is not OperationReceiptState.Applied)
                _notices.Failed(
                    $"Apply: {receipt.Detail ?? receipt.State.ToString()}.");
        };
    }

    /// <summary>
    /// The frame work that outlives the mode: the thumbnail cache's decode
    /// pump, and a spawn waiting for the scene to bind its actor. Leaving the
    /// library must not strand a pose that was already asked for, so the shell
    /// calls this unconditionally.
    /// </summary>
    public void Tick()
    {
        _thumbs.Tick();
        ReconcilePendingSpawn();
    }

    /// <summary>Draws into the rect the shell hands the workspace.</summary>
    public void Draw(Vector2 origin, Vector2 size)
    {
        if (!_showing)
        {
            _showing = true;
            Enter();
        }

        if (_config.Config.Library.ShowFileExtensions != _builtExtensions)
        {
            _seenRevision = -1;
            _autoDirty = true;
        }

        if (_type == LibraryType.AutoSaves)
        {
            // The auto-save root is re-enumerated on entry and on an explicit
            // rescan, never watched — and never on THIS thread: a cold root of
            // snapshot folders is a whole frame's worth of disk, so the kick
            // only starts a worker and the take mints whatever has landed.
            if (_autoDirty)
                KickAutoSaves();
            TakeAutoSaves();
        }
        else
        {
            SyncSnapshot();
        }

        SyncQuery();
        if (_refilter)
            Refilter();
        SyncTarget();
        SyncApplyTargets();
        SyncImportToggles();
        SyncStatus();
        SyncPreview();

        // The grid reflows at resize steps; the bar rows track the live
        // width through ChromeWidth so their clusters do not jump.
        _vm.ChromeWidth = size.X;
        PoseLibraryView.Draw(_vm, origin, StepResize(size));
        DrawApplyMenu();
        DrawTileMenu();
        _renameModal.Draw();
        // While the image picker is open the modal is not begun: an ImGui
        // modal that is begun blocks every other window, picker included,
        // and dims over it. Its state is untouched, so it resumes where it
        // was when the picker closes.
        if (!_metaImageBrowser.IsOpen)
            DrawMetadataModal();
        _metaImageBrowser.Draw();
        PumpEnrichments();
        DrawDeleteModal();
    }

    /// <summary>The footer primary's actor picker: every scene actor
    /// (skeleton-bearing for pose applies), applied-to on pick — the pose
    /// goes to whoever is CHOSEN, never silently to the selection.</summary>
    private void DrawApplyMenu()
    {
        if (_applyMenuRequested)
        {
            _applyMenuRequested = false;
            _applyTargets.Clear();
            var items = new List<ContextMenuItem>();
            foreach (var actor in _actors.Actors)
            {
                bool eligible = _type == LibraryType.Mcdf || actor.HasSkeleton;
                if (!eligible)
                    continue;
                _applyTargets.Add(actor);
                string name = _bindings.GetActorId(actor) is { } id
                    ? ActorNames.Display(id, actor.Name)
                    : ActorNames.Clean(actor.Name);
                items.Add(new ContextMenuItem(name, TablerIcon.UserPlus));
            }
            if (items.Count == 0)
            {
                _notices.Refused("No actor to apply to.");
                return;
            }
            if (items.Count == 1)
            {
                ApplyTo(_vm.Selected, _applyTargets[0]);
                return;
            }
            Crystarium.FloatingMenu.Open(
                "##library-apply-target", _applyMenuAnchor, items.ToArray());
        }

        int clicked = Crystarium.FloatingMenu.Draw("##library-apply-target");
        if (clicked >= 0 && clicked < _applyTargets.Count)
            ApplyTo(_vm.Selected, _applyTargets[clicked]);
    }

    // ── the tile menu ────────────────────────────────────────────────────

    /// <summary>The tile context menu: the apply/spawn/favorite verbs every
    /// tile always had, plus the authoring verbs (edit metadata, rename,
    /// move) and — on a flagged entry — the recovery verbs (retry,
    /// quarantine). Rows are decided HERE because they depend on the tab and
    /// the entry's typed status; the view only reports the right-click.
    /// </summary>
    private void DrawTileMenu()
    {
        if (_vm.MenuRequested)
        {
            // The request is consumed whether or not it can still be served:
            // a stale target must not re-open the menu every frame.
            _vm.MenuRequested = false;
            if (_vm.MenuTile >= 0 && _vm.MenuTile < _vm.Tiles.Count)
            {
                BuildTileMenu(_vm.MenuTile);
                Crystarium.FloatingMenu.Open(
                    TileMenuId, ImGui.GetMousePos(), _menuItems.ToArray());
            }
        }

        int clicked = Crystarium.FloatingMenu.Draw(TileMenuId);
        int moveClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick();
        if (moveClicked >= 0 && moveClicked < _moveDestinations.Count
            && _movePath is { } movePath)
        {
            MoveToFolder(movePath, _moveDestinations[moveClicked]);
            return;
        }
        if (clicked < 0 || clicked >= _menuActionRows.Count
            || _vm.MenuTile < 0 || _vm.MenuTile >= _vm.Tiles.Count)
            return;
        Dispatch(_menuActionRows[clicked], _vm.MenuTile);
    }

    private void BuildTileMenu(int index)
    {
        _menuItems.Clear();
        _menuActionRows.Clear();
        var tile = _vm.Tiles[index];
        bool scenes = _type == LibraryType.Scenes;
        bool objects = _type == LibraryType.Objects;
        int count = VerbTargets(index).Count;
        string many = count > 1 ? " " + count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        // The verb says what the file IS: a scene loads, an object spawns,
        // a pose or a character file applies. One file at a time.
        if (count == 1)
        {
            Row(TileMenuAction.Apply, new ContextMenuItem(
                scenes ? "Load scene" : objects ? "Spawn" : "Apply",
                scenes ? TablerIcon.Movie : objects ? TablerIcon.Plus : TablerIcon.Check,
                disabled: !_vm.CanApply));
            if (!scenes && !objects)
                Row(TileMenuAction.Spawn, new ContextMenuItem(
                    "Spawn as new actor", TablerIcon.UserPlus,
                    disabled: !_vm.CanSpawn));
        }
        if (_vm.CanFavorite)
            Row(TileMenuAction.Favorite, new ContextMenuItem(
                (tile.Favorite ? "Unfavorite" : "Favorite") + many, TablerIcon.Star));

        bool poses = _type == LibraryType.Poses;
        var status = _tileStatus[index];
        if (count == 1 && poses && status != PoseLibraryMetadataStatus.Valid)
        {
            Separator();
            Row(TileMenuAction.Retry, new ContextMenuItem(
                "Retry read", TablerIcon.Refresh,
                help: "Probe the file again — one that finished writing "
                    + "since the scan reads cleanly now."));
            Row(TileMenuAction.Quarantine, new ContextMenuItem(
                "Quarantine", TablerIcon.Shield,
                help: "Move the file into this folder's "
                    + PoseLibraryFileActions.QuarantineFolderName
                    + " folder: out of the library, kept as evidence."));
        }

        // The auto-save tab's files belong to retention — renaming or moving
        // one would break the save-event grouping it prunes by, so the
        // authoring verbs stay off that tab.
        if (_type != LibraryType.AutoSaves)
        {
            Separator();
            if (count == 1 && CanEditMetadata(index))
                Row(TileMenuAction.EditMetadata, new ContextMenuItem(
                    "Edit metadata…", TablerIcon.FileText,
                    help: "Author and tags, written back into the file."));
            if (count == 1)
                Row(TileMenuAction.Rename, new ContextMenuItem(
                    "Rename…", TablerIcon.Edit));
            _moveMore.Clear();
            foreach (var target in VerbTargets(index))
                if (target != index)
                    _moveMore.Add(_vm.Tiles[target].ThumbKey);
            Row(TileMenuAction.MoveTo, new ContextMenuItem(
                count > 1 ? $"Move{many} to folder…" : "Move to folder…", TablerIcon.Folder,
                submenuItems: BuildMoveSubmenu(tile.ThumbKey)));
        }
        Separator();
        if (count == 1)
            Row(TileMenuAction.Reveal, new ContextMenuItem(
                "Reveal in Explorer", TablerIcon.ExternalLink));
        Row(TileMenuAction.Delete, new ContextMenuItem(
            count > 1 ? $"Delete{many} files…" : "Delete…", TablerIcon.Trash, danger: true));

        void Row(TileMenuAction action, ContextMenuItem item)
        {
            _menuItems.Add(item);
            _menuActionRows.Add(action);
        }

        void Separator()
        {
            _menuItems.Add(ContextMenuItem.Separator);
            _menuActionRows.Add(TileMenuAction.None);
        }
    }

    /// <summary>Author and tags for one tile. Reached from the context menu
    /// AND from the action row, because a verb that exists only under a
    /// right-click is a verb most users never find.</summary>
    private void OpenMetadataEditor(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var tile = _vm.Tiles[index];
        _metaPath = tile.ThumbKey;
        _metaAuthor = tile.Author ?? string.Empty;
        _metaTags = string.Join(", ", tile.Tags);
        // Description and the preview image are not on the tile — the grid has
        // no use for either — so the document is read once, here, rather than
        // widening every tile in the library to carry them.
        _metaDescription = string.Empty;
        _metaHadImage = false;
        _metaImage = PosePreviewImageEdit.Keep;
        var read = AtomicPoseFileStore.Default.Read(_metaPath);
        if (read.Succeeded && read.Pose is { } pose)
        {
            _metaDescription = pose.Description ?? string.Empty;
            _metaHadImage = !string.IsNullOrEmpty(pose.Base64Image);
        }
        _metaOpen = true;
    }

    /// <summary>Whether the highlighted tile is an editable document: the ONE
    /// gate, read by the context menu and the action row alike.</summary>
    private bool CanEditMetadata(int index) =>
        _type == LibraryType.Poses
        && index >= 0
        && index < _vm.Tiles.Count
        && index < _tileStatus.Count
        && _tileStatus[index] == PoseLibraryMetadataStatus.Valid
        && !_vm.Tiles[index].ThumbKey.EndsWith(
            ".cmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tile's primary verb. A pose or a character file needs a
    /// TARGET, so it opens the actor picker; a scene is the whole session and
    /// has no target, so it starts its own transaction straight away.</summary>
    /// <summary>What activating a TILE means: a scene loads outright, and
    /// everything else opens the actor picker. A tile is not a button, so the
    /// picker hangs at the pointer that opened it rather than at a seat.
    /// </summary>
    private void ActivateTile(int index)
    {
        Select(index);
        if (_type == LibraryType.Scenes)
        {
            LoadScene(index);
            return;
        }
        if (_type == LibraryType.Objects)
        {
            ActivateObject(index);
            return;
        }
        ApplyToChosen(index);
    }

    /// <summary>The footer's LEFT cluster: configuring sources belongs on
    /// every tab (left-aligned, user rule), the Objects tab's placement
    /// choice sits at the bottom where the spawn happens, and the status
    /// stays last.</summary>
    private void DrawFooterLead(Crystarium.ActionBarScope scope)
    {
        scope.Button("Add source", () => _vm.SettingsClick?.Invoke());
        scope.Label(_vm.Status);
    }

    // ── the objects inspector rail ───────────────────────────────────────

    /// <summary>The probed path the details rows were read from; a selection
    /// change re-probes. Object entry files are small (a light or camera is a
    /// page of JSON; a container answers through its bounded metadata read),
    /// so the probe runs inline on the click.</summary>
    private string? _detailsPath;
    private readonly List<(string Label, string Value)> _detailsRows = [];
    private Vector3? _detailsColor;

    /// <summary>Which anchors the probed entry records — what gates the
    /// placement choices offered for it.</summary>
    private bool _detailsHasCameraAnchor;
    private bool _detailsHasActorAnchor;

    /// <summary>The modes on offer, positional against the dropdown. ALL
    /// four, always (ruled 2026-08-31): an entry without a saved anchor no
    /// longer refuses a relative mode — the load anchors on the content's
    /// centroid, landing it on the current camera or actor.</summary>
    private readonly List<ObjectPlacementMode> _placementChoices = [];
    private readonly List<string> _placementChoiceLabels = [];

    private void BuildPlacementChoices()
    {
        _placementChoices.Clear();
        _placementChoiceLabels.Clear();
        _placementChoices.Add(ObjectPlacementMode.InFrontOfCamera);
        _placementChoiceLabels.Add(PlacementModeLabels[3]);
        _placementChoices.Add(ObjectPlacementMode.AsSaved);
        _placementChoiceLabels.Add(PlacementModeLabels[0]);
        _placementChoices.Add(ObjectPlacementMode.RelativeToCamera);
        _placementChoiceLabels.Add(PlacementModeLabels[1]);
        _placementChoices.Add(ObjectPlacementMode.RelativeToSelectedActor);
        _placementChoiceLabels.Add(PlacementModeLabels[2]);
    }

    /// <summary>What this spawn actually uses: the preference when the
    /// entry can honour it, else as-saved — the same fallback the dropdown
    /// displays, so the shown choice and the spawn can never disagree.
    /// </summary>
    private ObjectPlacementMode EffectiveMode() =>
        _placementChoices.Contains(_placement.Mode)
            ? _placement.Mode
            : ObjectPlacementMode.AsSaved;

    /// <summary>
    /// The Objects tab's INSPECTOR rail — the same right column every other
    /// library tab fills. One plain run of rows: where a load lands, then
    /// what the selected entry IS — the properties a person recognizes it
    /// by, never raw coordinates.
    /// </summary>
    /// <summary>Per-tile stamps for the info rail, parallel to the tile
    /// list like the tag and author lists above.</summary>
    private readonly List<string> _tileModified = [];
    private readonly List<string> _tileContents = [];

    /// <summary>The rail for the tabs that cannot preview (MCDF, scenes):
    /// the selected FILE, stated — name, stamp, author, contents, tags.
    /// Returns the height consumed so a caller can stack below it.</summary>
    public float DrawInfoRail(Vector2 origin, Vector2 size)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        int selected = _vm.Selected;
        if (selected < 0 || selected >= _vm.Tiles.Count ||
            selected >= _tileModified.Count)
        {
            // Centred in the rail, both ways, like every empty state.
            Crystarium.TextInBand(
                origin, size, "Select a file",
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.FormHint,
                },
                TextAlign.Center);
            return size.Y;
        }

        var tile = _vm.Tiles[selected];
        Crystarium.TextAt(cursor, tile.Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;

        float body = Crystarium.Section(
            "##library-file-info", string.Empty,
            new Vector2(origin.X, cursor.Y), size.X, true, null,
            form =>
            {
                form.ReadOnly("Saved", _tileModified[selected],
                    mono: true);
                if (!string.IsNullOrEmpty(tile.Author))
                    form.ReadOnly("Author", tile.Author!, mono: true);
                // Contents are PER-KIND rows — "Actors 2", "Lights 3" —
                // never one truncating line (ruled 2026-08-31). The
                // pre-minted one-liner splits on its own separator.
                if (_tileContents[selected].Length > 0)
                    foreach (var part in _tileContents[selected].Split(", "))
                    {
                        int space = part.IndexOf(' ');
                        if (space > 0 && int.TryParse(
                                part[..space], out _))
                            form.ReadOnly(
                                char.ToUpperInvariant(part[space + 1])
                                    + part[(space + 2)..],
                                part[..space], mono: true);
                        else
                            form.ReadOnly("Contents", part, mono: true);
                    }
                if (tile.Tags.Count > 0)
                    form.ReadOnly("Tags", string.Join(", ", tile.Tags),
                        mono: true);
                if (tile.Flagged)
                    form.Status(tile.StatusText);
            },
            divider: false, dense: true);
        return cursor.Y - origin.Y + body + theme.Spacing.Six * scale;
    }

    public void DrawObjectsRail(Vector2 origin, Vector2 size)
    {
        int selected = _vm.Selected;
        if (selected < 0 || selected >= _vm.Tiles.Count ||
            selected >= _tileKinds.Count)
            return;
        ProbeDetails(_vm.Tiles[selected].ThumbKey, _tileKinds[selected]);

        float scale = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var cursor = origin + new Vector2(inset, inset);

        // The entry's NAME leads, then one plain "Properties" heading — no
        // separators anywhere on this rail.
        Crystarium.TextAt(cursor, _vm.Tiles[selected].Label, new TextStyle
        {
            Size = theme.Typography.SurfaceTitleSize,
            Weight = FontWeight.Medium,
            Color = theme.Text,
        });
        cursor.Y += (theme.Typography.SurfaceTitleSize + 10f) * scale;
        Crystarium.TextAt(cursor, "Properties", new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.FormHint,
        });
        cursor.Y += (theme.Typography.CaptionSize + 6f) * scale;

        Crystarium.Section(
            "##objects-inspector", string.Empty,
            new Vector2(origin.X, cursor.Y), size.X, true, null,
            form =>
            {
                if (_detailsColor is { } color)
                {
                    form.Custom("Color", 20f, row =>
                    {
                        float radius = 8f * ImGuiHelpers.GlobalScale;
                        var center = row.CenterControl(16f)
                            + new Vector2(radius, radius);
                        var clamped = Vector3.Clamp(
                            color, Vector3.Zero, Vector3.One);
                        ImGui.GetWindowDrawList().AddCircleFilled(
                            center, radius,
                            ImGui.ColorConvertFloat4ToU32(
                                new Vector4(clamped, 1f)));
                    });
                }
                foreach (var (label, value) in _detailsRows)
                    form.ReadOnly(label, value, mono: true);
            },
            divider: false, dense: true);
    }

    private void ProbeDetails(string path, PoseLibraryEntryKind kind)
    {
        if (string.Equals(path, _detailsPath, StringComparison.Ordinal))
            return;
        _detailsPath = path;
        _detailsRows.Clear();
        _detailsColor = null;
        _detailsHasCameraAnchor = false;
        _detailsHasActorAnchor = false;
        try
        {
            switch (kind)
            {
                case PoseLibraryEntryKind.Actor:
                case PoseLibraryEntryKind.Environment:
                case PoseLibraryEntryKind.Overlay:
                case PoseLibraryEntryKind.Group:
                case PoseLibraryEntryKind.WorldObject:
                case PoseLibraryEntryKind.Prop:
                case PoseLibraryEntryKind.Light:
                case PoseLibraryEntryKind.Camera:
                    var metadata = SceneFileStore.Default.ReadMetadata(path);
                    if (metadata.Succeeded)
                    {
                        if (!string.IsNullOrEmpty(metadata.PlaceName))
                            _detailsRows.Add(("Place", metadata.PlaceName!));
                        // A group entry says what it HOLDS — the one fact
                        // its tile cannot — as per-kind rows.
                        if (kind == PoseLibraryEntryKind.Group)
                            AppendContentsRows(metadata, _detailsRows);
                        if (kind == PoseLibraryEntryKind.Environment)
                        {
                            // The name travels in the file when the capture
                            // recorded it; an older file resolves through the
                            // live weather sheet by id.
                            string weather = metadata.WeatherName.Length > 0
                                ? metadata.WeatherName
                                : metadata.WeatherId != 0 &&
                                  _environment.GetWeatherInfo(
                                      metadata.WeatherId) is { } known
                                    ? known.Name
                                    : string.Empty;
                            if (weather.Length > 0)
                                _detailsRows.Add(("Weather", weather));
                        }
                        if (metadata.SavedAt is { } saved)
                            _detailsRows.Add(("Saved", saved.ToLocalTime()
                                .ToString(LibraryStamp.DateTimeFormat,
                                    CultureInfo.InvariantCulture)));
                        if (kind is PoseLibraryEntryKind.Actor
                            or PoseLibraryEntryKind.Group)
                        {
                            _detailsHasCameraAnchor = metadata.HasCameraAnchor;
                            _detailsHasActorAnchor = metadata.HasActorAnchor;
                        }
                    }
                    break;
            }
            // Every entry answers "Saved": the document's own stamp when it
            // records one, else the file's write time — a light or camera
            // document carries no date of its own.
            if (!_detailsRows.Exists(row => row.Label == "Saved"))
                _detailsRows.Add(("Saved",
                    System.IO.File.GetLastWriteTime(path).ToString(
                        LibraryStamp.DateTimeFormat,
                        CultureInfo.InvariantCulture)));
        }
        catch (Exception)
        {
            _detailsRows.Add(("Details", "could not be read"));
        }
        if (_detailsRows.Count == 0 && _detailsColor is null)
            _detailsRows.Add(("Details", "none recorded"));
    }

    /// <summary>The entry's contents as PER-KIND rows — "Actors 2",
    /// "Lights 3" — never one truncating line.</summary>
    private static void AppendContentsRows(
        SceneMetadataReadOutcome metadata,
        List<(string Label, string Value)> rows)
    {
        void Part(int count, string label)
        {
            if (count > 0)
                rows.Add((label,
                    count.ToString(CultureInfo.InvariantCulture)));
        }
        Part(metadata.ActorCount, "Actors");
        Part(metadata.PropCount, "Objects");
        Part(metadata.WorldObjectCount, "Borrowed objects");
        Part(metadata.LightCount, "Lights");
        Part(metadata.CameraCount, "Cameras");
        Part(metadata.OverlayCount, "Overlays");
    }

    /// <summary>
    /// An object tile's one action, by what the file is. An actor entry
    /// SPAWNS its actor — through the same scene workflow a scene load uses,
    /// with fresh additive options so a clear-first preference set for
    /// scenes can never fire from a library tile. Lights and cameras import
    /// through their own services, which spawn a new light and create a new
    /// camera respectively.
    /// </summary>
    private void ActivateObject(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count ||
            index >= _tileKinds.Count)
            return;
        var path = _vm.Tiles[index].ThumbKey;
        var name = _vm.Tiles[index].Label;
        switch (_tileKinds[index])
        {
            // ONE pipeline: every container entry — actor, group, object,
            // light, camera — spawns through the same placement-anchored
            // load.
            case PoseLibraryEntryKind.Actor:
            case PoseLibraryEntryKind.Group:
            case PoseLibraryEntryKind.WorldObject:
            case PoseLibraryEntryKind.Prop:
            case PoseLibraryEntryKind.Light:
            case PoseLibraryEntryKind.Camera:
                var actorMode = EffectiveMode();
                if (!_anchors.TryCurrentFor(
                        actorMode, out var anchorPosition,
                        out var anchorYaw, out var anchorRefusal))
                {
                    _notices.Refused(anchorRefusal!);
                    break;
                }
                var started = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    Placement = actorMode,
                    PlacementPosition = anchorPosition,
                    PlacementYaw = anchorYaw,
                });
                if (!started.Success)
                    _notices.Failed(
                        started.Detail ?? "The actor could not be spawned.");
                break;
            case PoseLibraryEntryKind.Overlay:
                // Screen-space: placement modes do not apply; the stored
                // centre-relative position re-attaches at the current
                // centre inside the load.
                var overlayLoad = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    IncludeActors = false,
                    IncludeProps = false,
                    IncludeLights = false,
                    IncludeCameras = false,
                    IncludeEnvironment = false,
                });
                if (!overlayLoad.Success)
                    _notices.Failed(
                        overlayLoad.Detail ??
                        "The overlay could not be staged.");
                break;
            case PoseLibraryEntryKind.Environment:
                // The load applies only what the file states; an environment
                // entry states nothing but the environment.
                var applied = _scenes.BeginLoad(path, new SceneLoadOptions
                {
                    IncludeActors = false,
                    IncludeProps = false,
                    IncludeLights = false,
                    IncludeCameras = false,
                    IncludeOverlays = false,
                });
                if (!applied.Success)
                    _notices.Failed(
                        applied.Detail ??
                        "The environment could not be applied.");
                break;
        }
    }

    /// <summary>Restores a highlighted scene through the ONE scene workflow —
    /// the same single-flight transaction the scene workspace starts, so a
    /// refusal reads the same on either surface.</summary>
    private void LoadScene(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        // Scenes obey the placement rule like every entry (ruled
        // 2026-08-31): the standing load options, plus wherever the
        // footer's choice puts the content.
        var sceneLoad = _sceneOptions.Options;
        var sceneMode = EffectiveMode();
        if (sceneMode != ObjectPlacementMode.AsSaved
            && _anchors.TryCurrentFor(
                sceneMode, out var scenePoint, out var sceneYaw, out _))
            sceneLoad = sceneLoad with
            {
                Placement = sceneMode,
                PlacementPosition = scenePoint,
                PlacementYaw = sceneYaw,
            };
        var started = _scenes.BeginLoad(
            _vm.Tiles[index].ThumbKey, sceneLoad);
        if (!started.Success)
            _notices.Failed(
                started.Detail ?? "The scene could not be loaded.");
    }

    private void Dispatch(TileMenuAction action, int index)
    {
        var tile = _vm.Tiles[index];
        var path = tile.ThumbKey;
        switch (action)
        {
            case TileMenuAction.Apply:
                ActivateTile(index);
                break;
            case TileMenuAction.Spawn:
                Spawn(index);
                break;
            case TileMenuAction.Favorite:
            {
                // The set follows the clicked tile: all on, or all off.
                bool favorite = !tile.Favorite;
                foreach (var target in VerbTargets(index))
                    SetFavorite(target, favorite);
                break;
            }
            case TileMenuAction.Retry:
                RetryProbe(path);
                break;
            case TileMenuAction.Quarantine:
                QuarantineFile(path);
                break;
            case TileMenuAction.EditMetadata:
                OpenMetadataEditor(index);
                break;
            case TileMenuAction.Rename:
                OpenRename(path);
                break;
            case TileMenuAction.Reveal:
                RevealFile(path);
                break;
            case TileMenuAction.Delete:
            {
                var targets = VerbTargets(index);
                _deletePath = path;
                _deleteMore.Clear();
                foreach (var target in targets)
                    if (target != index)
                        _deleteMore.Add(_vm.Tiles[target].ThumbKey);
                _deleteName = targets.Count > 1
                    ? $"{targets.Count} files"
                    : System.IO.Path.GetFileName(path);
                _deleteOpen = true;
                break;
            }
        }
    }

    // ── the recovery and authoring verbs ─────────────────────────────────
    // Disk work happens on the click, exactly as an apply's file load does;
    // every outcome is TYPED and is announced through the notification
    // channel, and a successful mutation asks the scan for a fresh complete
    // pass rather than editing the published snapshot.

    private void RetryProbe(string path)
    {
        var result = PoseLibraryFileActions.Default.Probe(path);
        if (!result.Succeeded)
        {
            _notices.Failed("Retry", result.Detail);
            return;
        }
        // A clean read says nothing: the badge that prompted the retry simply
        // goes, which IS the answer (user 2026-08-14 — the confirmations were
        // restating what the tile already showed). Only a still-bad read has
        // something the tile cannot say on its own.
        if (result.ProbeStatus != PoseLibraryMetadataStatus.Valid)
            _notices.Failed(
                "Retry: "
                + StatusText(result.ProbeStatus!.Value, result.Detail));
        // Either way the badge restates the CURRENT truth.
        _library.RequestScan();
    }

    private void QuarantineFile(string path)
    {
        var result = PoseLibraryFileActions.Default.Quarantine(path);
        if (!result.Succeeded)
        {
            _notices.Failed("Quarantine", result.Detail);
            return;
        }
        FavoritePathChanged(path, null);
        _notices.Done("Moved into "
            + PoseLibraryFileActions.QuarantineFolderName + ".");
        _library.RequestScan();
    }

    private ContextMenuItem[] BuildMoveSubmenu(string path)
    {
        _moveDestinations.Clear();
        _movePath = path;
        var items = new List<ContextMenuItem>();
        string current = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        var sources = _config.Config.Library.Sources;
        foreach (var folder in _library.Snapshot.Folders)
        {
            int bar = folder.Key.IndexOf('|');
            if (bar < 0
                || !int.TryParse(folder.Key.AsSpan(0, bar), out int source)
                || source < 0 || source >= sources.Count)
                continue;
            var relative = folder.Key[(bar + 1)..];
            var directory = relative.Length == 0
                ? sources[source].Path
                : System.IO.Path.Combine(sources[source].Path, relative);
            if (string.Equals(directory, current, StringComparison.OrdinalIgnoreCase))
                continue;
            string root = string.IsNullOrWhiteSpace(sources[source].Name)
                ? $"Source {source + 1}"
                : sources[source].Name;
            _moveDestinations.Add(directory);
            items.Add(new ContextMenuItem(
                relative.Length == 0 ? root : root + "\\" + relative,
                TablerIcon.Folder));
        }
        if (items.Count == 0)
            items.Add(new ContextMenuItem(
                "No other folder", TablerIcon.Folder, disabled: true));
        return items.ToArray();
    }

    private readonly List<string> _deleteMore = new();
    private readonly List<string> _moveMore = new();

    private void MoveToFolder(string path, string destination)
    {
        _movePath = null;
        foreach (var more in _moveMore)
        {
            var moved = PoseLibraryFileActions.Default.Move(more, destination);
            if (moved.Succeeded)
                FavoritePathChanged(more, moved.ResultPath);
            else
                _notices.Failed("Move", moved.Detail);
        }
        _moveMore.Clear();
        var result = PoseLibraryFileActions.Default.Move(path, destination);
        if (result.Succeeded)
        {
            FavoritePathChanged(path, result.ResultPath);
            _library.RequestScan();
        }
        else
            _notices.Failed("Move", result.Detail);
    }

    /// <summary>Opens Explorer with the file selected. A refusal is stated,
    /// never swallowed — the shell can decline.</summary>
    private void RevealFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                _notices.Refused("Reveal: the file no longer exists.");
                return;
            }
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            _notices.Failed("Reveal", ex.Message);
        }
    }

    /// <summary>Favourites key on the absolute path, so a path-changing verb
    /// carries the favourite along (or drops it with the file). Saves only
    /// when something actually changed.</summary>
    private void FavoritePathChanged(string oldPath, string? newPath)
    {
        var favorites = _config.Config.Library.Favorites;
        if (!favorites.Remove(oldPath))
            return;
        if (newPath is not null)
            favorites.Add(newPath);
        _config.Save();
    }

    // ── the file modals ──────────────────────────────────────────────────

    /// <summary>Strips every character Windows refuses in a file NAME —
    /// typed or pasted, the input simply never holds one (the export
    /// modal's rule).</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) < 0)
            return name;
        var kept = new char[name.Length];
        int count = 0;
        foreach (var c in name)
            if (Array.IndexOf(invalid, c) < 0)
                kept[count++] = c;
        return new string(kept, 0, count);
    }

    /// <summary>The file rename: the shared name prompt with the library's
    /// rules — a name is required, an existing sibling is never overwritten,
    /// the extension is kept. A success rescans.</summary>
    private void OpenRename(string path)
    {
        string folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        string extension = System.IO.Path.GetExtension(path);
        string checkedCandidate = string.Empty;
        bool taken = false;
        _renameModal.Open(
            "Rename file",
            System.IO.Path.GetFileNameWithoutExtension(path),
            name =>
            {
                var result = PoseLibraryFileActions.Default.Rename(path, name);
                if (result.Succeeded)
                {
                    FavoritePathChanged(path, result.ResultPath);
                    _library.RequestScan();
                }
                else
                    _notices.Failed("Rename", result.Detail);
            },
            confirm: "Rename",
            validate: name =>
            {
                string trimmed = name.Trim();
                if (trimmed.Length == 0)
                    return "A name is required.";
                string candidate = System.IO.Path.Combine(folder, trimmed + extension);
                // The disk is asked once per candidate, not once per frame.
                if (!string.Equals(candidate, checkedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    checkedCandidate = candidate;
                    taken = !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
                        && System.IO.File.Exists(candidate);
                }
                return taken ? "That name already exists here." : null;
            },
            sanitize: SanitizeFileName,
            placeholder: "File name");
    }

    /// <summary>The metadata modal: author and comma-separated tags, written
    /// back into the pose file through the atomic store (Brio's
    /// SaveMetadata flow). The core normalizes the tags; the typed outcome
    /// lands in the note.</summary>
    private void DrawMetadataModal()
    {
        if (!_metaOpen)
            return;
        Crystarium.Modal(
            "##library-metadata",
            _metaOpen,
            next => _metaOpen = next,
            "Edit metadata",
            height: 400f,
            body: () =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance = (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Author", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-author", _metaAuthor,
                next => _metaAuthor = next,
                placeholder: "Author");
            ImGui.Dummy(new Vector2(0f, rowGap));

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                "Tags (comma-separated)", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-tags", _metaTags,
                next => _metaTags = next,
                placeholder: "tag, tag");
            ImGui.Dummy(new Vector2(0f, rowGap));

            // Description and the preview image have serialized on both sides
            // since the format existed; only the editor was missing.
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Description", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-description", _metaDescription,
                next => _metaDescription = next,
                placeholder: "What this pose is for");
            ImGui.Dummy(new Vector2(0f, rowGap));

            bool willHaveImage = _metaImage.Remove
                ? false
                : _metaImage.Base64 is { Length: > 0 } || _metaHadImage;
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                willHaveImage ? "Preview image: stored" : "Preview image: none",
                captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    willHaveImage ? "Replace image" : "Add image",
                    style: pairStyle,
                    id: "library-metadata-image-set"))
                _metaImageBrowser.Open(
                    _lastImageFolder,
                    chosen =>
                    {
                        _lastImageFolder =
                            System.IO.Path.GetDirectoryName(chosen)
                            ?? _lastImageFolder;
                        var read = PoseLibraryFileActions.ReadPreviewImage(
                            chosen, out var encoded);
                        if (read.Succeeded && encoded is { Length: > 0 })
                            _metaImage = PosePreviewImageEdit.Set(encoded);
                        else
                            _notices.Failed("Preview image", read.Detail);
                    });
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Remove image",
                    style: pairStyle,
                    disabled: !willHaveImage,
                    id: "library-metadata-image-clear"))
                _metaImage = PosePreviewImageEdit.Cleared;
            ImGui.Dummy(new Vector2(0f, rowGap));

            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    id: "library-metadata-confirm"))
            {
                var result = PoseLibraryFileActions.Default.EditMetadata(
                    _metaPath,
                    _metaAuthor,
                    _metaTags.Split(','),
                    _metaDescription,
                    _metaImage);
                if (result.Succeeded)
                {
                    // The thumbnail cache keys on the path, and the path did
                    // not change: an edited preview would keep drawing the
                    // image the file no longer carries. Only the visible page
                    // decodes again, and only when the image was actually
                    // touched — an author or tag edit leaves the grid alone.
                    if (_metaImage.Remove || _metaImage.Base64 is { Length: > 0 })
                        _thumbs.Clear();
                    _library.RequestScan();
                }
                else
                    _notices.Failed("Metadata", result.Detail);
                _metaOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-metadata-cancel"))
                _metaOpen = false;
        });
    }

    /// <summary>The delete confirm: destructive, so it is never a bare menu
    /// click. Deleting an auto-save re-enumerates that tab; anything else
    /// rescans the library.</summary>
    private void DrawDeleteModal()
    {
        if (!_deleteOpen)
            return;
        Crystarium.Modal(
            "##library-delete",
            _deleteOpen,
            next => _deleteOpen = next,
            "Delete file",
            height: 180f,
            body: () =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance = (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), _deleteName,
                new TextStyle
                {
                    Size = theme.Typography.BodySize,
                    Color = theme.Text,
                });
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                "This permanently deletes the file from disk.",
                captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            ImGui.Dummy(new Vector2(0f, rowGap));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    "Delete",
                    variant: ButtonVariant.Danger,
                    style: pairStyle,
                    id: "library-delete-confirm"))
            {
                // The rest of a bulk delete goes first; the clicked file's
                // own outcome is the one reported below.
                foreach (var more in _deleteMore)
                {
                    var gone = PoseLibraryFileActions.Default.Delete(more);
                    if (gone.Succeeded)
                        FavoritePathChanged(more, null);
                    else
                        _notices.Failed("Delete", gone.Detail);
                }
                _deleteMore.Clear();
                var result = PoseLibraryFileActions.Default.Delete(_deletePath);
                if (result.Succeeded)
                {
                    FavoritePathChanged(_deletePath, null);
                    if (_type == LibraryType.AutoSaves)
                        _autoDirty = true;
                    else
                        _library.RequestScan();
                }
                else
                    _notices.Failed("Delete", result.Detail);
                _deleteOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-delete-cancel"))
                _deleteOpen = false;
        });
    }

    /// <summary>
    /// Resize stepping: a drag on the window edge reflows the pane only at
    /// <see cref="ResizeStep"/> boundaries — per-pixel reflow of the grid
    /// while dragging cost whole frames. Two constraints shape it. Only the
    /// WIDTH is stepped: the width is what lays the grid's columns out, while
    /// the height only moves the clipper — and stepping the height bounced
    /// the footer block off the true bottom edge by up to a step, which read
    /// as the footer flipping between one and two rows. And stepping engages
    /// AND releases on a <see cref="DragStreakFrames"/> streak: pointer
    /// deltas arrive in bursts, so releasing on the first delta-free frame
    /// flapped the layout between the exact and the floored size for the
    /// whole drag. A one-off change (entering the mode, a snapped window)
    /// never engages and adopts the exact size immediately.
    /// </summary>
    private Vector2 StepResize(Vector2 size)
    {
        bool moved = size != _handedSize;
        _handedSize = size;
        if (moved)
        {
            _stillStreak = 0;
            if (++_changedStreak >= DragStreakFrames)
                _stepping = true;
        }
        else if (++_stillStreak >= DragStreakFrames)
        {
            _stepping = false;
            _changedStreak = 0;
        }

        if (!_stepping)
        {
            _layoutX = size.X;
            _steppedX = 0f;
            return size;
        }

        float step = ResizeStep * ImGuiHelpers.GlobalScale;
        float stepped = MathF.Floor(size.X / step) * step;
        // Only cross a boundary; a sub-step wiggle keeps the standing layout
        // as long as it still fits the handed rect.
        if (stepped != _steppedX || _layoutX > size.X)
        {
            _steppedX = stepped;
            _layoutX = MathF.Min(MathF.Max(stepped, 1f), size.X);
        }
        return new Vector2(_layoutX, size.Y);
    }

    /// <summary>The workspace moved on. The decoded thumbnails are a cache of
    /// what was on screen, and the icon size is persisted here rather than on
    /// every drag tick.</summary>
    public void OnHidden()
    {
        _showing = false;
        _thumbs.Clear();
        // The hidden actor and its render target are the library's own cost;
        // leaving the mode gives them back.
        ClosePreview();
        if (!_iconSizeDirty)
            return;
        // The slider writes the config live so the grid reflows with the drag;
        // the disk write waits for the surface to close.
        _iconSizeDirty = false;
        _config.Save();
    }

    /// <summary>The first frame of a library session.</summary>
    private void Enter()
    {
        // Nothing is scanned until a surface asks, so the first entry is what
        // pays for the file system.
        _library.RequestScan();

        // The type is a browsing mode, not a preference: every entry starts on
        // the poses, which is also what the import redirect expects to land on.
        _type = LibraryType.Poses;
        _collapsed.Clear();
        _autoDirty = true;

        // The query and the tag are DRAFTS: they mean nothing outside the open
        // surface, so each entry starts on the whole library.
        ResetFilters();
        _lastAppliedTile = -1;
        _vm.IconSize = _config.Config.Library.IconSize;
        _iconSizeDirty = false;

        // Favourites and sources may have moved while the mode was away, and a
        // completed scan keeps its revision: rebuild unconditionally.
        _seenRevision = -1;
        _refilter = true;
    }

    /// <summary>The drafts, which no view of the library inherits from
    /// another.</summary>
    private void ResetFilters()
    {
        _vm.Query = string.Empty;
        _query = string.Empty;
        _queryLower = string.Empty;
        _vm.ActiveTag = null;
        _tagLower = null;
        _vm.SelectedFolder = 0;
        _rangeStart = -1;
        _rangeEnd = -1;
    }

    /// <summary>The active library type as an index (Poses/Auto-saves/MCDF).
    /// The shell's tab strip states it while the mode is on.</summary>
    public int SelectedType => (int)_type;

    /// <summary>The selected tile's file path, or null — the library
    /// window's footer and preview state their content off it.</summary>
    public string? SelectedPath
        => _vm.Selected >= 0 && _vm.Selected < _vm.Tiles.Count
            ? _vm.Tiles[_vm.Selected].ThumbKey
            : null;

    /// <summary>A shell tab. The filters are drafts of the view being left, so
    /// the new type starts on its whole library.</summary>
    public void SelectType(int index)
    {
        if (index < 0 || index > (int)LibraryType.Objects
            || index == (int)_type)
            return;
        _type = (LibraryType)index;
        ResetFilters();
        _lastAppliedTile = -1;
        ClearTileSelection();
        // Each type builds its OWN rail and tiles from the same snapshot, so
        // both paths have to rebuild rather than refilter.
        _seenRevision = -1;
        _autoDirty = true;
        _refilter = true;
    }

    private void Refresh()
    {
        if (_type == LibraryType.AutoSaves)
            _autoDirty = true;
        else
            _library.RequestScan();
    }

    // ── the rows ─────────────────────────────────────────────────────────

    /// <summary>Rebuilds the rail and the tiles from a new snapshot. Every
    /// string a frame reads is minted HERE, the count readouts included.
    /// </summary>
    private void SyncSnapshot()
    {
        var snapshot = _library.Snapshot;
        if (snapshot.Revision == _seenRevision)
            return;
        _seenRevision = snapshot.Revision;

        var favorites = _config.Config.Library.Favorites;
        var entries = snapshot.Entries;
        var kind = _type switch
        {
            LibraryType.Mcdf => PoseLibraryEntryKind.Mcdf,
            LibraryType.Scenes => PoseLibraryEntryKind.Scene,
            LibraryType.Objects => PoseLibraryEntryKind.Actor,
            _ => PoseLibraryEntryKind.Pose,
        };

        int total = 0;
        int favored = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!InTab(entries[i].Kind, kind)
                || !KindAdmitted(entries[i].Kind, kind))
                continue;
            total++;
            if (favorites.Contains(entries[i].FilePath))
                favored++;
        }

        // The two synthetic heads are positional by contract: [0] "All …",
        // [1] "Favorites", the scan's own folders after them.
        var folders = _vm.Folders;
        folders.Clear();
        folders.Add(new PoseLibraryFolderRow
        {
            Key = AllKey,
            Label = kind switch
            {
                PoseLibraryEntryKind.Mcdf => AllFilesLabel,
                PoseLibraryEntryKind.Scene => AllScenesLabel,
                _ => AllLabel,
            },
            LabelLower = "all",
            Depth = 0,
            Count = total,
            CountText = Count(total),
        });
        folders.Add(new PoseLibraryFolderRow
        {
            Key = FavoritesKey,
            Label = FavoritesLabel,
            LabelLower = "favorites",
            Depth = 0,
            Count = favored,
            CountText = Count(favored),
        });

        var scanned = snapshot.Folders;
        if (_folderRows.Length < scanned.Count)
            _folderRows = new int[Math.Max(scanned.Count, 32)];
        for (int i = 0; i < scanned.Count; i++)
        {
            var folder = scanned[i];
            int count = _type switch
            {
                LibraryType.Mcdf => folder.McdfCount,
                LibraryType.Scenes => folder.SceneCount,
                LibraryType.Objects => folder.ObjectsCount,
                _ => folder.PoseCount,
            };
            // A subfolder with none of this kind has no descendant of it
            // either, so dropping it drops a WHOLE subtree — the flattened tree
            // stays depth-first and the subtree range test stays a range test.
            // Source roots always list, exactly as the scan itself keeps them.
            if (folder.Depth > 0 && count == 0)
            {
                _folderRows[i] = -1;
                continue;
            }
            _folderRows[i] = folders.Count;
            folders.Add(new PoseLibraryFolderRow
            {
                Key = folder.Key,
                Label = folder.Label,
                LabelLower = folder.LabelLower,
                Depth = folder.Depth,
                Count = count,
                CountText = Count(count),
            });
        }

        var tiles = _vm.Tiles;
        tiles.Clear();
        _tileTags.Clear();
        _tileAuthors.Clear();
        _tileStatus.Clear();
        _tileKinds.Clear();
        _tileModified.Clear();
        _tileContents.Clear();
        // Labels are minted with or without the extension HERE; the search
        // keeps matching the bare name either way.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;
        foreach (var entry in Ordered(entries, kind))
        {
            _tileTags.Add(entry.TagsLower);
            _tileAuthors.Add(entry.AuthorLower);
            _tileStatus.Add(entry.MetadataStatus);
            _tileKinds.Add(entry.Kind);
            _tileModified.Add(entry.ModifiedText);
            _tileContents.Add(entry.SceneContents);
            bool flagged =
                entry.MetadataStatus != PoseLibraryMetadataStatus.Valid;
            // Minted ONCE. A scene's key and its heading are the same run —
            // the split exists because a folder's differ — and this loop runs
            // per entry per rebuild, where a second identical string is a
            // defect rather than a rounding error.
            string section = kind == PoseLibraryEntryKind.Scene
                ? SceneSectionLabel(entry)
                : string.Empty;
            tiles.Add(new PoseLibraryTileRow
            {
                Id = entry.FilePath,
                Label = _builtExtensions
                    ? entry.Name + System.IO.Path.GetExtension(entry.FilePath)
                    : entry.Name,
                LabelLower = entry.NameLower,
                // A scene's own line is what is INSIDE it — "3 actors, 2
                // lights" tells a user which scene this is; a stamp does not.
                Sub = entry.SceneContents.Length > 0
                    ? entry.SceneContents
                    : entry.ModifiedText,
                ThumbKey = entry.FilePath,
                HasThumbnail = entry.HasThumbnail,
                Favorite = favorites.Contains(entry.FilePath),
                Fallback = entry.Kind switch
                {
                    PoseLibraryEntryKind.Mcdf => TablerIcon.UserCircle,
                    PoseLibraryEntryKind.Scene => TablerIcon.Movie,
                    PoseLibraryEntryKind.Actor => TablerIcon.User,
                    PoseLibraryEntryKind.Light => TablerIcon.Bulb,
                    PoseLibraryEntryKind.Camera => TablerIcon.Camera,
                    PoseLibraryEntryKind.Environment => TablerIcon.Sun,
                    PoseLibraryEntryKind.Overlay => TablerIcon.Message,
                    PoseLibraryEntryKind.Group => TablerIcon.Folder,
                    PoseLibraryEntryKind.WorldObject => TablerIcon.Plant,
                    PoseLibraryEntryKind.Prop => TablerIcon.Moneybag,
                    _ => entry.IsLegacy
                        ? TablerIcon.File
                        : TablerIcon.Armature,
                },
                Author = entry.Author,
                Tags = entry.Tags,
                Folder = _folderRows[entry.Folder],
                SectionKey = section,
                SectionLabel = section,
                Flagged = flagged,
                StatusText = flagged
                    ? StatusText(entry.MetadataStatus, entry.MetadataDetail)
                    : string.Empty,
            });
        }

        // Row identity did not survive the rebuild, so neither does the
        // selection; a rail row that no longer exists falls back to "All".
        ClearTileSelection();
        _vm.ShowRail = true;
        _vm.RailHeads = 2;
        _vm.ShowNoSources = folders.Count <= 2;
        _vm.EmptyText = "No matches.";
        // The rows standing from here on are the SCANNED library's, so an
        // auto-save kick has to clear them rather than leave them showing
        // under a rail-less tab.
        _autoRows = false;
        if (_vm.SelectedFolder >= folders.Count)
            _vm.SelectedFolder = 0;
        SyncFolderRange();
        _refilter = true;
    }

    // ── the auto-save tab ────────────────────────────────────────────────
    // Read straight off IAutoSaveService.RootDirectory — retention, naming and
    // the write path all stay where they are; this only reads what the service
    // left. The read is SPLIT from the mint: enumerating a cold root, stamping
    // every file and formatting every string is disk work no frame may do, so
    // a worker produces a detached result and the draw thread only writes rows.

    /// <summary>One snapshot folder as the worker read it. Every string a mint
    /// reads is already formatted here, so the pass on the draw thread writes
    /// rows and looks favourites up and touches no file.</summary>
    private sealed class AutoSaveFolder
    {
        /// <summary>The snapshot folder itself — the row's provenance, and
        /// what the newest-first ordering broke its ties on.</summary>
        public required string Directory { get; init; }

        /// <summary>The day this snapshot's files were taken on — half of the
        /// rail row they fall under.</summary>
        public required string Day { get; init; }

        /// <summary>Its <c>.pose</c> files, already ordered. Never empty: a
        /// snapshot holding none is dropped by the worker, exactly as the
        /// synchronous build skipped it.</summary>
        public required List<AutoSaveEntry> Entries { get; init; }
    }

    /// <summary>One auto-saved pose, read and formatted off the draw thread.
    /// </summary>
    private sealed class AutoSaveEntry
    {
        public required string FilePath { get; init; }

        /// <summary>The bare file name. The label takes the extension back on
        /// only when the setting asks for it, and the search keeps matching
        /// this either way.</summary>
        public required string Name { get; init; }

        public required string NameLower { get; init; }

        /// <summary>The modified stamp, already formatted.</summary>
        public required string Stamp { get; init; }

        /// <summary>Where the file says it was captured, or empty when it
        /// records no place. Read per FILE rather than per folder because a
        /// day folder spans a whole session: a snapshot taken in Limsa and one
        /// taken in Gridania land in the same folder and must not share a row.
        /// Empty is legacy — every auto-save written before 2026-08-14, and
        /// any file whose document no longer reads — and gathers under its day
        /// alone. No place is ever inferred.</summary>
        public required string Place { get; init; }
    }

    /// <summary>
    /// Starts an enumeration and puts the tab in the shape its rows will land
    /// in. The shape is not the worker's answer and cannot wait for one; the
    /// ROWS are, so a refresh leaves the standing ones alone and only a first
    /// entry — or an arrival from another type, whose rows are the wrong
    /// library's — clears down to the scanning line.
    /// </summary>
    private void KickAutoSaves()
    {
        _autoDirty = false;
        // Captured at the KICK, exactly where the synchronous build captured
        // it: the mint below reads this rather than a flag that crossed a
        // thread, and a setting flipped mid-flight re-dirties on its own.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;

        // The rail is the auto-save tab's structure too now, with ONE head:
        // a snapshot is not a curated entry, so there is no favourites row.
        _vm.ShowRail = true;
        _vm.RailHeads = 1;
        _vm.ShowNoSources = false;

        // Rows that stand keep their rail row and its span: a kick that leaves
        // them showing must not filter them out from under the user while the
        // worker runs.
        // The previous view stays on screen until the new one is ready to
        // present; only a scan still running after the grace shows the
        // Scanning state, so a fast scan never flashes an empty grid.
        _autoAwaitSince = ImGui.GetTime();
        _autoAwaiting = !_autoRows;

        _autoPending = true;

        // Read once, here: the worker is handed the root rather than the
        // service, so nothing off-thread reaches into a plugin service.
        string root = _autoSave.RootDirectory;
        lock (_autoSync)
        {
            if (_autoScanning)
            {
                _autoQueued = true;
                return;
            }
            _autoScanning = true;
        }

        _ = Task.Run(() => ScanAutoSaves(root));
    }

    /// <summary>The worker, mirroring <c>PoseLibraryService.ScanLoop</c>: a
    /// kick raised while a pass runs coalesces into exactly ONE more run
    /// afterwards, which is what keeps a held rescan from stacking workers.
    /// </summary>
    private void ScanAutoSaves(string root)
    {
        while (true)
        {
            List<AutoSaveFolder> read;
            try
            {
                read = ReadAutoSaves(root);
            }
            catch (Exception)
            {
                // A missing or unreadable root is an empty tab, not a failure —
                // and a result has to land either way, or the tab would sit on
                // its scanning caption with the rescan disabled forever.
                read = [];
            }

            Volatile.Write(ref _autoResult, read);

            lock (_autoSync)
            {
                if (!_autoQueued)
                {
                    _autoScanning = false;
                    return;
                }
                _autoQueued = false;
            }
        }
    }

    /// <summary>
    /// The enumeration itself: the snapshot folders under the auto-save root,
    /// their <c>.pose</c> files, and every string a row will show. Nothing it
    /// returns is shared with the pane, so a result outliving the mode owns
    /// only its own strings.
    /// </summary>
    private static List<AutoSaveFolder> ReadAutoSaves(string root)
    {
        var snapshots = new List<(string Directory, DateTime At)>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
                snapshots.Add((directory, SafeFolderTime(directory)));
        }
        catch (Exception)
        {
            // A missing or unreadable root is an empty tab, not a failure.
        }

        // Newest first, ties on name descending: the order the service's own
        // retention uses, so what the browser lists last is what it prunes.
        snapshots.Sort(static (a, b) =>
        {
            int byDate = b.At.CompareTo(a.At);
            return byDate != 0
                ? byDate
                : string.CompareOrdinal(b.Directory, a.Directory);
        });

        var read = new List<AutoSaveFolder>(snapshots.Count);
        var files = new List<string>();
        foreach (var (directory, _) in snapshots)
        {
            files.Clear();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (System.IO.Path.GetExtension(file).Equals(
                            PoseExtension, StringComparison.OrdinalIgnoreCase))
                        files.Add(file);
            }
            catch (Exception)
            {
            }

            if (files.Count == 0)
                continue;
            // Newest first, by the save time; the name only breaks ties.
            files.Sort((a, b) =>
            {
                int byTime = SafeFileTime(b).CompareTo(SafeFileTime(a));
                return byTime != 0
                    ? byTime
                    : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            var entries = new List<AutoSaveEntry>(files.Count);
            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                entries.Add(new AutoSaveEntry
                {
                    FilePath = file,
                    Name = name,
                    NameLower = name.ToLowerInvariant(),
                    Stamp = SafeFileTime(file).ToString(
                        StampFormat, CultureInfo.InvariantCulture),
                    Place = SafePlace(file),
                });
            }

            read.Add(new AutoSaveFolder
            {
                Directory = directory,
                Day = SnapshotDay(directory),
                Entries = entries,
            });
        }

        return read;
    }

    /// <summary>Polls the worker's completed slot. An idle frame costs one
    /// volatile read: no lock, no allocation, nothing to drain.</summary>
    private const double PresentGraceSeconds = 0.3;
    private double _autoAwaitSince;
    private bool _autoAwaiting;

    /// <summary>The Scanning state, shown only when a scan outlives the
    /// presentation grace: the rail and the grid empty, the word in the
    /// middle.</summary>
    private void ShowAutoSavesScanning()
    {
        _autoAwaiting = false;
            _rangeStart = -1;
            _rangeEnd = -1;
            _vm.SelectedFolder = 0;
            _vm.Folders.Clear();
            _vm.Tiles.Clear();
            _tileTags.Clear();
            _tileAuthors.Clear();
            _tileStatus.Clear();
            _tileKinds.Clear();
            ClearTileSelection();
            _vm.EmptyText = ScanningText;
            _refilter = true;
            }

    private void TakeAutoSaves()
    {
        if (Volatile.Read(ref _autoResult) is null)
        {
            if (_autoAwaiting && ImGui.GetTime() - _autoAwaitSince >= PresentGraceSeconds)
                ShowAutoSavesScanning();
            return;
        }
        _autoAwaiting = false;
        // Interlocked rather than a plain null-out: a pass finishing between
        // the read above and the clear is then picked up on the next frame
        // instead of being overwritten.
        if (Interlocked.Exchange(ref _autoResult, null) is not { } scan)
            return;
        MintAutoSaves(scan);
    }

    /// <summary>
    /// The rows, from what the worker read. The tab's structure is its RAIL:
    /// one head, then one row per DAY AND PLACE — "2026-08-14 – Limsa
    /// Lominsa" — because a day folder spans a whole session and a session
    /// visits more than one zone. The grid keeps tiles only; selecting a rail
    /// row is what filters them.
    ///
    /// <para>Rows appear in FIRST-ENCOUNTER order, which is the scan's own
    /// newest-first order, so the newest day leads and the newest place inside
    /// a day leads. Tiles stay in pure scan order — an auto-save browser is a
    /// recovery tool, so "All auto-saves" must read newest-first rather than
    /// in place blocks. A row's tiles are therefore NOT contiguous, which is
    /// fine: the folder filter is a range test over the ROW index, never over
    /// tile positions.</para>
    ///
    /// <para>List writes, a favourites lookup and a dictionary probe only —
    /// every string a row shows was minted by the worker.</para>
    /// </summary>
    private void MintAutoSaves(List<AutoSaveFolder> scan)
    {
        _autoPending = false;
        _autoRows = true;

        // The rail row the user was standing on, held by KEY: the rows are
        // rebuilt from scratch on every pass, so an index would silently point
        // at a different day after a prune.
        string? held = _vm.SelectedFolder > 0 && _vm.SelectedFolder < _vm.Folders.Count
            ? _vm.Folders[_vm.SelectedFolder].Key
            : null;

        var favorites = _config.Config.Library.Favorites;
        var folders = _vm.Folders;
        var tiles = _vm.Tiles;
        folders.Clear();
        tiles.Clear();
        _tileTags.Clear();
        _tileAuthors.Clear();
        _tileStatus.Clear();

        // Run -> rail row index, for this pass only: a mint runs on tab
        // entry and on an explicit rescan, never per frame. A run is the
        // saves of one day at one place, in time order: leaving for another
        // place and coming back makes two rows, so the rail reads as time.
        int run = 0;
        string? runDay = null;
        string? runPlace = null;
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);

        int total = 0;
        for (int s = 0; s < scan.Count; s++)
            total += scan[s].Entries.Count;

        // One synthetic head, positional by the rail's own contract.
        folders.Add(new PoseLibraryFolderRow
        {
            Key = AllKey,
            Label = AllAutoSavesLabel,
            LabelLower = "all",
            Depth = 0,
            Count = total,
            CountText = Count(total),
        });

        for (int s = 0; s < scan.Count; s++)
        {
            var snapshot = scan[s];
            var entries = snapshot.Entries;
            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                if (!string.Equals(runDay, snapshot.Day, StringComparison.Ordinal)
                    || !string.Equals(runPlace, entry.Place, StringComparison.Ordinal))
                {
                    run++;
                    runDay = snapshot.Day;
                    runPlace = entry.Place;
                }
                string key = snapshot.Day + KeySeparator + entry.Place
                    + KeySeparator + run.ToString(CultureInfo.InvariantCulture);
                if (!rows.TryGetValue(key, out int group))
                {
                    group = folders.Count;
                    rows.Add(key, group);
                    folders.Add(new PoseLibraryFolderRow
                    {
                        Key = key,
                        // A file that records no place claims nothing about
                        // where it was taken: it reads as the bare day.
                        Label = entry.Place.Length > 0
                            ? snapshot.Day + PlaceSeparator + entry.Place
                            : snapshot.Day,
                        LabelLower = string.Empty,
                        Depth = 0,
                    });
                }

                folders[group].Count++;
                _tileTags.Add(Array.Empty<string>());
                _tileAuthors.Add(string.Empty);
                _tileStatus.Add(PoseLibraryMetadataStatus.Valid);
                tiles.Add(new PoseLibraryTileRow
                {
                    Id = entry.FilePath,
                    Label = _builtExtensions
                        ? entry.Name + PoseExtension
                        : entry.Name,
                    LabelLower = entry.NameLower,
                    Sub = entry.Stamp,
                    ThumbKey = entry.FilePath,
                    // An auto-save is a normal export, so it carries whatever
                    // preview the exporter wrote; the cache probes once and
                    // memoizes a file without one.
                    HasThumbnail = true,
                    Favorite = favorites.Contains(entry.FilePath),
                    Folder = group,
                });
            }
        }

        // A row's total is known only once every file has landed in it, so the
        // readouts are minted here. The head already carries its own.
        for (int i = 1; i < folders.Count; i++)
            folders[i].CountText = Count(folders[i].Count);

        ClearTileSelection();
        _vm.SelectedFolder =
            held is not null && rows.TryGetValue(held, out int standing)
                ? standing
                : 0;
        _vm.ShowRail = true;
        _vm.RailHeads = 1;
        _vm.ShowNoSources = false;
        _vm.EmptyText = NoAutoSavesText;
        SyncFolderRange();
        _refilter = true;
    }

    /// <summary>The day header a snapshot groups under: the local date of the
    /// folder's own UTC stamp. A folder the collision suffix renamed does not
    /// parse and falls back to its write time.</summary>
    private static string SnapshotDay(string directory)
    {
        var name = System.IO.Path.GetFileName(directory);

        // The per-day layout: the folder name IS the (local) day. Read off the
        // NAME rather than through the mtime fallback, which a later prune
        // deleting siblings inside the folder would silently bump — then
        // restated in the caption's own format, which is not the disk's.
        if (DateTime.TryParseExact(
                name,
                SnapshotDayFolderFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var named))
            return named.ToString(DayFormat, CultureInfo.InvariantCulture);

        var time = DateTime.TryParseExact(
            name,
            SnapshotFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime()
            : SafeFolderTime(directory).ToLocalTime();
        return time.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    private static DateTime SafeFolderTime(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static DateTime SafeFileTime(string file)
    {
        try
        {
            return File.GetLastWriteTime(file);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// Where an auto-saved pose says it was taken. Read through the ordinary
    /// pose codec's own metadata probe — the same seam the scanned library
    /// indexes every <c>.pose</c> with, so the auto-save tab is not a second
    /// JSON contract. Worker thread only: the probe validates a whole bounded
    /// document per file.
    ///
    /// <para>Anything that does not answer a place is EMPTY, never a guess: a
    /// file written before auto-saves recorded one, and a file whose document
    /// no longer reads, are both "no place recorded" and gather under the day
    /// alone.</para>
    /// </summary>
    private static string SafePlace(string file)
    {
        try
        {
            var metadata = AtomicPoseFileStore.Default.ReadMetadata(file);
            return metadata.Succeeded ? metadata.PlaceName ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The tab's entries in the order the grid must receive them: scan order
    /// for every kind whose sections ARE its folders, and SECTION order for
    /// scenes, whose sections are place-and-day pairs no directory records.
    /// Newest day leads, places sort inside a day, and the newest file leads
    /// inside a place. Ordering here rather than in the grid is what keeps the
    /// section break a single key comparison.
    /// </summary>
    /// <summary>Whether an entry kind belongs to the current tab. The
    /// Objects tab is the one MANY-kind tab; every other tab is one kind.
    /// </summary>
    private static bool InTab(
        PoseLibraryEntryKind entryKind, PoseLibraryEntryKind primary) =>
        primary == PoseLibraryEntryKind.Actor
            ? entryKind is PoseLibraryEntryKind.Actor
                or PoseLibraryEntryKind.Light
                or PoseLibraryEntryKind.Camera
                or PoseLibraryEntryKind.Environment
                or PoseLibraryEntryKind.Overlay
                or PoseLibraryEntryKind.Group
                or PoseLibraryEntryKind.WorldObject
                or PoseLibraryEntryKind.Prop
            : entryKind == primary;

    private IEnumerable<PoseLibraryEntry> Ordered(
        IReadOnlyList<PoseLibraryEntry> entries, PoseLibraryEntryKind kind)
    {
        var matching = entries.Where(entry =>
            InTab(entry.Kind, kind) && KindAdmitted(entry.Kind, kind));
        return kind == PoseLibraryEntryKind.Scene
            ? matching
                .OrderByDescending(entry => SceneDay(entry).Date)
                .ThenBy(entry => entry.ScenePlace, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(entry => SceneDay(entry))
            : matching;
    }

    /// <summary>One scene section's heading: where it was captured and on
    /// which day. A scene written before scenes recorded a place falls back to
    /// the DAY ALONE — the place is not guessed, and such files gather under a
    /// heading that claims nothing about where they were taken.</summary>
    private static string SceneSectionLabel(PoseLibraryEntry entry)
    {
        string day = SceneDay(entry).ToString(DayFormat, CultureInfo.InvariantCulture);
        return entry.ScenePlace.Length > 0
            ? entry.ScenePlace + PlaceSeparator + day
            : day;
    }

    /// <summary>The day a scene claims it was taken on. The heading pairs a
    /// place the DOCUMENT recorded with a day, so the day comes from the
    /// document too wherever it answers; a file's mtime is when the file last
    /// changed, which a copy or a sync moves. The mtime is the fallback for a
    /// scene that records no capture time, never a preference — and the grid's
    /// own ordering reads this same day, so heading and order cannot
    /// disagree.</summary>
    private static DateTime SceneDay(PoseLibraryEntry entry) =>
        entry.SceneCapturedAt?.ToLocalTime().DateTime ?? entry.Modified;

    private void SyncQuery()
    {
        if (string.Equals(_query, _vm.Query, StringComparison.Ordinal))
            return;
        _query = _vm.Query;
        // Lowercased ONCE per query change; the scan below compares ordinal
        // against names that were lowercased when the snapshot was built.
        _queryLower = _query.Trim().ToLowerInvariant();
        _refilter = true;
    }

    /// <summary>The selected rail row's descendant span. Depth-first flattening
    /// makes a subtree contiguous, so a folder test is a range test rather than
    /// a walk. A synthetic head has no span — it is not a filter — and how many
    /// heads there are is the rail's own, per-tab, count.</summary>
    private void SyncFolderRange()
    {
        int selected = _vm.SelectedFolder;
        var folders = _vm.Folders;
        if (selected < _vm.RailHeads || selected >= folders.Count)
        {
            _rangeStart = -1;
            _rangeEnd = -1;
            return;
        }
        int depth = folders[selected].Depth;
        int end = folders.Count;
        for (int i = selected + 1; i < folders.Count; i++)
        {
            if (folders[i].Depth > depth)
                continue;
            end = i;
            break;
        }
        _rangeStart = selected;
        _rangeEnd = end;
    }

    /// <summary>
    /// The visible list, refilled in place, and the groups it falls into. A
    /// query searches the WHOLE library and ignores the folder tree — a name is
    /// looked for, not a place — while Favorites and the tag chip are filters
    /// and compose with it.
    ///
    /// <para>Grouping is free here: the tiles are already ordered by folder, so
    /// a group closes the moment the folder changes and its count is known
    /// without a second pass.</para>
    /// </summary>
    private void Refilter()
    {
        _refilter = false;
        var visible = _vm.Visible;
        var tiles = _vm.Tiles;
        var groups = _vm.Groups;
        visible.Clear();

        bool query = _queryLower.Length > 0;
        int folder = _vm.SelectedFolder;
        int selected = _vm.Selected;
        bool kept = false;
        int groupCount = 0;
        int openFolder = -1;
        string openSection = string.Empty;
        PoseLibraryGroupRow? open = null;
        // Scenes section by where and when they were captured, not by the
        // directory they happen to sit in; the rail still filters by folder,
        // so the two structures coexist rather than replace each other.
        bool sectioned = _type == LibraryType.Scenes;
        // Only a rail that HAS a favourites head can be standing on it; the
        // auto-save rail's row 1 is a day and a place.
        int heads = _vm.RailHeads;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (heads > 1 && folder == 1 && !tile.Favorite)
                continue;
            if (query)
            {
                // A query is a lookup across everything the entry SAYS about
                // itself: the name, the author, and the tags — all matched
                // against runs the snapshot already lowercased.
                if (!tile.LabelLower.Contains(_queryLower, StringComparison.Ordinal)
                    && !_tileAuthors[i].Contains(_queryLower, StringComparison.Ordinal)
                    && !AnyTagContains(_tileTags[i], _queryLower))
                    continue;
            }
            else if (folder >= heads)
            {
                int row = tile.Folder;
                if (row < _rangeStart || row >= _rangeEnd)
                    continue;
            }
            if (_tagLower is { Length: > 0 } tag
                && !HasTag(_tileTags[i], tag))
                continue;

            if (sectioned)
            {
                if (open is null || !string.Equals(
                        tile.SectionKey, openSection, StringComparison.Ordinal))
                {
                    openSection = tile.SectionKey;
                    open = GroupSlot(groups, groupCount++);
                    open.Key = tile.SectionKey;
                    open.Label = tile.SectionLabel;
                    open.Collapsed =
                        _collapsed.TryGetValue(tile.SectionKey, out var held)
                        && held;
                    open.Start = visible.Count;
                    open.Count = 0;
                }
            }
            else if (open is null || tile.Folder != openFolder)
            {
                openFolder = tile.Folder;
                var source = _vm.Folders[openFolder];
                open = GroupSlot(groups, groupCount++);
                open.Key = source.Key;
                open.Label = source.Label;
                open.Collapsed = _collapsed.TryGetValue(source.Key, out var set)
                    && set;
                open.Start = visible.Count;
                open.Count = 0;
            }
            open.Count++;

            if (i == selected)
                kept = true;
            visible.Add(i);
        }

        if (groups.Count > groupCount)
            groups.RemoveRange(groupCount, groups.Count - groupCount);
        for (int g = 0; g < groupCount; g++)
            groups[g].CountText = Count(groups[g].Count);

        // One group states nothing the rail has not already said — and the
        // auto-save tab now says ALL of it in its rail, so its grid keeps
        // tiles only. A scene's place-and-day section is the one header no
        // rail states, since the scene rail is still the directory tree.
        _vm.Grouped = _type != LibraryType.AutoSaves
            && (groupCount > 1 || sectioned);
        _vm.LayoutRevision++;

        // A selection the filter dropped is no longer on screen, so it stops
        // being what the action row would act on.
        if (!kept)
            ClearTileSelection();
    }

    /// <summary>The group row for a slot, reused in place: a refilter mints
    /// only the count readouts, never the rows.</summary>
    private static PoseLibraryGroupRow GroupSlot(
        List<PoseLibraryGroupRow> groups, int index)
    {
        if (index < groups.Count)
            return groups[index];
        var row = new PoseLibraryGroupRow();
        groups.Add(row);
        return row;
    }

    private void ToggleGroup(int index)
    {
        if (index < 0 || index >= _vm.Groups.Count)
            return;
        var group = _vm.Groups[index];
        bool collapsed = !group.Collapsed;
        group.Collapsed = collapsed;
        _collapsed[group.Key] = collapsed;
        _vm.LayoutRevision++;
    }

    private static bool HasTag(IReadOnlyList<string> tagsLower, string tagLower)
    {
        for (int i = 0; i < tagsLower.Count; i++)
            if (string.Equals(tagsLower[i], tagLower, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The query's tag test: a SUBSTRING match, unlike the tag
    /// chip's exact filter — a query is a lookup, a chip is a selection.
    /// </summary>
    private static bool AnyTagContains(
        IReadOnlyList<string> tagsLower, string queryLower)
    {
        for (int i = 0; i < tagsLower.Count; i++)
            if (tagsLower[i].Contains(queryLower, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The flagged tile's minted diagnosis: the classification word,
    /// then the codec's own detail.</summary>
    private static string StatusText(
        PoseLibraryMetadataStatus status, string detail)
    {
        var word = status switch
        {
            PoseLibraryMetadataStatus.Corrupt => "Unreadable",
            PoseLibraryMetadataStatus.Future => "Unsupported version",
            PoseLibraryMetadataStatus.Oversized => "Too large",
            _ => string.Empty,
        };
        return detail.Length == 0
            ? word
            : word.Length == 0
                ? detail
                : word + ": " + detail;
    }

    /// <summary>
    /// Whether the footer belongs to a live character-file apply. Every
    /// conjunct earns its place: the MCDF TAB is the only surface that
    /// starts one, so no other tab may have its caption or its action row
    /// taken over (a tab added later is excluded by construction); the
    /// single-flight transaction must be an IMPORT rather than an export;
    /// and its receipt must still be Pending, because a terminal outcome
    /// reports through the note like every other result.
    /// </summary>
    internal static bool ShowsImportCancel(
        LibraryType type,
        bool busy,
        McdfProgress? progress,
        OperationReceipt? receipt) =>
        type == LibraryType.Mcdf
        && busy
        && progress is { Kind: McdfOperationKind.Import }
        && receipt is { State: OperationReceiptState.Pending };

    private void SyncStatus()
    {
        // Each tab states its OWN enumeration. The auto-save tab browses no
        // scanned source, so the library scan is neither its state nor a
        // reason to refuse its rescan — its own worker is both.
        bool scanning = _type == LibraryType.AutoSaves
            ? _autoPending
            : _library.IsScanning;
        _vm.IsScanning = scanning;

        // A character file applied from HERE runs as a long transaction, and
        // the appearance pane's progress row is a pane away. State the live
        // phase and offer the stop on the surface that started it. The MCDF
        // TAB is the only surface that can start one, so it is the only one
        // that may claim the footer: without that conjunct this hijacks the
        // Poses and Auto-saves captions and buries the auto-save health line.
        var running = _integration.Mcdf;
        bool importing = ShowsImportCancel(
            _type, _integration.McdfBusy, running, _integration.McdfReceipt);
        _vm.ShowCancelImport = importing;
        // The stop greys — never vanishes — through Committing and Rolling
        // back, matching the appearance pane's progress row.
        _vm.CanCancelImport = importing && running!.Cancellable;
        if (importing)
        {
            _vm.Status = $"{AppearancePane.PhaseLabel(running!.Phase)} {running.FileName}";
            return;
        }

        // The auto-save tab's idle caption is a REFUSAL channel, nothing
        // more: it speaks only when auto-save needs recovery, because that is
        // the one state where a silent footer would hide that the tab's own
        // source has stopped filling. A healthy cadence says nothing, so this
        // tab's footer reads exactly like every other tab's.
        if (_type == LibraryType.AutoSaves && !scanning)
        {
            _vm.Status = AutoSaveRecoveryText();
            return;
        }

        // No counter (user: pointless beside the single action row) — the
        // caption carries only the scan state, and notes above win.
        _vm.Status = scanning ? ScanningText : string.Empty;
    }

    /// <summary>The auto-save tab's ONE caption, minted once per observation
    /// CHANGE: the recovery-required detail, or nothing. Health narration —
    /// the last-success stamp, the off state, the empty-session line — was
    /// removed (user 2026-08-14): a cadence that is working is not news, and
    /// stating it gave this tab a bar of chrome no other tab carries. A
    /// recovery obligation stays, because a tab whose source has stopped
    /// filling must not look merely empty.</summary>
    private string AutoSaveRecoveryText()
    {
        var record = _autoSave.LastHealthRecord;
        var terminal = _autoSave.LastTerminalResult;
        var key = (record?.UpdatedUtc, record?.Status, terminal.Status);
        if (key == _autoStatusKey)
            return _autoStatusText;
        _autoStatusKey = key;

        string text;
        if (terminal.Status == AutoSaveTerminalStatus.RecoveryRequired)
            text = "Auto-save needs recovery: "
                + Trim(terminal.Detail, "see the health record.");
        else if (record is { Status: AutoSaveHealthStatus.RecoveryRequired })
            text = "Auto-save needs recovery: "
                + Trim(record.Detail ?? record.FailurePhase, "see the health record.");
        else
            text = string.Empty;
        return _autoStatusText = text;

        // The health record's detail is bounded at 4096; the footer is one
        // caption line.
        static string Trim(string? detail, string fallback)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return fallback;
            return detail.Length <= 160 ? detail : detail[..160] + "…";
        }
    }

    // ── the target actor ─────────────────────────────────────────────────

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

    /// <summary>
    /// The inspector rail's live preview. The service owns the hidden actor,
    /// the camera and the render, and the inspector section draws it; this only
    /// says WHEN it is wanted, WHICH pose it shows and WHOSE appearance it
    /// borrows. Every gate is here rather than in either drawing surface:
    /// neither has any idea what an MCDF entry is.
    /// </summary>
    private void SyncPreview()
    {
        // A WHITELIST, stated as one: every tab whose entries are pose files —
        // auto-saves included, whose tiles key on the .pose path exactly as
        // the library's do. Written as an exclusion this silently admitted the
        // next tab added; scenes did exactly that, feeding .xivs scene paths
        // into the pose preview binder. Character files never travel the
        // import pipeline, and a scene is not a pose file at all: it has no
        // single skeleton to stand on a preview body.
        _vm.PreviewAvailable = _type is LibraryType.Poses or LibraryType.AutoSaves;
        SyncCharacterFile();
        // No eye anymore (user 2026-08-11): the preview is always live on a
        // tab that can preview, so availability alone gates it.
        bool wanted = _vm.PreviewAvailable
            && _vm.Selected >= 0
            && _vm.Selected < _vm.Tiles.Count;
        var source = wanted ? PreviewSource() : null;

        // The claim is stated whether or not this pane gets to act on it: the
        // import dialog drives the SAME service while it is open, and what it
        // does with the seat when it closes depends on whether anyone else
        // still wants it.
        _files.SetPreviewClaim(source is not null);
        if (_files.IsImportPreviewActive)
        {
            // Stood down, NOT closed — the dialog is driving. The pose it put
            // there is not this pane's to remember, so the binder forgets it
            // and re-states the frame the seat comes back. The rail's preview
            // block STAYS UP as a read-only mirror of what the dialog shows
            // (user 2026-08-10: it vanished for the whole dialog session) —
            // same service, same texture, camera commands included — gated on
            // the user's own preview eye so a switched-off preview does not
            // reappear just because the dialog previews for itself. A tile
            // selection is deliberately NOT required: the dialog's highlight
            // is the subject, not this pane's.
            _previewBinder.StandDown();
            _files.SetPreviewVisible(_vm.PreviewAvailable);
            return;
        }

        if (source is null)
        {
            // The seat STAYS on a preview-capable tab: the empty well and its
            // reason are the affordance that a preview exists at all (user
            // 2026-08-11: nothing indicated one until a tile was clicked).
            // Only the MCDF tab, which can never preview, drops the section.
            _previewBinder.Close();
            _files.SetPreviewVisible(
                _vm.PreviewAvailable,
                wanted
                    ? "No actor to preview on."
                    : "Select a pose to preview.");
            return;
        }

        // The rail's own option menus can pose the target under the preview
        // (a rest preset, "From clipboard"), and so can this pane's applies:
        // either way the stance the binder rebases onto has moved and has to
        // be read again. One pull per frame, no wiring between the surfaces.
        if (_files.TargetPoseRevision != _seenPoseRevision)
        {
            _seenPoseRevision = _files.TargetPoseRevision;
            _previewBinder.InvalidateBaseline();
        }

        var path = _vm.Tiles[_vm.Selected].ThumbKey;
        // The candidate is the FILE-FREE half of the real build, so the poll
        // costs no read per frame; the real build happens only when the binder
        // says something moved — the file load, the expression routing and the
        // filter governance all stay in the one place.
        if (_previewBinder.Begin(
                source, path, PosePreviewBinder.Trim(BuildImportOptionsCore())))
            _previewBinder.Pose(
                path, PosePreviewBinder.Trim(BuildImportOptions(path)));

        // The seat is the inspector rail's, so the section is told to show it;
        // the render and its status are read there, straight off the service.
        _files.SetPreviewVisible(true);
    }

    /// <summary>One highlighted character file's own account of itself, and
    /// the path it belongs to. Replaced WHOLE from the reading task, never
    /// field by field, so the draw thread either sees the previous highlight's
    /// finished state or this one's — the path is what it matches on.</summary>
    private sealed record CharacterFileState(
        string Path, McdfSummary? Summary, string? Status);

    private volatile CharacterFileState? _characterFile;

    /// <summary>
    /// The character-file tab's stand-in for the pose preview: an MCDF cannot
    /// be rendered on the preview body (see
    /// <see cref="PoseFileInspectorSection.SetCharacterFile"/>), so the
    /// inspector shows what the package says about ITSELF instead.
    ///
    /// <para>The read is header-only and takes no actor, no operation
    /// directory and none of the single MCDF operation slot — a highlight may
    /// never spend the machinery an import needs. It still touches the disk,
    /// so it runs off the frame and the panel says it is reading until it
    /// lands; a highlight that moves on first is simply not adopted.</para>
    /// </summary>
    private void SyncCharacterFile()
    {
        string? path = _type == LibraryType.Mcdf
            && _vm.Selected >= 0 && _vm.Selected < _vm.Tiles.Count
            ? _vm.Tiles[_vm.Selected].ThumbKey
            : null;
        if (path == null)
        {
            _characterFile = null;
            _files.SetCharacterFile(null, null);
            return;
        }

        var state = _characterFile;
        if (state == null || !string.Equals(state.Path, path, StringComparison.Ordinal))
        {
            state = new CharacterFileState(path, null, "Reading the character file…");
            _characterFile = state;
            string reading = path;
            Task.Run(() =>
            {
                var read = _integration.ReadMcdfSummary(reading);
                var landed = new CharacterFileState(
                    reading,
                    read.Success ? read.Value : null,
                    read.Success ? null : read.Detail);
                // Only the highlight that asked for this read may adopt it.
                if (_characterFile is { } current
                    && string.Equals(current.Path, reading, StringComparison.Ordinal))
                    _characterFile = landed;
            });
        }
        _files.SetCharacterFile(state.Summary, state.Status);
    }

    /// <summary>The actor the preview borrows an appearance from: the
    /// selection's actor when it can be posed, else the first actor an apply
    /// would land on — the picker's own leading candidate.</summary>
    private IActor? PreviewSource() =>
        TargetActor() is { HasSkeleton: true } actor
            ? actor
            : FirstApplyTarget();

    /// <summary>Tears the preview down and takes its seat back off the
    /// inspector rail. Idempotent — the frame after a close must not close
    /// again, and the seat is withdrawn either way so the section never draws
    /// a preview this pane has stopped feeding.</summary>
    private void ClosePreview()
    {
        _previewBinder.Close();
        _files.SetPreviewVisible(false);
    }

    // ── the import components ────────────────────────────────────────────

    /// <summary>The action row's per-tab affordances. NO component toggles
    /// live here: the inspector owns which of position/rotation/scale a pose
    /// applies, and an auto-save restore is full-fidelity by contract, so the
    /// second set this row used to carry was both redundant and the reason
    /// that one tab's footer read taller than every other (user 2026-08-14).
    /// </summary>
    private void SyncImportToggles()
    {
        // Favorites are the poses library's — an auto-save snapshot is not a
        // curated entry.
        _vm.ShowImportMenus = false;
        _vm.CanFavorite = _type == LibraryType.Poses;
        // A scene is not spawned as an actor, and saving one belongs where
        // scenes are found rather than behind a menu.
        _vm.ShowSpawn =
            _type is not LibraryType.Scenes and not LibraryType.Objects;
        // Scenes and objects both choose WHERE a load lands (ruled
        // 2026-08-31): the same four-mode dropdown, the same preference.
        // No probe here any more — nothing gates on saved anchors since
        // the centroid fallback made every mode honourable.
        if (_type is LibraryType.Objects or LibraryType.Scenes)
        {
            BuildPlacementChoices();
            _vm.PlacementOptions = _placementChoiceLabels.ToArray();
            _vm.PlacementSelected =
                _placementChoices.IndexOf(EffectiveMode());
            _vm.OnPlacement = next =>
            {
                if (next >= 0 && next < _placementChoices.Count)
                    _placement.Mode = _placementChoices[next];
            };
        }
        else
        {
            _vm.PlacementOptions = null;
        }
        _vm.ShowSaveScene = _type == LibraryType.Scenes;
        _vm.SceneBusy = _scenes.Busy;
        _vm.ShowEditMetadata = _type == LibraryType.Poses;
        _vm.CanEditMetadata = CanEditMetadata(_vm.Selected);
    }

    /// <summary>The library's own import options: full scope with the active
    /// tab's component toggles, and never a bone filter — a catalog apply is
    /// whole-file. A library or auto-save apply has LOAD semantics: clean
    /// slate in scope, then the file — prior in-scope edits (including the
    /// position residue a rotation-only import can never repair) must not
    /// survive a load. The FILES dialog keeps its own explicit "Reset first"
    /// checkbox for opt-in layering; its scope/expression drafts likewise
    /// never reach a library apply.</summary>
    private PoseImportOptions BuildImportOptions(string path)
    {
        // Brio's .cmp preset substitution (FileUIHelpers.cs:690) reaches the
        // library too — its file list carries .cmp entries, and Brio's own
        // library apply goes through the same popup dispatch. The library's
        // load semantics still ride on top.
        if (_files.CmpImportOverride(path, out _, out _) is { } cmp)
        {
            cmp.ResetBeforeImport = true;
            return cmp;
        }

        var options = BuildImportOptionsCore();
        // Smart expression routing (Brio ResolveSmartImport): a face-only
        // .pose can NEVER land through the body path — Dawntrail faces are
        // posed through bone POSITIONS the body path masks — and the library
        // has no import-type control, so such a file applies as an
        // expression; the engine then forces every component exactly as
        // Brio's ExpressionOptions does. The reset keeps expression scope:
        // face bones, never the head. Unconditional — the tile apply has no
        // type control, so this routing is structural, not the import
        // menu's Smart checkbox.
        if (path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) &&
            PoseFile.Load(path) is { } file &&
            PoseFileService.IsExpressionOnlyPose(file))
        {
            // Re-derived as the Expression type pair, never patched onto this
            // build: a rail with Body checked has the face already excluded,
            // and setting AsExpression over that applies nothing (see
            // PoseFileInspectorSection.RouteAsType).
            options = _files.RouteAsType(
                options, body: false, expression: true);
        }
        return options;
    }

    /// <summary>The UI-derived half of <see cref="BuildImportOptions"/>: the
    /// active tab's toggles, the load semantics and the bone-filter
    /// governance. Free of file I/O by contract — the preview polls it every
    /// frame to notice an option change — and free of side effects: the files
    /// section's own build only reads its checkbox state.</summary>
    private PoseImportOptions BuildImportOptionsCore()
    {
        // Poses apply with the SHARED menu options (the rail hosts them in
        // library mode) plus the library's load semantics; an auto-save
        // restore is full-fidelity — every component, no control to say
        // otherwise, because a recovery that silently dropped a component
        // would not be a recovery.
        var options = _type == LibraryType.AutoSaves
            ? new PoseImportOptions
            {
                ApplyPosition = true,
                ApplyRotation = true,
                ApplyScale = true,
            }
            : _files.BuildImportOptions();
        options.ResetBeforeImport = true;
        // The bone filter is NOT re-applied here: the files section's own
        // build already folds it in for the one state Brio lets it govern
        // (neither type checked), and folding it in again would have shrunk
        // a Body/Expression import the same way Brio's disabled Custom Import
        // Options button forbids. An auto-save restore is full-fidelity by
        // contract and never sees the filter at all.
        return options;
    }

    // ── the grid's actions ───────────────────────────────────────────────

    private void Select(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        _vm.SelectedSet.Clear();
        _vm.SelectedSet.Add(index);
        _vm.Selected = index;
        EnrichTile(index);
    }

    private void ClearTileSelection()
    {
        _vm.Selected = -1;
        _vm.SelectedSet.Clear();
    }

    /// <summary>Ctrl toggles a tile in the set; Shift selects the range from
    /// the primary; a plain click selects the tile alone. The primary is
    /// the tile the rail describes.</summary>
    private void SelectWith(int index, bool ctrl, bool shift)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        if (shift && _vm.Selected >= 0 && _vm.Selected < _vm.Tiles.Count)
        {
            int from = Math.Min(_vm.Selected, index);
            int to = Math.Max(_vm.Selected, index);
            if (!ctrl)
                _vm.SelectedSet.Clear();
            for (int i = from; i <= to; i++)
                _vm.SelectedSet.Add(i);
            EnrichTile(index);
            return;
        }
        if (ctrl)
        {
            if (!_vm.SelectedSet.Remove(index))
            {
                _vm.SelectedSet.Add(index);
                _vm.Selected = index;
                EnrichTile(index);
            }
            else if (_vm.Selected == index)
                _vm.Selected = _vm.SelectedSet.Count > 0 ? _vm.SelectedSet.Max() : -1;
            return;
        }
        Select(index);
    }

    private void MarqueeSelect(IReadOnlyList<int> caught, bool additive)
    {
        if (caught.Count == 0)
            return;
        if (!additive)
            _vm.SelectedSet.Clear();
        foreach (var index in caught)
            _vm.SelectedSet.Add(index);
        _vm.Selected = caught[caught.Count - 1];
        EnrichTile(_vm.Selected);
    }

    /// <summary>The tiles a verb acts on: the whole set when the tile
    /// clicked is in it, else that tile alone.</summary>
    private List<int> VerbTargets(int index)
    {
        if (_vm.SelectedSet.Contains(index) && _vm.SelectedSet.Count > 1)
            return _vm.SelectedSet.OrderBy(i => i).ToList();
        return [index];
    }

    /// <summary>The information a selection wants — author, tags, status,
    /// what a scene holds — is read from the file when the tile is
    /// SELECTED, never at scan time. The read runs off the UI thread and
    /// its answer is applied to the tile on the next frame.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<TileFacts> _enrichments = new();
    private readonly record struct TileFacts(
        string Id, string? Author, IReadOnlyList<string> Tags, string? Sub,
        PoseLibraryMetadataStatus Status, string Detail);

    private void EnrichTile(int index)
    {
        var tile = _vm.Tiles[index];
        if (tile.Enriched)
            return;
        tile.Enriched = true;
        string path = tile.Id;
        var kind = PoseLibraryService.KindOf(path);
        _ = Task.Run(() =>
        {
            try
            {
                if (kind == PoseLibraryEntryKind.Pose)
                {
                    var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
                    var (status, detail) = PoseLibraryFileActions.Classify(metadata);
                    _enrichments.Enqueue(new TileFacts(
                        path,
                        metadata.Succeeded ? metadata.Author : null,
                        metadata.Succeeded ? metadata.Tags : [],
                        null, status, detail));
                }
                else if (kind != PoseLibraryEntryKind.Mcdf)
                {
                    var metadata = SceneFileStore.Default.ReadMetadata(path);
                    var (status, detail) = PoseLibraryFileActions.Classify(metadata);
                    _enrichments.Enqueue(new TileFacts(
                        path,
                        metadata.Succeeded ? metadata.Author : null,
                        [],
                        metadata.Succeeded && kind == PoseLibraryEntryKind.Scene
                            ? PoseLibraryService.DescribeScene(metadata)
                            : null,
                        status, detail));
                }
            }
            catch (Exception)
            {
                // A file that cannot be read shows what the listing knew.
            }
        });
    }

    private void PumpEnrichments()
    {
        while (_enrichments.TryDequeue(out var facts))
        {
            for (int i = 0; i < _vm.Tiles.Count; i++)
            {
                var tile = _vm.Tiles[i];
                if (!string.Equals(tile.Id, facts.Id, StringComparison.Ordinal))
                    continue;
                tile.Author = facts.Author;
                tile.Tags = facts.Tags;
                if (facts.Sub is { Length: > 0 } sub)
                    tile.Sub = sub;
                if (facts.Status != PoseLibraryMetadataStatus.Valid)
                {
                    tile.Flagged = true;
                    tile.StatusText = StatusText(facts.Status, facts.Detail);
                }
                if (i < _tileStatus.Count)
                    _tileStatus[i] = facts.Status;
                if (i < _tileAuthors.Count)
                    _tileAuthors[i] = facts.Author?.ToLowerInvariant() ?? string.Empty;
                if (i < _tileTags.Count)
                    _tileTags[i] = facts.Tags.Select(tag => tag.ToLowerInvariant()).ToArray();
                break;
            }
        }
    }

    private void SelectFolder(int index)
    {
        if (index < 0 || index >= _vm.Folders.Count
            || index == _vm.SelectedFolder)
            return;
        _vm.SelectedFolder = index;
        SyncFolderRange();
        _refilter = true;
    }

    private void TagFilter(string? tag)
    {
        _vm.ActiveTag = tag;
        // Lowercased ONCE per change; the scan compares ordinal against tags
        // the snapshot already lowercased.
        _tagLower = tag?.ToLowerInvariant();
        _refilter = true;
    }

    private void SetIconSize(float size)
    {
        _vm.IconSize = size;
        int rounded = (int)MathF.Round(size);
        if (_config.Config.Library.IconSize == rounded)
            return;
        _config.Config.Library.IconSize = rounded;
        _iconSizeDirty = true;
    }

    private void ToggleFavorite(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        SetFavorite(index, !_vm.Tiles[index].Favorite);
    }

    private void SetFavorite(int index, bool favorite)
    {
        if (_type != LibraryType.Poses)
            return;
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var tile = _vm.Tiles[index];
        var favorites = _config.Config.Library.Favorites;
        if (tile.Favorite == favorite)
            return;
        if (favorite)
            favorites.Add(tile.ThumbKey);
        else
            favorites.Remove(tile.ThumbKey);
        tile.Favorite = favorite;

        // Favouriting is a deliberate act with no other write to ride on, so
        // it persists immediately.
        _config.Save();

        // Only the scanned rails carry the synthetic Favorites head; the
        // auto-save tab's row 1 is a day and a place.
        var row = _vm.RailHeads > 1 && _vm.Folders.Count > 1
            ? _vm.Folders[1]
            : null;
        if (row is not null)
        {
            row.Count += favorite ? 1 : -1;
            row.CountText = Count(row.Count);
        }
        if (_vm.SelectedFolder == 1)
            _refilter = true;
    }

    private void Apply(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        double now = ImGui.GetTime();
        if (index == _lastAppliedTile && now - _lastAppliedAt < ReactivationSwallow)
            return;
        _lastAppliedTile = index;
        _lastAppliedAt = now;

        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index);
            return;
        }

        if (TargetActor() is not { HasSkeleton: true } actor)
        {
            _notices.Refused("Select an actor to apply a pose to.");
            return;
        }
        ApplyTo(index, actor);
    }

    /// <summary>The one apply: a tile onto an EXPLICIT actor — the picker's
    /// choice or the double-click path's selection target.</summary>
    private void ApplyTo(int index, IActor actor)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index);
            return;
        }
        if (!actor.HasSkeleton)
        {
            _notices.Refused("That actor has no skeleton to pose.");
            return;
        }
        var path = _vm.Tiles[index].ThumbKey;
        // Brio's expression-only .cmp gate: reported, and NOT imported.
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }
        // The target's stance is about to change, so the preview's rebase
        // baseline is stale from this call on — the NEXT tile has to be shown
        // landing on this one, not on what stood before it.
        _previewBinder.InvalidateBaseline();
        if (_bindings.GetActorId(actor) is not { } expectedActor)
        {
            _notices.Failed("Apply: the actor could not be resolved.");
            return;
        }
        var result = _poseFacade.ImportPose(
            actor,
            path,
            BuildImportOptions(path),
            onReceipt: TrackImport(expectedActor));
        if (!result.Success)
            _notices.Failed(Failure(result));
        else if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    /// <summary>
    /// The MCDF apply: the SAME call the appearance pane's Import… dialog
    /// makes (<c>AppearancePane.OpenMcdfImport</c>), so a character file picked
    /// here travels the identical mods/appearance/body-scale pipeline. The
    /// session reports progress and every failure on its own surface; the
    /// notification only carries a refusal to start.
    /// </summary>
    private void ApplyCharacterFile(int index)
    {
        if (TargetActor() is not { } actor
            || _bindings.GetActorId(actor) is not { } id)
        {
            _notices.Refused("Select an actor to apply a character file to.");
            return;
        }
        var begun = _integration.BeginImport(id, _vm.Tiles[index].ThumbKey);
        if (!begun.Success)
            _notices.Failed("Import", begun.Detail);
    }

    private void Spawn(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count || _type == LibraryType.Mcdf)
            return;

        // The .cmp verdict is taken BEFORE the spawn: an import Brio would
        // refuse must not leave a spare actor standing in the scene.
        var path = _vm.Tiles[index].ThumbKey;
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }

        var spawned = _spawnService.SpawnNewActor(reserveCompanionSlot: false);
        if (spawned is null)
        {
            _notices.Failed("The actor could not be spawned.");
            return;
        }

        // The options are frozen HERE, at the click, so a toggle or tab
        // change made while the scene binds the new actor cannot retarget
        // the import.
        _pendingActor = spawned;
        _pendingPath = path;
        _pendingOptions = BuildImportOptions(path);
        _pendingFrames = 0;
        if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    /// <summary>Second half of <see cref="Spawn"/>: the scene has not rescanned
    /// at click time, so the new actor is selected and posed once the refresh
    /// has bound it. The pending state is cleared BEFORE the import, so no
    /// outcome can apply the same pose twice.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingActor is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
        {
            if (++_pendingFrames < PendingSpawnFrames)
                return;
            ClearPendingSpawn();
            _notices.Failed("Spawned actor never became ready.");
            return;
        }

        var path = _pendingPath!;
        var options = _pendingOptions!;
        ClearPendingSpawn();

        _selection.Select(SelectionId.ForActor(id));
        var result = _poseFacade.ImportPose(
            spawned,
            path,
            options,
            onReceipt: TrackImport(id));
        if (!result.Success)
            _notices.Failed(Failure(result));
    }

    private void ClearPendingSpawn()
    {
        _pendingActor = null;
        _pendingPath = null;
        _pendingOptions = null;
        _pendingFrames = 0;
    }

    private static string Failure(PoseEditResult result) =>
        "Apply: " + (result.Detail ?? "the pose could not be applied.");

    /// <summary>Resolves a tile's thumbnail. The view asks only for tiles that
    /// carry one, and a shared wrap must be re-resolved each frame, so this is
    /// a dictionary hit and nothing more.</summary>
    private PoseThumbnail ResolveThumbnail(string path)
    {
        var handle = _thumbs.Get(path, out var size);
        return handle == 0 ? default : new PoseThumbnail(handle, size);
    }
}
