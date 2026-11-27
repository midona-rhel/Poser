using Poser.Application.World;
using Poser.Domain.Scene;
using Poser.Application.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Poser.Application.Integration;
using Poser.Application.Transforms;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.UI.Views;
using Poser.UI.Widgets;
using Poser.Domain.Library;
using Poser.Documents.Files;
using Poser.Application.AutoSave;
using Poser.Application.Library;
using Poser.Application.Settings;
using static Poser.UI.Widgets.ActionBarWidgets;
using static Poser.UI.Widgets.ButtonWidgets;

namespace Poser.UI;

/// <summary>
/// The Library: its tabs, folder list and grid of files. Editing and showing a
/// file's details are done by their own classes. What happens to the files
/// themselves is decided outside the interface.
/// </summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>A double-click and the Apply button both apply the selected
    /// tile. A second apply of the same tile within this many seconds is
    /// ignored, so the file is not applied twice.</summary>
    private const double ReactivationSwallow = 0.35;

    private const string AllKey = "##pose-library-all";

    private const string FavoritesKey = "##pose-library-favorites";

    private const string AllLabel = "All poses";

    private const string AllFilesLabel = "All character files";

    private const string AllScenesLabel = "All scenes";

    private const string FavoritesLabel = "Favorites";

    /// <summary>How the date is shown in an auto-save row of the folder list
    /// and in a scene group heading. Only for display; file names on disk are
    /// never read with it.</summary>
    private const string DayFormat = LibraryStamp.DateFormat;

    /// <summary>Joins a date to the place a file was saved in. A scene group
    /// heading reads "place – date"; a selected pose that records a place
    /// reads "date – place" under its name.</summary>
    private const string PlaceSeparator = " – ";

    /// <summary>Separates the parts of the hidden key of an Auto-saves folder
    /// row: the section and the day. It never appears in either.</summary>
    private const char KeySeparator = '\u001f';

    private const string PoseExtension = ".pose";

    /// <summary>Shown while folders are still being read: in the footer on
    /// any tab, and in the empty Auto-saves grid the first time it opens.</summary>
    private const string ScanningText = "Scanning";

    /// <summary>Shown in the Auto-saves grid once reading the folder has
    /// finished and found nothing.</summary>
    private const string NoAutoSavesText = "No auto-saves yet.";

    /// <summary>The Library's tabs. The Library window decides the order
    /// they are shown in.</summary>
    internal enum LibraryType
    {
        Poses,
        AutoSaves,
        Mcdf,
        Scenes,

        /// <summary>Everything that is not a pose, a scene or a character
        /// file, such as actors, lights and cameras. They share one tab
        /// because they are saved in the same Objects folder.</summary>
        Objects,
    }

    private readonly ConfigurationService _config;

    private readonly IPoseLibraryService _library;

    private readonly PoseThumbnailCache _thumbs;

    private readonly IPoseImportCommands _imports;

    private readonly Application.Library.ILibrarySceneActions _libraryScene;

    private readonly ISceneWorkflow _scenes;



    private readonly LibraryDetailsPanel _details;




    private readonly SelectionSession _selection;
    private readonly ICharacterFiles _characterFiles;

    private readonly SceneSession _scene;

    private readonly McdfTransaction _mcdf;


    private readonly PoseFileInspectorSection _files;


    /// <summary>Where the result of every action is reported. A file action,
    /// an apply or a spawn finishes after the click that started it, so its
    /// result is a notification, not a line in the grid.</summary>
    private readonly UserNotices _notices;

    private readonly PoseLibraryViewModel _vm = new();

    private readonly List<ActorId> _applyTargets = new();

    /// <summary>Which tab is open. It is not saved: each time the Library
    /// opens, it starts on Poses.</summary>
    private LibraryType _type;

    /// <summary>The kinds of object the Objects tab shows. It starts with
    /// every kind, and each kind toggle removes or adds its kind.</summary>
    private readonly HashSet<PoseLibraryEntryKind> _kindFilter =
        new(Enum.GetValues<PoseLibraryEntryKind>());

    /// <summary>Which groups are collapsed. Cleared each time the Library
    /// opens. The Poses and MCDF tabs use the same key for a folder on
    /// purpose, because it is the same folder.</summary>
    private readonly Dictionary<string, bool> _collapsed =
        new(StringComparer.Ordinal);

    /// <summary>For each scanned folder, its row in the folder list
    /// (<see cref="PoseLibraryViewModel.Folders"/>), or -1 when the open tab
    /// hides it. Each tab builds its own folder list, so a file's folder
    /// number is not its row number.</summary>
    private int[] _folderRows = [];

    /// <summary>The Auto-saves tab reads its folder itself. Setting this makes
    /// it read the folder again.</summary>
    private bool _autoDirty = true;

    /// <summary>Whether the rows on screen came from the Auto-saves tab. When
    /// that tab starts reading its folder and finds another tab's rows still
    /// on screen, it clears them. Its own rows take a frame or more to arrive,
    /// and until then it must not show another tab's files.
    /// </summary>
    private bool _autoRows;

    /// <summary>Whether the Auto-saves tab is still reading its folder. Set
    /// when a read starts and cleared when its rows are built. The rescan
    /// button and the footer text use this, not the background reader's own
    /// state.</summary>
    private bool _autoPending;

    private readonly Application.Library.AutoSaveLibrary _autoLibrary;

    /// <summary>Each tile's tags in lower case, in tile order. The tiles are
    /// only some of the scanned files, so a tile's position cannot be used to
    /// find its file; search reads this list instead.</summary>
    private readonly List<IReadOnlyList<string>> _tileTags = [];

    /// <summary>Each tile's author in lower case, or empty when the file names
    /// none. Search matches it as well as the name and the tags.</summary>
    private readonly List<string> _tileAuthors = [];

    /// <summary>Whether each tile's file could be read. The right-click menu
    /// uses it to decide which repair actions, such as Retry, to offer.</summary>
    private readonly List<PoseLibraryMetadataStatus> _tileStatus = [];

    /// <summary>Each tile's kind of file, in tile order. It decides what applying the tile does.</summary>
    private readonly List<PoseLibraryEntryKind> _tileKinds = [];

    /// <summary>Which scan the rows were built from. Switching tabs resets it,
    /// because each tab builds its own folder list and tiles from the same
    /// scan.</summary>
    private int _seenRevision = -1;

    private string _query = string.Empty;

    private string _queryLower = string.Empty;

    private string? _tagLower;

    private bool _refilter = true;

    /// <summary>The rows in the folder list that are inside the selected
    /// folder, or -1 when no folder is selected. Each folder is listed directly
    /// above everything inside it, so its contents are the rows up to the next
    /// row that is not indented further.</summary>
    private int _rangeStart = -1;

    private int _rangeEnd = -1;

    /// <summary>Whether the current tiles were built with extensions on
    /// their labels; changing Settings rebuilds them.</summary>
    private bool _builtExtensions;

    private int _lastAppliedTile = -1;

    private double _lastAppliedAt;

    private bool _iconSizeDirty;

    // ── the tile context menu and its file actions ───────────────────────
    // This class builds the tile menu, not the view, because its rows depend
    // on the tab, on whether the file could be read, and on which edit and
    // repair actions apply. Each file action reports its result as a
    // notification. After a change on disk the Library scans again instead of
    // editing its list.

    private const string TileMenuId = "##pose-library-tile-menu";

    /// <summary>What each right-click menu row does. Separators use
    /// <see cref="TileMenuAction.None"/>, so each row's position here matches
    /// the row number the menu reports when clicked.</summary>
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

    /// <summary>The folders in the Move to menu, one per row, and the file to
    /// move. Both are fixed when the menu opens.</summary>
    private readonly List<string> _moveDestinations = [];

    private string? _movePath;

    // The three file dialogs. Each keeps the file's path from when it opened,
    // because tile positions change when the list is filtered and paths do not.
    private readonly global::Poser.UI.Controls.EntityNameModal _renameModal = new();

    private readonly LibraryMetadataEditor _metadataEditor;
    private readonly Application.Library.ILibraryFileOperations _fileOperations;

    private bool _deleteOpen;

    private string _deletePath = string.Empty;

    private string _deleteName = string.Empty;

    /// <summary>Drives the one shared pose preview: whose appearance it uses,
    /// which file it shows and with which options. It poses the preview again
    /// whenever one of these changes.</summary>
    private readonly PosePreviewController _previewBinder;

    /// <summary>The last <see cref="PoseFileInspectorSection.TargetPoseRevision"/>
    /// this pane saw. When it changes, the preview takes the actor's current
    /// pose again as the pose it starts from.</summary>
    private int _seenPoseRevision;

    /// <summary>Whether the Library is on screen. The first draw after it
    /// opens resets the Library for the new visit.</summary>
    private bool _showing;

    /// <summary>Raised by the Add source button and by the message shown when
    /// there are no library folders. The Settings window is opened elsewhere.</summary>
    public event Action? OnSettingsRequested;

    /// <summary>Raised by the save button on the Scenes tab. Saving a scene,
    /// with its save dialog and description, is handled outside the Library,
    /// so this pane only asks for it.</summary>
    public event Action? OnSaveSceneRequested;

    public PoseLibraryPane(
        ConfigurationService config,
        IPoseLibraryService library,
        PoseThumbnailCache thumbs,
        IPoseImportCommands imports,
        IPoseFileCapture capture,
        IPosePreviewRuntime previewRuntime,
        Application.Library.ILibrarySceneActions libraryScene,
        SelectionSession selection,
        SceneSession scene,
        McdfTransaction mcdf,
        PropertiesContent properties,
        IPosePreview preview,
        ISceneWorkflow scenes,
        IEnvironmentControl environment,
        UserNotices notices,
        ICharacterFiles characterFiles,
        Application.Library.ILibraryFileOperations fileOperations,
        Application.Library.AutoSaveLibrary autoLibrary,
        SceneLoadPreferences scenePreferences,
        Application.World.IPlaceService place,
        SceneAutoSaveService sceneAutoSave)
    {
        _autoLibrary = autoLibrary;
        _scenePreferences = scenePreferences;
        _place = place;
        _sceneAutoSave = sceneAutoSave;
        _fileOperations = fileOperations;
        _metadataEditor = new(fileOperations, thumbs, notices);
        _characterFiles = characterFiles;
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _imports = imports;
        _libraryScene = libraryScene;
        _scenes = scenes;
        _details = new(environment, config);
        _selection = selection;
        _scene = scene;
        _mcdf = mcdf;
        _files = properties.PoseFiles;
        _notices = notices;
        _previewBinder = new PosePreviewController(previewRuntime, capture);

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
        // Applying a file to an actor always goes through the actor picker,
        // so the actor is always chosen. When only one actor can take it, the
        // picker is skipped. A scene has no target actor and loads at once.
        _vm.OnApplyTile = ActivateTile;
        _vm.Footer = DrawFooterLead;
        _vm.OnSpawnTile = Spawn;
        _vm.OnToggleFavorite = ToggleFavorite;
        _vm.OnTagFilter = TagFilter;
        _vm.OnIconSize = SetIconSize;
        _vm.OnRefresh = Refresh;
        _vm.OnApplyMenu = () =>
        {
            if (IsSceneTile(_vm.Selected))
            {
                LoadScene(_vm.Selected);
                return;
            }
            // An object tile spawns or applies depending on what the file is.
            // The actor picker is only for poses and MCDFs.
            if (_type == LibraryType.Objects)
            {
                ActivateObject(_vm.Selected);
                return;
            }
            ApplyToChosen(_vm.Selected);
        };
        // Applying an MCDF is the one long task this pane starts, so
        // the pane also offers a way to stop it. It is the same cancel as the
        // one on the appearance progress row.
        _vm.OnCancelImport = _mcdf.Cancel;
        _vm.OnSaveScene = () => OnSaveSceneRequested?.Invoke();
        _vm.OnEditMetadata = OpenMetadataEditor;
        _vm.OnOpenSettings = () => OnSettingsRequested?.Invoke();
        _vm.ResolveThumbnail = ResolveThumbnail;
        // Spawning needs no selection and nothing in the scene, so it is always
        // allowed. If the game cannot spawn, a message says so instead.
        _vm.CanSpawn = true;
    }

    /// <summary>Reports the import's final result when it was not applied.</summary>
    private Action<OperationReceipt> TrackImport(ActorId expectedActor) =>
        OperationReceipt.WhenFinal(expectedActor, receipt =>
        {
            if (receipt.State is not OperationReceiptState.Applied)
                _notices.Report(new Poser.Domain.Outcome(false, receipt.Detail), "Apply");
        });

    /// <summary>Lets the thumbnail cache carry on decoding pictures.</summary>
    public void Tick()
    {
        _thumbs.Tick();
    }

    /// <summary>Draws the Library into the area the window gives it.</summary>
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
            // The auto-save folder is read again when the tab opens and when
            // rescan is pressed; nothing watches it for changes. Reading it can
            // take a whole frame, so it is read in the background and each
            // frame shows whatever has arrived.
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
        SyncApplyTargets();
        SyncTarget();
        SyncTabActions();
        SyncStatus();
        SyncPreview();

        PoseLibraryView.Draw(_vm, origin, size);
        DrawTileMenu();
        _renameModal.Draw();
        _metadataEditor.Draw();
        PumpEnrichments();
        DrawDeleteModal();
    }

    // ── the footer ───────────────────────────────────────────────────────

    /// <summary>Shows Add source and scan status at the left of the footer.</summary>
    private void DrawFooterLead(ActionBarScope scope)
    {
        scope.Button("Add source", () => _vm.SettingsClick?.Invoke());
        scope.Label(_vm.Status);
    }

    // ── where spawned objects and scenes are placed ──────────────────────

    /// <summary>Placement choices in the order shown in the dropdown.</summary>
    private readonly List<ObjectPlacementMode> _placementChoices = [];

    private readonly List<string> _placementChoiceLabels = [];

    private void BuildPlacementChoices()
    {
        _placementChoices.Clear();
        _placementChoiceLabels.Clear();
        // The Library lists In front of camera first.
        foreach (var mode in new[]
                 {
                     ObjectPlacementMode.InFrontOfCamera, ObjectPlacementMode.AsSaved,
                     ObjectPlacementMode.RelativeToCamera, ObjectPlacementMode.RelativeToSelectedActor,
                 })
        {
            _placementChoices.Add(mode);
            _placementChoiceLabels.Add(PlacementLabels.For(mode));
        }
    }

    private readonly SceneLoadPreferences _scenePreferences;
    private readonly Application.World.IPlaceService _place;
    private readonly SceneAutoSaveService _sceneAutoSave;
    private readonly Dictionary<string, uint> _sceneTerritories = new(StringComparer.OrdinalIgnoreCase);

    private bool IsSceneTile(int index) => _type == LibraryType.Scenes ||
        (_type == LibraryType.AutoSaves &&
         (index >= 0 && index < _vm.Tiles.Count
            ? _vm.Tiles[index].Id.EndsWith(SceneFile.Extension, StringComparison.OrdinalIgnoreCase)
            : _vm.SelectedFolder < _legacyAutoSavesStart));

    private ObjectPlacementMode EffectiveMode(int index = -1)
    {
        if (index < 0) index = _vm.Selected;
        if (IsSceneTile(index) && index >= 0 && index < _vm.Tiles.Count)
        {
            var path = _vm.Tiles[index].Id;
            return _scenePreferences.PlacementFor(path, _sceneTerritories.GetValueOrDefault(path),
                _place.Current.TerritoryId, _config.Config.DefaultSpawnPlacement);
        }
        return _config.Config.DefaultSpawnPlacement;
    }



    /// <summary>Called when the Library closes. It drops the decoded
    /// thumbnails, which only served what was on screen, and saves the icon
    /// size once here instead of on every step of the slider drag.</summary>
    public void OnHidden()
    {
        _showing = false;
        _thumbs.Clear();
        // The pose preview uses a hidden actor and its own picture; closing
        // the Library frees both.
        ClosePreview();
        if (!_iconSizeDirty)
            return;
        // The slider changes the setting at once so the grid follows the
        // drag; it is saved to disk only when the Library closes.
        _iconSizeDirty = false;
        _config.Save();
    }

    /// <summary>Runs on the first frame after the Library opens.</summary>
    private void Enter()
    {
        // No folder is read until something needs it, so opening the Library
        // starts the scan. The auto-save folder is read here too, so its tab
        // already has a list when it is opened; opening the tab then only
        // checks those files again.
        _library.RequestScan();
        _autoLibrary.RequestScan();

        // Every visit starts on the Poses tab. Code that opens the Library to
        // import a pose also expects to land there.
        _type = LibraryType.Poses;
        _collapsed.Clear();
        _autoDirty = true;

        // The search text and the tag filter only matter while the Library is
        // open, so each visit starts with nothing filtered.
        ResetFilters();
        _lastAppliedTile = -1;
        _vm.IconSize = _config.Config.Library.IconSize;
        _iconSizeDirty = false;

        // Favorites and library folders may have changed while the Library was
        // closed, and a finished scan keeps its number, so always rebuild.
        _seenRevision = -1;
        _refilter = true;
    }

    /// <summary>Clears the search, the tag filter and the chosen folder. No
    /// tab or visit carries these over from another.</summary>
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

    /// <summary>The open tab as a number. The Library window uses it to
    /// show which tab is open.</summary>
    public int SelectedType => (int)_type;
    public bool ShowsPosePreview => (_type is LibraryType.Poses or LibraryType.AutoSaves)
        && !IsSceneTile(_vm.Selected);

    /// <summary>Switches tab. The new tab starts with nothing filtered, because
    /// the filters belonged to the tab being left.</summary>
    public void SelectType(int index)
    {
        if (index < 0 || index > (int)LibraryType.Objects
            || index == (int)_type)
            return;
        _type = (LibraryType)index;
        ResetFilters();
        _lastAppliedTile = -1;
        ClearTileSelection();
        // Each tab builds its own folder list and tiles from the same scan, so
        // both the scanned tabs and Auto-saves must rebuild, not just filter.
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

    /// <summary>Rebuilds the folder list and the tiles after a new scan. Every
    /// text the grid draws, the counts included, is built here once, so drawing
    /// does not make new text every frame.
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
                || !KindAdmitted(entries[i], kind))
                continue;
            total++;
            if (favorites.Contains(entries[i].FilePath))
                favored++;
        }

        // The folder list always starts with "All …" and then "Favorites",
        // followed by the scanned folders. Other code relies on this order.
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
            // A folder's count includes its subfolders, so a subfolder with no
            // files of this kind has none below it either. Hiding it hides
            // everything below it too, and each folder stays directly above
            // its contents. The top folder of each library source always shows.
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
        // Open menus point at tiles by position, and a rebuilt list moves them, so close the menus.
        FloatingMenu.Dismiss(TileMenuId);
        FloatingMenu.Dismiss("##library-apply-target");
        tiles.Clear();
        _tileTags.Clear();
        _tileAuthors.Clear();
        _tileStatus.Clear();
        _tileKinds.Clear();
        // Names get their file extension here or not, depending on the
        // setting. Search always matches the name without it.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;
        foreach (var entry in Ordered(entries, kind))
        {
            _tileTags.Add(entry.TagsLower);
            _tileAuthors.Add(entry.AuthorLower);
            _tileStatus.Add(entry.MetadataStatus);
            _tileKinds.Add(entry.Kind);
            bool flagged =
                entry.MetadataStatus != PoseLibraryMetadataStatus.Valid;
            // Built once and used twice: for a scene, the group key and the
            // heading are the same text. They are separate fields because for
            // folders they differ. This loop runs for every file on every
            // rebuild, so making the same text twice would be wasted work.
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
                // A scene's second line says what is in it, such as "3 actors,
                // 2 lights". That tells scenes apart better than a date.
                Modified = entry.ModifiedText,
                Sub = entry.SceneContents.Length > 0
                    ? entry.SceneContents
                    : entry.ModifiedText,
                ThumbKey = entry.FilePath,
                HasThumbnail = entry.HasThumbnail,
                Favorite = favorites.Contains(entry.FilePath),
                Fallback = entry.Kind switch
                {
                    PoseLibraryEntryKind.Mcdf or PoseLibraryEntryKind.Chara => TablerIcon.UserCircle,
                    PoseLibraryEntryKind.Scene => TablerIcon.Movie,
                    PoseLibraryEntryKind.Actor => TablerIcon.User,
                    PoseLibraryEntryKind.Light => TablerIcon.Bulb,
                    PoseLibraryEntryKind.Camera => TablerIcon.Camera,
                    PoseLibraryEntryKind.Environment => TablerIcon.Sun,
                    PoseLibraryEntryKind.Overlay => TablerIcon.Message,
                    PoseLibraryEntryKind.Group => TablerIcon.Folder,
                    PoseLibraryEntryKind.WorldObject => entry.WorldKind switch
                    {
                        WorldAssetKind.Furniture => TablerIcon.Couch,
                        WorldAssetKind.Effect => TablerIcon.Fire,
                        _ => TablerIcon.Plant,
                    },
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

        // After a rescan the old selection no longer matches the rows, so it is
        // cleared. When only the sort order changed, the same files stay selected.
        if (!RestoreSortSelection())
            ClearTileSelection();
        _vm.ShowRail = true;
        _vm.RailHeads = 2;
        var sources = snapshot.Sources;
        int enabled = 0;
        int ready = 0;
        int failures = snapshot.SkippedSourceCount;
        foreach (var source in sources)
        {
            if (!source.Enabled)
                continue;
            enabled++;
            if (source.Health == PoseLibrarySourceHealth.Ready)
            {
                ready++;
                continue;
            }
            if (source.Health != PoseLibrarySourceHealth.Unscanned)
                failures++;
        }
        _vm.ShowNoSources = enabled == 0 ||
            (ready == 0 && snapshot.TerminalResult == PoseLibraryScanResult.Failure);
        _vm.NoSourcesTitle = enabled == 0 && failures == 0
            ? "No enabled library sources."
            : _vm.ShowNoSources
                ? "Library sources unavailable."
                : string.Empty;
        _vm.NoSourcesDetail = enabled == 0 && failures == 0
            ? "Enable a source in Settings to browse the library."
            : "Open Settings > Library to review source folders.";
        var emptyKind = _type switch
        {
            LibraryType.Mcdf => "character files",
            LibraryType.Scenes => "scenes",
            LibraryType.Objects => "objects",
            _ => "poses",
        };
        _vm.EmptyText = total == 0 && _queryLower.Length == 0 && _tagLower is null
            ? $"No {emptyKind} found."
            : "No matches.";
        // The rows on screen now belong to the scanned tabs. If the Auto-saves
        // tab opens next, it must clear them instead of showing them.
        _autoRows = false;
        if (_vm.SelectedFolder >= folders.Count)
            _vm.SelectedFolder = 0;
        SyncFolderRange();
        _refilter = true;
    }

    // ── thumbnails ───────────────────────────────────────────────────────

    /// <summary>Gets a file's thumbnail from the cache. If it is not decoded
    /// yet, this starts decoding it and returns nothing for now.</summary>
    private PoseThumbnail ResolveThumbnail(string path)
    {
        var handle = _thumbs.Get(path, out var size);
        return handle == 0 ? default : new PoseThumbnail(handle, size);
    }
}
