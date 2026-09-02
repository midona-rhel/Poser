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
}
