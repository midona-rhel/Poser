using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
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

    /// <summary>Consecutive frames the size must MOVE before stepping engages;
    /// the frame after it stops, the exact size is adopted.</summary>
    private const int DragStreakFrames = 3;

    private const string AllKey = "##pose-library-all";
    private const string FavoritesKey = "##pose-library-favorites";
    private const string AllLabel = "All poses";
    private const string AllFilesLabel = "All character files";
    private const string FavoritesLabel = "Favorites";

    /// <summary>The auto-save folder name's own format
    /// (<c>AutoSaveService.CreateSnapshotFolder</c>), which is UTC.</summary>
    private const string SnapshotFolderFormat = "yyyy-MM-dd HH-mm-ss'Z'";

    /// <summary>The stamp every tile and every snapshot header shows.</summary>
    private const string StampFormat = "yyyy-MM-dd HH:mm";

    private const string PoseExtension = ".pose";

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
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly ActorIntegrationSession _integration;
    private readonly IAutoSaveService _autoSave;
    private readonly PoseLibraryViewModel _vm = new();

    /// <summary>Which library the tabs are showing. SESSION state: it is a
    /// browsing mode, not a preference, so it is never persisted and every
    /// entry starts on the poses.</summary>
    private LibraryType _type;

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

    /// <summary>Scratch for the auto-save enumeration; reused so a rebuild
    /// allocates only the rows it actually adds.</summary>
    private readonly List<(string Directory, DateTime At)> _snapshots = [];
    private readonly List<string> _snapshotFiles = [];

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

    // Resize stepping state (StepResize).
    private Vector2 _handedSize;
    private Vector2 _steppedSize;
    private Vector2 _layoutSize;
    private int _changedStreak;

    /// <summary>Whether the standing tiles were minted with extensions on
    /// their labels; a Settings flip forces a remint.</summary>
    private bool _builtExtensions;

    /// <summary>Why the last apply or spawn did nothing, or null. Cleared by
    /// the next one and by any filter change.</summary>
    private string? _note;

    private ActorId? _targetId;
    private string _targetName = string.Empty;

    private int _lastAppliedTile = -1;
    private double _lastAppliedAt;

    private bool _iconSizeDirty;

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
        PoseFileInspectorSection poseFileSection,
        ActorIntegrationSession integration,
        IAutoSaveService autoSave)
    {
        _config = config;
        _library = library;
        _thumbs = thumbs;
        _poseFacade = poseFacade;
        _spawnService = spawnService;
        _selection = selection;
        _bindings = bindings;
        _poseFileSection = poseFileSection;
        _integration = integration;
        _autoSave = autoSave;

        _vm.OnQuery = next => _vm.Query = next;
        _vm.OnSelectFolder = SelectFolder;
        _vm.OnSelectType = SelectType;
        _vm.OnToggleGroup = ToggleGroup;
        _vm.OnSelect = Select;
        _vm.OnApplyTile = Apply;
        _vm.OnSpawnTile = Spawn;
        _vm.OnToggleFavorite = ToggleFavorite;
        _vm.OnTagFilter = TagFilter;
        _vm.OnIconSize = SetIconSize;
        _vm.OnRefresh = Refresh;
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
            // The auto-save root is written by this plugin on its own schedule
            // and holds a handful of folders: it is re-enumerated on entry and
            // on an explicit rescan, never watched.
            if (_autoDirty)
                BuildAutoSaves();
        }
        else
        {
            SyncSnapshot();
        }

        SyncQuery();
        if (_refilter)
            Refilter();
        SyncTarget();
        SyncStatus();

        PoseLibraryView.Draw(_vm, origin, StepResize(size));
    }

    /// <summary>
    /// Resize stepping: a drag on the window edge reflows the pane only at
    /// <see cref="ResizeStep"/> boundaries — per-pixel reflow of the grid
    /// while dragging cost whole frames. Stepping engages only for an ACTIVE
    /// drag, a size that moves across consecutive frames: a one-off change
    /// (entering the mode, a snapped window, a released drag) adopts the
    /// exact size immediately, because stepping it drew the pane floored for
    /// a beat and read as a reflow on navigation.
    /// </summary>
    private Vector2 StepResize(Vector2 size)
    {
        bool moved = size != _handedSize;
        _handedSize = size;
        _changedStreak = moved ? _changedStreak + 1 : 0;

        if (_changedStreak < DragStreakFrames)
        {
            _layoutSize = size;
            _steppedSize = Vector2.Zero;
            return size;
        }

        float step = ResizeStep * ImGuiHelpers.GlobalScale;
        var stepped = new Vector2(
            MathF.Floor(size.X / step) * step,
            MathF.Floor(size.Y / step) * step);
        // Only cross a boundary; a sub-step wiggle keeps the standing layout
        // as long as it still fits the handed rect.
        if (stepped != _steppedSize || _layoutSize.X > size.X
            || _layoutSize.Y > size.Y)
        {
            _steppedSize = stepped;
            _layoutSize = new Vector2(
                MathF.Min(MathF.Max(stepped.X, 1f), size.X),
                MathF.Min(MathF.Max(stepped.Y, 1f), size.Y));
        }
        return _layoutSize;
    }

    /// <summary>The workspace moved on. The decoded thumbnails are a cache of
    /// what was on screen, and the icon size is persisted here rather than on
    /// every drag tick.</summary>
    public void OnHidden()
    {
        _showing = false;
        _thumbs.Clear();
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
        _vm.SelectedType = (int)LibraryType.Poses;
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

    /// <summary>A band tab. The filters are drafts of the view being left, so
    /// the new type starts on its whole library.</summary>
    private void SelectType(int index)
    {
        if (index < 0 || index > (int)LibraryType.Mcdf
            || index == (int)_type)
            return;
        _type = (LibraryType)index;
        _vm.SelectedType = index;
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
        if (_vm.SelectedFolder >= folders.Count)
            _vm.SelectedFolder = 0;
        SyncFolderRange();
        _refilter = true;
    }

    /// <summary>
    /// The auto-save tab's rows, read straight off
    /// <see cref="IAutoSaveService.RootDirectory"/>: one group per snapshot
    /// folder, its <c>.pose</c> files as tiles. Retention, naming and the write
    /// path all stay where they are — this only reads what the service left.
    /// </summary>
    private void BuildAutoSaves()
    {
        _autoDirty = false;
        _builtExtensions = _config.Config.Library.ShowFileExtensions;
        var favorites = _config.Config.Library.Favorites;
        var folders = _vm.Folders;
        var tiles = _vm.Tiles;
        folders.Clear();
        tiles.Clear();
        _tileTags.Clear();

        _snapshots.Clear();
        try
        {
            foreach (var directory in
                     Directory.EnumerateDirectories(_autoSave.RootDirectory))
                _snapshots.Add((directory, SafeFolderTime(directory)));
        }
        catch (Exception)
        {
            // A missing or unreadable root is an empty tab, not a failure.
        }

        // Newest first, ties on name descending: the order the service's own
        // retention uses, so what the browser lists last is what it prunes.
        _snapshots.Sort(static (a, b) =>
        {
            int byDate = b.At.CompareTo(a.At);
            return byDate != 0
                ? byDate
                : string.CompareOrdinal(b.Directory, a.Directory);
        });

        foreach (var (directory, _) in _snapshots)
        {
            _snapshotFiles.Clear();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (System.IO.Path.GetExtension(file).Equals(
                            PoseExtension, StringComparison.OrdinalIgnoreCase))
                        _snapshotFiles.Add(file);
            }
            catch (Exception)
            {
            }

            if (_snapshotFiles.Count == 0)
                continue;
            _snapshotFiles.Sort(StringComparer.OrdinalIgnoreCase);

            int group = folders.Count;
            folders.Add(new PoseLibraryFolderRow
            {
                Key = directory,
                Label = SnapshotLabel(directory),
                LabelLower = string.Empty,
                Depth = 0,
                Count = _snapshotFiles.Count,
                CountText = Count(_snapshotFiles.Count),
            });

            foreach (var file in _snapshotFiles)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                _tileTags.Add(Array.Empty<string>());
                tiles.Add(new PoseLibraryTileRow
                {
                    Id = file,
                    Label = _builtExtensions ? name + PoseExtension : name,
                    LabelLower = name.ToLowerInvariant(),
                    Sub = SafeFileTime(file).ToString(
                        StampFormat, CultureInfo.InvariantCulture),
                    ThumbKey = file,
                    // An auto-save is a normal export, so it carries whatever
                    // preview the exporter wrote; the cache probes once and
                    // memoizes a file without one.
                    HasThumbnail = true,
                    Favorite = favorites.Contains(file),
                    Folder = group,
                });
            }
        }

        _vm.Selected = -1;
        _vm.SelectedFolder = 0;
        _vm.ShowRail = false;
        _vm.ShowNoSources = false;
        _vm.EmptyText = "No auto-saves yet.";
        _rangeStart = -1;
        _rangeEnd = -1;
        _refilter = true;
    }

    /// <summary>The snapshot folder's own UTC stamp, shown local and in the
    /// same shape as a tile's. A folder the collision suffix renamed does not
    /// parse and keeps its raw name, which is still a timestamp.</summary>
    private static string SnapshotLabel(string directory)
    {
        var name = System.IO.Path.GetFileName(directory);
        return DateTime.TryParseExact(
            name,
            SnapshotFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime().ToString(
                StampFormat, CultureInfo.InvariantCulture)
            : name;
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
        // The auto-save tab browses no scanned source, so a running scan is
        // neither its state nor a reason to refuse its rescan.
        bool scanning = _type != LibraryType.AutoSaves && _library.IsScanning;
        _vm.IsScanning = scanning;

        if (_note is { } note)
        {
            _vm.Status = note;
            return;
        }

        // One noun everywhere: the count switching words between views read
        // as the count meaning different things.
        if (_captionCount != _vm.Visible.Count
            || _captionScanning != scanning)
        {
            _captionCount = _vm.Visible.Count;
            _captionScanning = scanning;
            _caption = scanning
                ? "Scanning…"
                : Count(_captionCount)
                    + (_captionCount == 1 ? " item" : " items");
        }
        _vm.Status = _caption;
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
            _ => (ActorId?)null,
        };
        if (actorId is not { } id)
            return null;
        var resolved = _bindings.Resolve(id);
        return resolved.Success ? resolved.Value : null;
    }

    /// <summary>The primary action's caption, minted only when the actor it
    /// names changes — the same discipline as the footer count.</summary>
    private void SyncTarget()
    {
        var actor = TargetActor();
        // A pose is applied to a skeleton; an actor without one is not a
        // target, exactly as the actor menu's own import action states. A
        // character file dresses the actor instead and needs no skeleton.
        bool can = _type == LibraryType.Mcdf
            ? actor is not null
            : actor is { HasSkeleton: true };
        _vm.CanApply = can;

        // A character file is applied to an actor that already exists; there is
        // no "spawn and dress" path in v1.
        _vm.CanSpawn = _type != LibraryType.Mcdf;

        var id = can ? _bindings.GetActorId(actor!) : null;
        string name = can ? actor!.Name : string.Empty;
        if (Nullable.Equals(id, _targetId)
            && string.Equals(name, _targetName, StringComparison.Ordinal))
            return;
        _targetId = id;
        _targetName = name;
        _vm.ApplyLabel = id is { } value
            ? "Apply to " + _config.GetDisplayName(value.LogicalId, Clean(name))
            : "Apply";
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
        var result = _poseFacade.ImportPose(
            actor,
            _vm.Tiles[index].ThumbKey,
            _poseFileSection.BuildImportOptions(),
            _poseFileSection.FreezeSelectedScope());
        _note = result.Success ? null : Failure(result);
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
        var spawned = _spawnService.SpawnNewActor(reserveCompanionSlot: false);
        if (spawned is null)
        {
            _note = "The actor could not be spawned.";
            return;
        }

        // The options are frozen HERE, at the click, so a scope change made
        // while the scene binds the new actor cannot retarget the import. The
        // Selected-scope bone freeze is deliberately NOT taken: those BoneIds
        // belong to the source actor and the facade would reject them on the
        // spawn, so a spawn always applies the file without a bone filter.
        _pendingActor = spawned;
        _pendingPath = _vm.Tiles[index].ThumbKey;
        _pendingOptions = _poseFileSection.BuildImportOptions();
        _pendingFrames = 0;
        _note = null;
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
