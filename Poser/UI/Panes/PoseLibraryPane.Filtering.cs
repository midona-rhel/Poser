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

/// <summary>Kind filters, the query, ordering and the status line.</summary>
public sealed partial class PoseLibraryPane
{
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

        // No counter (user: pointless beside the single action row). Source
        // failures are presented beside their saved folders in Settings.
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
}
