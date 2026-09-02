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
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Entities;
using Poser.Files;
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
public sealed partial class PoseLibraryPane
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

    private readonly ISceneLifecycleHistory _lifecycle;

    /// <summary>The standing load options, so a scene started from a TILE is
    /// the same load the scene workspace's dialog would have run.</summary>
    private readonly SceneLoadPreferences _sceneOptions;

    private readonly SelectionSession _selection;
    private readonly Game.Journal.DisruptiveSteps _disruptive;

    private readonly IEntityBindings _bindings;

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
        IEntityBindings bindings,
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
        ISceneLifecycleHistory lifecycle,
        UserNotices notices,
        Game.Journal.DisruptiveSteps disruptive)
    {
        _disruptive = disruptive;
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

    /// <summary>Resolves a tile's thumbnail. The view asks only for tiles that
    /// carry one, and a shared wrap must be re-resolved each frame, so this is
    /// a dictionary hit and nothing more.</summary>
    private PoseThumbnail ResolveThumbnail(string path)
    {
        var handle = _thumbs.Get(path, out var size);
        return handle == 0 ? default : new PoseThumbnail(handle, size);
    }
}
