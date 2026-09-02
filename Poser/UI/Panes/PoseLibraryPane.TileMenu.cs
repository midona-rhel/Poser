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

/// <summary>The tile context menu and what each of its verbs dispatches to.</summary>
public sealed partial class PoseLibraryPane
{
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
        int moveClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick();
        if (moveClicked >= 0 && moveClicked < _moveDestinations.Count
            && _movePath is { } movePath)
        {
            MoveToFolder(movePath, _moveDestinations[moveClicked]);
            return;
        }
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
        bool objects = _type == LibraryType.Objects;
        int count = VerbTargets(index).Count;
        string many = count > 1 ? " " + count.ToString(CultureInfo.InvariantCulture) : string.Empty;
        // The verb says what the file IS: a scene loads, an object spawns,
        // a pose or a character file applies. One file at a time.
        if (count == 1)
        {
            Row(TileMenuAction.Apply, new ContextMenuItem(
                scenes ? "Load scene" : objects ? "Spawn" : "Apply",
                scenes ? TablerIcon.Movie : objects ? TablerIcon.Plus : TablerIcon.Check,
                disabled: !_vm.CanApply));
            if (!scenes && !objects)
                Row(TileMenuAction.Spawn, new ContextMenuItem(
                    "Spawn as new actor", TablerIcon.UserPlus,
                    disabled: !_vm.CanSpawn));
        }
        if (_vm.CanFavorite)
            Row(TileMenuAction.Favorite, new ContextMenuItem(
                (tile.Favorite ? "Unfavorite" : "Favorite") + many, TablerIcon.Star));

        bool poses = _type == LibraryType.Poses;
        var status = _tileStatus[index];
        if (count == 1 && poses && status != PoseLibraryMetadataStatus.Valid)
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
            if (count == 1 && CanEditMetadata(index))
                Row(TileMenuAction.EditMetadata, new ContextMenuItem(
                    "Edit metadata…", TablerIcon.FileText,
                    help: "Author and tags, written back into the file."));
            if (count == 1)
                Row(TileMenuAction.Rename, new ContextMenuItem(
                    "Rename…", TablerIcon.Edit));
            _moveMore.Clear();
            foreach (var target in VerbTargets(index))
                if (target != index)
                    _moveMore.Add(_vm.Tiles[target].ThumbKey);
            Row(TileMenuAction.MoveTo, new ContextMenuItem(
                count > 1 ? $"Move{many} to folder…" : "Move to folder…", TablerIcon.Folder,
                submenuItems: BuildMoveSubmenu(tile.ThumbKey)));
        }
        Separator();
        if (count == 1)
            Row(TileMenuAction.Reveal, new ContextMenuItem(
                "Reveal in Explorer", TablerIcon.ExternalLink));
        Row(TileMenuAction.Delete, new ContextMenuItem(
            count > 1 ? $"Delete{many} files…" : "Delete…", TablerIcon.Trash, danger: true));

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
        // Description and the preview image are not on the tile — the grid has
        // no use for either — so the document is read once, here, rather than
        // widening every tile in the library to carry them.
        _metaDescription = string.Empty;
        _metaHadImage = false;
        _metaImage = PosePreviewImageEdit.Keep;
        var read = AtomicPoseFileStore.Default.Read(_metaPath);
        if (read.Succeeded && read.Pose is { } pose)
        {
            _metaDescription = pose.Description ?? string.Empty;
            _metaHadImage = !string.IsNullOrEmpty(pose.Base64Image);
        }
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
        if (_type == LibraryType.Objects)
        {
            ActivateObject(index);
            return;
        }
        ApplyToChosen(index);
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
            {
                // The set follows the clicked tile: all on, or all off.
                bool favorite = !tile.Favorite;
                foreach (var target in VerbTargets(index))
                    SetFavorite(target, favorite);
                break;
            }
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
                OpenRename(path);
                break;
            case TileMenuAction.Reveal:
                RevealFile(path);
                break;
            case TileMenuAction.Delete:
            {
                var targets = VerbTargets(index);
                _deletePath = path;
                _deleteMore.Clear();
                foreach (var target in targets)
                    if (target != index)
                        _deleteMore.Add(_vm.Tiles[target].ThumbKey);
                _deleteName = targets.Count > 1
                    ? $"{targets.Count} files"
                    : System.IO.Path.GetFileName(path);
                _deleteOpen = true;
                break;
            }
        }
    }

    // ── the recovery and authoring verbs ─────────────────────────────────
    // Disk work happens on the click, exactly as an apply's file load does;
    // every outcome is TYPED and is announced through the notification
    // channel, and a successful mutation asks the scan for a fresh complete
    // pass rather than editing the published snapshot.
}
