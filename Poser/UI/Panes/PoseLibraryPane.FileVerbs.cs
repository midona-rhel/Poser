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

/// <summary>File verbs: retry, quarantine, move, reveal, rename, favourites.</summary>
public sealed partial class PoseLibraryPane
{
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
}
