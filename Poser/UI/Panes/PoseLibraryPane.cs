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

    /// <summary>The stamp every tile shows.</summary>
    private const string StampFormat = "yyyy-MM-dd HH:mm";

    /// <summary>The day part of an auto-save rail row and of a scene section.
    /// </summary>
    private const string DayFormat = "yyyy-MM-dd";

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
    }

    private readonly ConfigurationService _config;
    private readonly IPoseLibraryService _library;
    private readonly PoseThumbnailCache _thumbs;
    private readonly CleanPoseFacade _poseFacade;
    private readonly IActorSpawnService _spawnService;
    private readonly SceneWorkflow _scenes;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly ActorIntegrationSession _integration;
    private readonly IAutoSaveService _autoSave;
    private readonly PoseFileInspectorSection _files;
    private readonly IActorManager _actors;
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

    /// <summary>Why the last apply or spawn did nothing, or null. Cleared by
    /// the next one and by any filter change.</summary>
    private string? _note;


    private int _lastAppliedTile = -1;
    private double _lastAppliedAt;

    private bool _iconSizeDirty;

    // ── the tile context menu and its file actions ───────────────────────
    // The BINDER owns the tile menu now: its rows depend on the tab, the
    // entry's typed metadata status, and which authoring/recovery verbs
    // apply — none of which the view knows. Disk actions go through the
    // typed PoseLibraryFileActions verbs; every outcome lands in _note and a
    // successful mutation requests a rescan, never edits the snapshot.

    private const string TileMenuId = "##pose-library-tile-menu";
    private const string MoveMenuId = "##pose-library-move-menu";

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
    private bool _renameOpen;
    private string _renamePath = string.Empty;
    private string _renameName = string.Empty;
    private string _renameCandidate = string.Empty;
    private bool _renameTaken;

    private bool _metaOpen;
    private string _metaPath = string.Empty;
    private string _metaAuthor = string.Empty;
    private string _metaTags = string.Empty;

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
        SceneWorkflow scenes)
    {
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _poseFacade = poseFacade;
        _spawnService = spawnService;
        _scenes = scenes;
        _selection = selection;
        _bindings = bindings;
        _integration = integration;
        _autoSave = autoSave;
        _files = files;
        _actors = actors;
        _previewBinder = new PosePreviewBinder(preview, poseFacade);

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnSelectFolder = SelectFolder;
        _vm.OnToggleGroup = ToggleGroup;
        _vm.OnSelect = Select;
        // Every apply that HAS a target goes through the actor picker — one
        // workflow, the target always explicit (a lone eligible actor skips
        // the menu). A scene has no target and loads outright.
        _vm.OnApplyTile = ActivateTile;
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
            _applyMenuAnchor = Crystarium.ButtonSeat;
            _applyMenuRequested = true;
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
                _note = $"Apply: {receipt.Detail ?? receipt.State.ToString()}.";
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
        SyncImportToggles();
        SyncStatus();
        SyncPreview();

        // The grid reflows at resize steps; the bar rows track the live
        // width through ChromeWidth so their clusters do not jump.
        _vm.ChromeWidth = size.X;
        PoseLibraryView.Draw(_vm, origin, StepResize(size));
        DrawApplyMenu();
        DrawTileMenu();
        DrawMoveMenu();
        DrawRenameModal();
        DrawMetadataModal();
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
                    ? _config.GetDisplayName(id.LogicalId, Clean(actor.Name))
                    : Clean(actor.Name);
                items.Add(new ContextMenuItem(name, TablerIcon.UserPlus));
            }
            if (items.Count == 0)
            {
                _note = "No actor to apply to.";
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
        // A scene is restored whole into the session; it has no actor to be
        // applied TO, so it states the verb it actually performs.
        Row(TileMenuAction.Apply, new ContextMenuItem(
            scenes ? "Load scene" : "Apply",
            scenes ? TablerIcon.Movie : TablerIcon.Check,
            disabled: !_vm.CanApply));
        if (!scenes)
            Row(TileMenuAction.Spawn, new ContextMenuItem(
                "Spawn as new actor", TablerIcon.UserPlus,
                disabled: !_vm.CanSpawn));
        if (_vm.CanFavorite)
            Row(TileMenuAction.Favorite, new ContextMenuItem(
                tile.Favorite ? "Unfavorite" : "Favorite", TablerIcon.Star));

        bool poses = _type == LibraryType.Poses;
        var status = _tileStatus[index];
        if (poses && status != PoseLibraryMetadataStatus.Valid)
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
            // Valid only. Editing rewrites the whole document, and a Future
            // entry is one whose schema Poser has already said it does not
            // support; the core refuses it as well, this keeps the menu from
            // offering a verb that would only answer a refusal.
            if (CanEditMetadata(index))
                Row(TileMenuAction.EditMetadata, new ContextMenuItem(
                    "Edit metadata…", TablerIcon.FileText,
                    help: "Author and tags, written back into the file."));
            Row(TileMenuAction.Rename, new ContextMenuItem(
                "Rename…", TablerIcon.Edit));
            Row(TileMenuAction.MoveTo, new ContextMenuItem(
                "Move to folder…", TablerIcon.Folder));
        }

        Separator();
        Row(TileMenuAction.Reveal, new ContextMenuItem(
            "Reveal in Explorer", TablerIcon.ExternalLink));
        Row(TileMenuAction.Delete, new ContextMenuItem(
            "Delete…", TablerIcon.Trash, danger: true));

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
        _applyMenuAnchor = ImGui.GetMousePos();
        _applyMenuRequested = true;
    }

    /// <summary>Restores a highlighted scene through the ONE scene workflow —
    /// the same single-flight transaction the scene workspace starts, so a
    /// refusal reads the same on either surface.</summary>
    private void LoadScene(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var started = _scenes.BeginLoad(_vm.Tiles[index].ThumbKey);
        if (!started.Success)
            _note = started.Detail;
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
                ToggleFavorite(index);
                break;
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
                _renamePath = path;
                _renameName = System.IO.Path.GetFileNameWithoutExtension(path);
                _renameCandidate = string.Empty;
                _renameTaken = false;
                _renameOpen = true;
                break;
            case TileMenuAction.MoveTo:
                OpenMoveMenu(path);
                break;
            case TileMenuAction.Reveal:
                RevealFile(path);
                break;
            case TileMenuAction.Delete:
                _deletePath = path;
                _deleteName = System.IO.Path.GetFileName(path);
                _deleteOpen = true;
                break;
        }
    }

    // ── the recovery and authoring verbs ─────────────────────────────────
    // Disk work happens on the click, exactly as an apply's file load does;
    // every outcome is TYPED and lands in the footer note, and a successful
    // mutation asks the scan for a fresh complete pass rather than editing
    // the published snapshot.

    private void RetryProbe(string path)
    {
        var result = PoseLibraryFileActions.Default.Probe(path);
        if (!result.Succeeded)
        {
            _note = "Retry: " + result.Detail;
            return;
        }
        // A clean read says nothing: the badge that prompted the retry simply
        // goes, which IS the answer (user 2026-08-14 — the confirmations were
        // restating what the tile already showed). Only a still-bad read has
        // something the tile cannot say on its own.
        _note = result.ProbeStatus == PoseLibraryMetadataStatus.Valid
            ? null
            : "Retry: " + StatusText(result.ProbeStatus!.Value, result.Detail);
        // Either way the badge restates the CURRENT truth.
        _library.RequestScan();
    }

    private void QuarantineFile(string path)
    {
        var result = PoseLibraryFileActions.Default.Quarantine(path);
        if (!result.Succeeded)
        {
            _note = "Quarantine: " + result.Detail;
            return;
        }
        FavoritePathChanged(path, null);
        _note = "Moved into "
            + PoseLibraryFileActions.QuarantineFolderName + ".";
        _library.RequestScan();
    }

    /// <summary>The move-to submenu: every scanned folder except the file's
    /// own, labeled root-first so two same-named subfolders stay apart.
    /// Destinations are frozen at open, resolved from the CURRENT config by
    /// the snapshot's source index — a source deleted since the scan simply
    /// contributes no row.</summary>
    private void OpenMoveMenu(string path)
    {
        _moveDestinations.Clear();
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
            if (string.Equals(
                    directory, current, StringComparison.OrdinalIgnoreCase))
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
        {
            _note = "No other folder to move to.";
            return;
        }
        _movePath = path;
        Crystarium.FloatingMenu.Open(
            MoveMenuId, ImGui.GetMousePos(), items.ToArray());
    }

    private void DrawMoveMenu()
    {
        int clicked = Crystarium.FloatingMenu.Draw(MoveMenuId);
        if (clicked < 0 || clicked >= _moveDestinations.Count
            || _movePath is not { } path)
            return;
        _movePath = null;
        var result = PoseLibraryFileActions.Default.Move(
            path, _moveDestinations[clicked]);
        if (result.Succeeded)
        {
            FavoritePathChanged(path, result.ResultPath);
            _note = null;
            _library.RequestScan();
        }
        else
            _note = "Move: " + result.Detail;
    }

    /// <summary>Opens Explorer with the file selected. A refusal is stated,
    /// never swallowed — the shell can decline.</summary>
    private void RevealFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
            {
                _note = "Reveal: the file no longer exists.";
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
            _note = "Reveal: " + ex.Message;
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

    /// <summary>The rename modal: name input, inline validation (required,
    /// no silent overwrite), Rename/Cancel. The typed result lands in the
    /// footer note and a success rescans.</summary>
    private void DrawRenameModal()
    {
        if (!_renameOpen)
            return;
        Crystarium.Modal(
            "##library-rename",
            _renameOpen,
            next => _renameOpen = next,
            "Rename file",
            height: 200f,
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
                ImGui.GetCursorScreenPos(), "Name", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-rename-name", _renameName,
                next => _renameName = SanitizeFileName(next),
                placeholder: "File name");
            ImGui.Dummy(new Vector2(0f, rowGap));

            string trimmed = _renameName.Trim();
            string candidate = trimmed.Length == 0
                ? string.Empty
                : System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_renamePath) ?? string.Empty,
                    trimmed + System.IO.Path.GetExtension(_renamePath));
            if (!string.Equals(
                    candidate, _renameCandidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                _renameCandidate = candidate;
                _renameTaken = candidate.Length > 0
                    && !string.Equals(
                        candidate, _renamePath,
                        StringComparison.OrdinalIgnoreCase)
                    && System.IO.File.Exists(candidate);
            }
            string? problem = trimmed.Length == 0
                ? "A name is required."
                : _renameTaken
                    ? "That name already exists here."
                    : null;
            if (problem is not null)
            {
                Crystarium.TextAt(
                    ImGui.GetCursorScreenPos(), problem, captionStyle);
                ImGui.Dummy(new Vector2(1f, captionAdvance));
            }
            ImGui.Dummy(new Vector2(0f, rowGap));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    "Rename",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    disabled: problem is not null,
                    help: problem,
                    id: "library-rename-confirm"))
            {
                var result = PoseLibraryFileActions.Default.Rename(
                    _renamePath, trimmed);
                if (result.Succeeded)
                {
                    FavoritePathChanged(_renamePath, result.ResultPath);
                    _note = null;
                    _library.RequestScan();
                }
                else
                    _note = "Rename: " + result.Detail;
                _renameOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-rename-cancel"))
                _renameOpen = false;
        });
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
            height: 240f,
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

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    id: "library-metadata-confirm"))
            {
                var result = PoseLibraryFileActions.Default.EditMetadata(
                    _metaPath, _metaAuthor, _metaTags.Split(','));
                if (result.Succeeded)
                {
                    _note = null;
                    _library.RequestScan();
                }
                else
                    _note = "Metadata: " + result.Detail;
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
                var result = PoseLibraryFileActions.Default.Delete(_deletePath);
                if (result.Succeeded)
                {
                    FavoritePathChanged(_deletePath, null);
                    _note = null;
                    if (_type == LibraryType.AutoSaves)
                        _autoDirty = true;
                    else
                        _library.RequestScan();
                }
                else
                    _note = "Delete: " + result.Detail;
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
        _note = null;
    }

    /// <summary>The active library type as an index (Poses/Auto-saves/MCDF).
    /// The shell's tab strip states it while the mode is on.</summary>
    public int SelectedType => (int)_type;

    /// <summary>A shell tab. The filters are drafts of the view being left, so
    /// the new type starts on its whole library.</summary>
    public void SelectType(int index)
    {
        if (index < 0 || index > (int)LibraryType.Scenes
            || index == (int)_type)
            return;
        _type = (LibraryType)index;
        ResetFilters();
        _lastAppliedTile = -1;
        _vm.Selected = -1;
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
            _ => PoseLibraryEntryKind.Pose,
        };

        int total = 0;
        int favored = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Kind != kind)
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
            int count = kind switch
            {
                PoseLibraryEntryKind.Mcdf => folder.McdfCount,
                PoseLibraryEntryKind.Scene => folder.SceneCount,
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
        // Labels are minted with or without the extension HERE; the search
        // keeps matching the bare name either way.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;
        foreach (var entry in Ordered(entries, kind))
        {
            _tileTags.Add(entry.TagsLower);
            _tileAuthors.Add(entry.AuthorLower);
            _tileStatus.Add(entry.MetadataStatus);
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
        _vm.Selected = -1;
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
        if (!_autoRows)
        {
            _rangeStart = -1;
            _rangeEnd = -1;
            _vm.SelectedFolder = 0;
            _vm.Folders.Clear();
            _vm.Tiles.Clear();
            _tileTags.Clear();
            _tileAuthors.Clear();
            _tileStatus.Clear();
            _vm.Selected = -1;
            _vm.EmptyText = ScanningText;
            _refilter = true;
        }

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
            files.Sort(StringComparer.OrdinalIgnoreCase);

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
    private void TakeAutoSaves()
    {
        if (Volatile.Read(ref _autoResult) is null)
            return;
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

        // Day-and-place -> rail row index, for this pass only: a mint runs on
        // tab entry and on an explicit rescan, never per frame.
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
                string key = snapshot.Day + KeySeparator + entry.Place;
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

        _vm.Selected = -1;
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

        // The per-day layout: the folder name IS the (local) day. Taken
        // verbatim rather than through the mtime fallback, which a later
        // prune deleting siblings inside the folder would silently bump.
        if (DateTime.TryParseExact(
                name,
                DayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            return name!;

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
    private static IEnumerable<PoseLibraryEntry> Ordered(
        IReadOnlyList<PoseLibraryEntry> entries, PoseLibraryEntryKind kind)
    {
        var matching = entries.Where(entry => entry.Kind == kind);
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
        _note = null;
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
            _vm.Selected = -1;
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

        if (_note is { } note)
        {
            _vm.Status = note;
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
        if (_type == LibraryType.Scenes)
        {
            _vm.CanApply = true;
            _vm.ApplyLabel = "Load scene";
            return;
        }

        // The primary opens the actor picker; its caption is constant.
        _vm.ApplyLabel = "Apply to";
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
        // next tab added; scenes did exactly that, feeding .poserscene paths
        // into the pose preview binder. Character files never travel the
        // import pipeline, and a scene is not a pose file at all: it has no
        // single skeleton to stand on a preview body.
        _vm.PreviewAvailable = _type is LibraryType.Poses or LibraryType.AutoSaves;
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

    /// <summary>Strips the raw object-index suffix ("Name (201)") the scene
    /// names carry, matching what every other surface displays.</summary>
    private static string Clean(string name)
    {
        int open = name.LastIndexOf('(');
        if (open <= 0 || name[^1] != ')')
            return name;
        for (int i = open + 1; i < name.Length - 1; i++)
            if (name[i] is < '0' or > '9')
                return name;
        return name.AsSpan(0, open).TrimEnd().ToString();
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
        _vm.ShowSpawn = _type != LibraryType.Scenes;
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
        _vm.Selected = index;
        _note = null;
    }

    private void SelectFolder(int index)
    {
        if (index < 0 || index >= _vm.Folders.Count
            || index == _vm.SelectedFolder)
            return;
        _vm.SelectedFolder = index;
        _note = null;
        SyncFolderRange();
        _refilter = true;
    }

    private void TagFilter(string? tag)
    {
        _vm.ActiveTag = tag;
        // Lowercased ONCE per change; the scan compares ordinal against tags
        // the snapshot already lowercased.
        _tagLower = tag?.ToLowerInvariant();
        _note = null;
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
        if (_type != LibraryType.Poses)
            return;
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        var tile = _vm.Tiles[index];
        var favorites = _config.Config.Library.Favorites;
        bool favorite = !tile.Favorite;
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
            _note = "Select an actor to apply a pose to.";
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
            _note = "That actor has no skeleton to pose.";
            return;
        }
        var path = _vm.Tiles[index].ThumbKey;
        // Brio's expression-only .cmp gate: reported, and NOT imported.
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _note = cmpNote;
            return;
        }
        // The target's stance is about to change, so the preview's rebase
        // baseline is stale from this call on — the NEXT tile has to be shown
        // landing on this one, not on what stood before it.
        _previewBinder.InvalidateBaseline();
        if (_bindings.GetActorId(actor) is not { } expectedActor)
        {
            _note = "Apply: the actor could not be resolved.";
            return;
        }
        var result = _poseFacade.ImportPose(
            actor,
            path,
            BuildImportOptions(path),
            onReceipt: TrackImport(expectedActor));
        _note = result.Success ? cmpNote : Failure(result);
    }

    /// <summary>
    /// The MCDF apply: the SAME call the appearance pane's Import… dialog
    /// makes (<c>AppearancePane.OpenMcdfImport</c>), so a character file picked
    /// here travels the identical mods/appearance/body-scale pipeline. The
    /// session reports progress and every failure on its own surface; the note
    /// only carries a refusal to start.
    /// </summary>
    private void ApplyCharacterFile(int index)
    {
        if (TargetActor() is not { } actor
            || _bindings.GetActorId(actor) is not { } id)
        {
            _note = "Select an actor to apply a character file to.";
            return;
        }
        var begun = _integration.BeginImport(id, _vm.Tiles[index].ThumbKey);
        _note = begun.Success ? null : "Import: " + begun.Detail;
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
            _note = cmpNote;
            return;
        }

        var spawned = _spawnService.SpawnNewActor(reserveCompanionSlot: false);
        if (spawned is null)
        {
            _note = "The actor could not be spawned.";
            return;
        }

        // The options are frozen HERE, at the click, so a toggle or tab
        // change made while the scene binds the new actor cannot retarget
        // the import.
        _pendingActor = spawned;
        _pendingPath = path;
        _pendingOptions = BuildImportOptions(path);
        _pendingFrames = 0;
        _note = cmpNote;
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
            _note = "Spawned actor never became ready.";
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
        _note = result.Success ? null : Failure(result);
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
