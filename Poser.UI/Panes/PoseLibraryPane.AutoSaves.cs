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
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.UI.Views;
using Poser.UI.Widgets;
using Poser.Domain.Library;
using Poser.Documents.Library;

namespace Poser.UI;

/// <summary>The auto-saves tab: scanning its folder and building a tile for each file.</summary>
public sealed partial class PoseLibraryPane
{
    private int _legacyAutoSavesStart = int.MaxValue;
    /// <summary>
    /// Starts a scan. On a refresh, the rows already shown stay until the new
    /// rows arrive. Opening the tab, or switching from another library tab,
    /// clears rows that do not belong here.
    /// </summary>
    private void KickAutoSaves()
    {
        _autoDirty = false;
        // Read this setting now, before the scan starts on another thread, so
        // the scan never reads interface state. Changing it later starts another scan.
        _builtExtensions = _config.Config.Library.ShowFileExtensions;

        // Auto-saves have no favourites row at the top of the folder list.
        _vm.ShowRail = true;
        _vm.RailHeads = 0;
        _vm.ShowNoSources = false;

        // Tiles already shown keep their folder row, so they do not vanish
        // while the scan runs. The previous view stays on screen until the new
        // one is ready. Only a scan still running after a short wait shows
        // Scanning, so a fast scan never flashes an empty grid.
        _autoAwaitSince = ImGui.GetTime();
        _autoAwaiting = !_autoRows;

        _autoPending = true;

        _autoLibrary.RequestScan();
    }

    /// <summary>How long a scan may run before the tab shows Scanning in place
    /// of the previous view.</summary>
    private const double PresentGraceSeconds = 0.3;

    private double _autoAwaitSince;

    private bool _autoAwaiting;

    /// <summary>The Scanning state, shown only when a scan takes longer than
    /// that short wait: the folder list and the grid are empty, with the word
    /// in the middle.</summary>
    private void ShowAutoSavesScanning()
    {
        _autoAwaiting = false;
            _rangeStart = -1;
            _rangeEnd = -1;
            _vm.SelectedFolder = 0;
            _vm.Folders.Clear();
            FloatingMenu.Dismiss(TileMenuId);
            FloatingMenu.Dismiss("##library-apply-target");
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
        if (_autoLibrary.TakeLatest() is not { } scan)
        {
            if (_autoAwaiting && ImGui.GetTime() - _autoAwaitSince >= PresentGraceSeconds)
                ShowAutoSavesScanning();
            return;
        }
        _autoAwaiting = false;
        MintAutoSaves(scan);
    }

    /// <summary>
    /// Shows the scan results in the folder list: a top row for scene
    /// autosaves and one for old pose backups, each with one row per day. The
    /// grid holds only tiles; choosing a folder row filters them.
    ///
    /// <para>Rows appear in the order they are first met, which is the scan's
    /// newest-first order, so the newest day comes first. Tiles stay in scan
    /// order, so the full view is newest first. A row's tiles need not sit
    /// together, because filtering checks each tile's row number, not its
    /// position.</para>
    ///
    /// <para>This only fills lists and looks up favourites. Every text a row
    /// shows was built by the scan.</para>
    /// </summary>
    private void MintAutoSaves(List<AutoSaveFolder> scan)
    {
        _autoPending = false;
        _autoRows = true;

        // Remember the selected folder row by its key. The rows are rebuilt
        // from scratch every time, so an index could point at a different day
        // after old auto-saves are removed.
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

        // One folder row per day, found for this pass only. This runs when the
        // tab opens and on a rescan, never every frame. The listing opens no
        // file, so the place a save was taken at shows once it is selected.
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);

        // Two top rows. The tab opens on scene auto-saves; old pose auto-saves
        // can still be reached, but do not show in the default list or its search.
        for (int section = 0; section < 2; section++)
        {
        bool legacy = section == 1;
        int root = folders.Count;
        if (legacy) _legacyAutoSavesStart = root;
        string rootKey = legacy ? "autosaves-legacy" : "autosaves-scenes";
        rows.Add(rootKey, root);
        folders.Add(new PoseLibraryFolderRow
        {
            Key = rootKey,
            Label = legacy ? "Legacy poses" : "Scene autosaves",
            LabelLower = legacy ? "legacy poses" : "scene autosaves",
            Depth = 0,
        });
        for (int s = 0; s < scan.Count; s++)
        {
            var snapshot = scan[s];
            var entries = snapshot.Entries;
            for (int e = 0; e < entries.Count; e++)
            {
                var entry = entries[e];
                bool isScene = entry.FilePath.EndsWith(Poser.Documents.Files.SceneFile.Extension, StringComparison.OrdinalIgnoreCase);
                if (isScene == legacy) continue;
                folders[root].Count++;
                string key = rootKey + KeySeparator + snapshot.Day;
                if (!rows.TryGetValue(key, out int group))
                {
                    group = folders.Count;
                    rows.Add(key, group);
                    folders.Add(new PoseLibraryFolderRow
                    {
                        Key = key,
                        Label = snapshot.Day,
                        LabelLower = string.Empty,
                        Depth = 1,
                    });
                }

                folders[group].Count++;
                _tileTags.Add(Array.Empty<string>());
                _tileAuthors.Add(string.Empty);
                _tileStatus.Add(PoseLibraryMetadataStatus.Valid);
                tiles.Add(new PoseLibraryTileRow
                {
                    Id = entry.FilePath,
                    Fallback = isScene ? TablerIcon.Movie : TablerIcon.Armature,
                    Label = _builtExtensions
                        ? entry.Name + Path.GetExtension(entry.FilePath)
                        : entry.Name,
                    LabelLower = entry.NameLower,
                    Sub = entry.Stamp,
                    Modified = entry.Stamp,
                    ThumbKey = entry.FilePath,
                    // Autosaves are written without a picture, and asking the
                    // thumbnail cache would open every file on screen.
                    HasThumbnail = false,
                    Favorite = favorites.Contains(entry.FilePath),
                    Folder = group,
                });
            }
        }
        }

        // A row's total is only known once every file is in it, so the counts
        // are written here.
        for (int i = 0; i < folders.Count; i++)
            folders[i].CountText = Count(folders[i].Count);

        ClearTileSelection();
        _vm.SelectedFolder =
            held is not null && rows.TryGetValue(held, out int standing)
                ? standing
                : 0;
        _vm.ShowRail = true;
        _vm.RailHeads = 0;
        _vm.ShowNoSources = false;
        _vm.EmptyText = NoAutoSavesText;
        SyncFolderRange();
        _refilter = true;
    }

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether an entry kind belongs to the current tab. The
    /// Objects tab shows many kinds, and MCDFs share a tab with Chara files;
    /// every other tab shows one kind.
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
            : primary == PoseLibraryEntryKind.Mcdf
                ? entryKind is PoseLibraryEntryKind.Mcdf or PoseLibraryEntryKind.Chara
                : entryKind == primary;

    /// <summary>
    /// The tab's entries in the order the grid shows them. Scenes are grouped by
    /// day and place, which no folder records, so they are sorted here: newest
    /// day first, then by place, then by the chosen sort. Other kinds stay
    /// grouped by folder. Sorting here, rather than in the grid, lets the grid
    /// start a new section by comparing a single key.
    /// </summary>
    private IEnumerable<PoseLibraryEntry> Ordered(
        IReadOnlyList<PoseLibraryEntry> entries, PoseLibraryEntryKind kind)
    {
        var matching = entries.Where(entry =>
            InTab(entry.Kind, kind) && KindAdmitted(entry, kind));
        if (_sort != LibraryBrowseSort.Name)
        {
            var modified = Comparer<DateTime>.Create((left, right) =>
                LibraryBrowseOrdering.CompareModified(left, right, _sort));
            // Keep each folder or scene section together. The date sort only
            // orders files within a section.
            return kind == PoseLibraryEntryKind.Scene
                ? matching
                    .OrderByDescending(entry => SceneDay(entry).Date)
                    .ThenBy(entry => entry.ScenePlace, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Modified, modified)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                : matching
                    .OrderBy(entry => entry.Folder)
                    .ThenBy(entry => entry.Modified, modified)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        }
        return kind == PoseLibraryEntryKind.Scene
            ? matching
                .OrderByDescending(entry => SceneDay(entry).Date)
                .ThenBy(entry => entry.ScenePlace, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(entry => SceneDay(entry))
            : matching;
    }

    /// <summary>A scene section's heading: where the scene was taken and on
    /// which day. A scene that records no place gets the day alone. The place is
    /// not guessed, so such files gather under a heading that says nothing about
    /// where they were taken.</summary>
    private static string SceneSectionLabel(PoseLibraryEntry entry)
    {
        string day = SceneDay(entry).ToString(DayFormat, CultureInfo.InvariantCulture);
        return entry.ScenePlace.Length > 0
            ? entry.ScenePlace + PlaceSeparator + day
            : day;
    }

    /// <summary>The day the scene says it was taken. The heading pairs the place
    /// recorded in the scene file with a day, so the day comes from the file too
    /// when it has one. A file's modified time is when the file last changed,
    /// which copying or syncing moves, so it is only used when the scene records
    /// no time. The grid's sorting reads this same day, so the heading and the
    /// order always agree.</summary>
    private static DateTime SceneDay(PoseLibraryEntry entry) =>
        entry.SceneCapturedAt?.ToLocalTime().DateTime ?? entry.Modified;
}
