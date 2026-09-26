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
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The auto-saves tab: scanning the folder, reading each file, minting its tiles.</summary>
public sealed partial class PoseLibraryPane
{
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

        _autoLibrary.RequestScan();
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
            Crystarium.FloatingMenu.Dismiss(TileMenuId);
            Crystarium.FloatingMenu.Dismiss("##library-apply-target");
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
            : primary == PoseLibraryEntryKind.Mcdf
                ? entryKind is PoseLibraryEntryKind.Mcdf or PoseLibraryEntryKind.Chara
                : entryKind == primary;

    private IEnumerable<PoseLibraryEntry> Ordered(
        IReadOnlyList<PoseLibraryEntry> entries, PoseLibraryEntryKind kind)
    {
        var matching = entries.Where(entry =>
            InTab(entry.Kind, kind) && KindAdmitted(entry, kind));
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
}
