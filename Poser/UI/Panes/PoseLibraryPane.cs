using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Integration;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Game.Preview;
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
    private const string FavoritesLabel = "Favorites";

    /// <summary>The auto-save folder name's own format
    /// (<c>AutoSaveService.CreateSnapshotFolder</c>), which is UTC.</summary>
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";

    /// <summary>The stamp every tile shows.</summary>
    private const string StampFormat = "yyyy-MM-dd HH:mm";

    /// <summary>The auto-save tab's day headers.</summary>
    private const string DayFormat = "yyyy-MM-dd";

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
    private enum LibraryType
    {
        Poses,
        AutoSaves,
        Mcdf,
    }

    private readonly ConfigurationService _config;
    private readonly IPoseLibraryService _library;
    private readonly PoseThumbnailCache _thumbs;
    private readonly CleanPoseFacade _poseFacade;
    private readonly IActorSpawnService _spawnService;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly ActorIntegrationSession _integration;
    private readonly IAutoSaveService _autoSave;
    private readonly PoseFileInspectorSection _files;
    private readonly IActorManager _actors;
    private readonly PoseLibraryViewModel _vm = new();
    private bool _applyMenuRequested;
    private readonly List<IActor> _applyTargets = new();

    /// <summary>Which library the tabs are showing. SESSION state: it is a
    /// browsing mode, not a preference, so it is never persisted and every
    /// entry starts on the poses.</summary>
    private LibraryType _type;

    /// <summary>The toggle row's import components, one set per tab. SESSION
    /// state like the FILES section's own toggles. The poses tab starts
    /// rotation-only — the pose import default; the auto-save tab starts with
    /// all three, because a restore reproduces what was saved. The MCDF tab
    /// has no set: character files never travel the pose import pipeline.
    /// </summary>
    // All components by default — Brio's REAL import path: with both
    // import types selected (the popup's normal state) it uses
    // DefaultIPCImporterOptions, TransformComponents.All on every bone with
    // the transform icons ignored (FileUIHelpers.cs:697-701). The
    // rotation-only default this replaced matched DefaultImporterOptions, a
    // fallback Brio's own popup path never takes; DT faces NEED positions.
    private bool _posesPosition = true;
    private bool _posesRotation = true;
    private bool _posesScale = true;
    private bool _autoPosition = true;
    private bool _autoRotation = true;
    private bool _autoScale = true;

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

    // The caption is a STRING PER COUNT, not per frame: it is rebuilt only when
    // the number it states or the mode it states it in changes.
    private string _caption = string.Empty;
    private int _captionCount = -1;
    private bool _captionScanning;

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
        PosePreviewService preview)
    {
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _poseFacade = poseFacade;
        _spawnService = spawnService;
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
        // Every apply goes through the actor picker — one workflow, the
        // target always explicit (a lone eligible actor skips the menu).
        _vm.OnApplyTile = index =>
        {
            Select(index);
            _applyMenuRequested = true;
        };
        _vm.OnSpawnTile = Spawn;
        _vm.OnToggleFavorite = ToggleFavorite;
        _vm.OnTagFilter = TagFilter;
        _vm.OnIconSize = SetIconSize;
        _vm.OnRefresh = Refresh;
        _vm.OnImportPosition = SetImportPosition;
        _vm.OnImportRotation = SetImportRotation;
        _vm.OnImportScale = SetImportScale;
        // The two Brio menus, opened from the toggle row; the shared state
        // lives on the FILES section so both surfaces read one filter. The
        // library mount opens the import menu WITHOUT presets — rest poses
        // belong to the actor part (user rule).
        _vm.OnImportMenu = () => _files.RequestImportMenu(withPresets: false);
        _vm.OnBoneFilterMenu = () => _files.RequestBoneFilterMenu();
        _vm.OnApplyMenu = () => _applyMenuRequested = true;
        _vm.OnOpenSettings = () => OnSettingsRequested?.Invoke();
        _vm.ResolveThumbnail = ResolveThumbnail;
        // Spawning needs no selection and no scene state; the service answers
        // null when the game refuses, which is a note rather than a gate.
        _vm.CanSpawn = true;
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
                "##library-apply-target", ImGui.GetMousePos(), items.ToArray());
        }

        int clicked = Crystarium.FloatingMenu.Draw("##library-apply-target");
        if (clicked >= 0 && clicked < _applyTargets.Count)
            ApplyTo(_vm.Selected, _applyTargets[clicked]);
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
        if (index < 0 || index > (int)LibraryType.Mcdf
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
        var kind = _type == LibraryType.Mcdf
            ? PoseLibraryEntryKind.Mcdf
            : PoseLibraryEntryKind.Pose;

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
            Label = kind == PoseLibraryEntryKind.Mcdf ? AllFilesLabel : AllLabel,
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
            int count = kind == PoseLibraryEntryKind.Mcdf
                ? folder.McdfCount
                : folder.PoseCount;
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
        // Labels are minted with or without the extension HERE; the search
        // keeps matching the bare name either way.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Kind != kind)
                continue;
            _tileTags.Add(entry.TagsLower);
            tiles.Add(new PoseLibraryTileRow
            {
                Id = entry.FilePath,
                Label = _builtExtensions
                    ? entry.Name + System.IO.Path.GetExtension(entry.FilePath)
                    : entry.Name,
                LabelLower = entry.NameLower,
                Sub = entry.ModifiedText,
                ThumbKey = entry.FilePath,
                HasThumbnail = entry.HasThumbnail,
                Favorite = favorites.Contains(entry.FilePath),
                Fallback = entry.Kind == PoseLibraryEntryKind.Mcdf
                    ? TablerIcon.UserCircle
                    : entry.IsLegacy
                        ? TablerIcon.File
                        : TablerIcon.Armature,
                Author = entry.Author,
                Tags = entry.Tags,
                Folder = _folderRows[entry.Folder],
            });
        }

        // Row identity did not survive the rebuild, so neither does the
        // selection; a rail row that no longer exists falls back to "All".
        _vm.Selected = -1;
        _vm.ShowRail = true;
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

        /// <summary>The day header this snapshot groups under.</summary>
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

        _vm.ShowRail = false;
        _vm.ShowNoSources = false;
        _vm.SelectedFolder = 0;
        _rangeStart = -1;
        _rangeEnd = -1;

        if (!_autoRows)
        {
            _vm.Folders.Clear();
            _vm.Tiles.Clear();
            _tileTags.Clear();
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
    /// The rows, from what the worker read. One header per DAY, not per
    /// snapshot (user call): the snapshots arrive newest-first, so a day is a
    /// contiguous run that closes when the date string changes, and each tile
    /// keeps its own full stamp. List writes and a favourites lookup only.
    /// </summary>
    private void MintAutoSaves(List<AutoSaveFolder> scan)
    {
        _autoPending = false;
        _autoRows = true;

        var favorites = _config.Config.Library.Favorites;
        var folders = _vm.Folders;
        var tiles = _vm.Tiles;
        folders.Clear();
        tiles.Clear();
        _tileTags.Clear();

        PoseLibraryFolderRow? dayRow = null;
        for (int s = 0; s < scan.Count; s++)
        {
            var snapshot = scan[s];
            if (dayRow is null
                || !string.Equals(dayRow.Key, snapshot.Day, StringComparison.Ordinal))
            {
                dayRow = new PoseLibraryFolderRow
                {
                    Key = snapshot.Day,
                    Label = snapshot.Day,
                    LabelLower = string.Empty,
                    Depth = 0,
                };
                folders.Add(dayRow);
            }

            var entries = snapshot.Entries;
            dayRow.Count += entries.Count;

            int group = folders.Count - 1;
            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                _tileTags.Add(Array.Empty<string>());
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

        // The day totals are known only once every snapshot has landed in
        // its run, so the readouts are minted here.
        for (int i = 0; i < folders.Count; i++)
            folders[i].CountText = Count(folders[i].Count);

        _vm.Selected = -1;
        _vm.SelectedFolder = 0;
        _vm.ShowRail = false;
        _vm.ShowNoSources = false;
        _vm.EmptyText = NoAutoSavesText;
        _rangeStart = -1;
        _rangeEnd = -1;
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

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

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
    /// a walk.</summary>
    private void SyncFolderRange()
    {
        int selected = _vm.SelectedFolder;
        var folders = _vm.Folders;
        if (selected < 2 || selected >= folders.Count)
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
        PoseLibraryGroupRow? open = null;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (folder == 1 && !tile.Favorite)
                continue;
            if (query)
            {
                if (!tile.LabelLower.Contains(_queryLower, StringComparison.Ordinal))
                    continue;
            }
            else if (folder >= 2)
            {
                int row = tile.Folder;
                if (row < _rangeStart || row >= _rangeEnd)
                    continue;
            }
            if (_tagLower is { Length: > 0 } tag
                && !HasTag(_tileTags[i], tag))
                continue;

            if (open is null || tile.Folder != openFolder)
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

        // One group states nothing the rail has not already said — except on
        // the auto-save tab, where the header IS the only structure.
        _vm.Grouped = groupCount > 1 || _type == LibraryType.AutoSaves;
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

    private void SyncStatus()
    {
        // Each tab states its OWN enumeration. The auto-save tab browses no
        // scanned source, so the library scan is neither its state nor a
        // reason to refuse its rescan — its own worker is both.
        bool scanning = _type == LibraryType.AutoSaves
            ? _autoPending
            : _library.IsScanning;
        _vm.IsScanning = scanning;

        if (_note is { } note)
        {
            _vm.Status = note;
            return;
        }

        // No counter (user: pointless beside the single action row) — the
        // caption carries only the scan state, and notes above win.
        _vm.Status = scanning ? ScanningText : string.Empty;
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
        _vm.CanSpawn = _type != LibraryType.Mcdf;

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
        // Every tab whose entries are pose files — auto-saves included, whose
        // tiles key on the .pose path exactly as the library's do. Character
        // files never travel the import pipeline at all, so the MCDF tab has
        // nothing to preview and its eye is disabled.
        _vm.PreviewAvailable = _type != LibraryType.Mcdf;
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
            ClosePreview();
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

    /// <summary>The toggle row: the active tab's set, hidden on the MCDF
    /// tab.</summary>
    private void SyncImportToggles()
    {
        // The poses tab's options live in the inspector rail now; the
        // row keeps component toggles only where they still govern (the
        // auto-save tab's restore). Favorites are the poses library's —
        // an auto-save snapshot is not a curated entry.
        _vm.ShowImportToggles = _type == LibraryType.AutoSaves;
        _vm.ShowImportMenus = false;
        _vm.CanFavorite = _type == LibraryType.Poses;
        bool auto = _type == LibraryType.AutoSaves;
        _vm.ImportPosition = auto ? _autoPosition : _posesPosition;
        _vm.ImportRotation = auto ? _autoRotation : _posesRotation;
        _vm.ImportScale = auto ? _autoScale : _posesScale;
    }

    private void SetImportPosition(bool value)
    {
        if (_type == LibraryType.AutoSaves)
            _autoPosition = value;
        else
            _posesPosition = value;
    }

    private void SetImportRotation(bool value)
    {
        if (_type == LibraryType.AutoSaves)
            _autoRotation = value;
        else
            _posesRotation = value;
    }

    private void SetImportScale(bool value)
    {
        if (_type == LibraryType.AutoSaves)
            _autoScale = value;
        else
            _posesScale = value;
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
        bool auto = _type == LibraryType.AutoSaves;
        // Poses apply with the SHARED menu options (the rail hosts them in
        // library mode) plus the library's load semantics; an auto-save
        // restore keeps its own full-fidelity toggles.
        var options = auto
            ? new PoseImportOptions
            {
                ApplyPosition = _autoPosition,
                ApplyRotation = _autoRotation,
                ApplyScale = _autoScale,
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
        // auto-save tab's row 1 is a snapshot.
        var row = _type != LibraryType.AutoSaves && _vm.Folders.Count > 1
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
        var result = _poseFacade.ImportPose(actor, path, BuildImportOptions(path));
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
        var result = _poseFacade.ImportPose(spawned, path, options);
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
