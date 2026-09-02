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

/// <summary>Grid selection, the marquee and tile enrichment.</summary>
public sealed partial class PoseLibraryPane
{
    private void Select(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        _vm.SelectedSet.Clear();
        _vm.SelectedSet.Add(index);
        _vm.Selected = index;
        EnrichTile(index);
    }

    private void ClearTileSelection()
    {
        _vm.Selected = -1;
        _vm.SelectedSet.Clear();
    }

    /// <summary>Ctrl toggles a tile in the set; Shift selects the range from
    /// the primary; a plain click selects the tile alone. The primary is
    /// the tile the rail describes.</summary>
    private void SelectWith(int index, bool ctrl, bool shift)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        if (shift && _vm.Selected >= 0 && _vm.Selected < _vm.Tiles.Count)
        {
            int from = Math.Min(_vm.Selected, index);
            int to = Math.Max(_vm.Selected, index);
            if (!ctrl)
                _vm.SelectedSet.Clear();
            for (int i = from; i <= to; i++)
                _vm.SelectedSet.Add(i);
            EnrichTile(index);
            return;
        }
        if (ctrl)
        {
            if (!_vm.SelectedSet.Remove(index))
            {
                _vm.SelectedSet.Add(index);
                _vm.Selected = index;
                EnrichTile(index);
            }
            else if (_vm.Selected == index)
                _vm.Selected = _vm.SelectedSet.Count > 0 ? _vm.SelectedSet.Max() : -1;
            return;
        }
        Select(index);
    }

    private void MarqueeSelect(IReadOnlyList<int> caught, bool additive)
    {
        if (caught.Count == 0)
            return;
        if (!additive)
            _vm.SelectedSet.Clear();
        foreach (var index in caught)
            _vm.SelectedSet.Add(index);
        _vm.Selected = caught[caught.Count - 1];
        EnrichTile(_vm.Selected);
    }

    /// <summary>The tiles a verb acts on: the whole set when the tile
    /// clicked is in it, else that tile alone.</summary>
    private List<int> VerbTargets(int index)
    {
        if (_vm.SelectedSet.Contains(index) && _vm.SelectedSet.Count > 1)
            return _vm.SelectedSet.OrderBy(i => i).ToList();
        return [index];
    }

    /// <summary>The information a selection wants — author, tags, status,
    /// what a scene holds — is read from the file when the tile is
    /// SELECTED, never at scan time. The read runs off the UI thread and
    /// its answer is applied to the tile on the next frame.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<TileFacts> _enrichments = new();

    private readonly record struct TileFacts(
        string Id, string? Author, IReadOnlyList<string> Tags, string? Sub,
        PoseLibraryMetadataStatus Status, string Detail);

    private void EnrichTile(int index)
    {
        var tile = _vm.Tiles[index];
        if (tile.Enriched)
            return;
        tile.Enriched = true;
        string path = tile.Id;
        var kind = PoseLibraryService.KindOf(path);
        _ = Task.Run(() =>
        {
            try
            {
                if (kind == PoseLibraryEntryKind.Pose)
                {
                    var metadata = AtomicPoseFileStore.Default.ReadMetadata(path);
                    var (status, detail) = PoseLibraryFileActions.Classify(metadata);
                    _enrichments.Enqueue(new TileFacts(
                        path,
                        metadata.Succeeded ? metadata.Author : null,
                        metadata.Succeeded ? metadata.Tags : [],
                        null, status, detail));
                }
                else if (kind != PoseLibraryEntryKind.Mcdf)
                {
                    var metadata = SceneFileStore.Default.ReadMetadata(path);
                    var (status, detail) = PoseLibraryFileActions.Classify(metadata);
                    _enrichments.Enqueue(new TileFacts(
                        path,
                        metadata.Succeeded ? metadata.Author : null,
                        [],
                        metadata.Succeeded && kind == PoseLibraryEntryKind.Scene
                            ? PoseLibraryService.DescribeScene(metadata)
                            : null,
                        status, detail));
                }
            }
            catch (Exception)
            {
                // A file that cannot be read shows what the listing knew.
            }
        });
    }

    private void PumpEnrichments()
    {
        while (_enrichments.TryDequeue(out var facts))
        {
            for (int i = 0; i < _vm.Tiles.Count; i++)
            {
                var tile = _vm.Tiles[i];
                if (!string.Equals(tile.Id, facts.Id, StringComparison.Ordinal))
                    continue;
                tile.Author = facts.Author;
                tile.Tags = facts.Tags;
                if (facts.Sub is { Length: > 0 } sub)
                    tile.Sub = sub;
                if (facts.Status != PoseLibraryMetadataStatus.Valid)
                {
                    tile.Flagged = true;
                    tile.StatusText = StatusText(facts.Status, facts.Detail);
                }
                if (i < _tileStatus.Count)
                    _tileStatus[i] = facts.Status;
                if (i < _tileAuthors.Count)
                    _tileAuthors[i] = facts.Author?.ToLowerInvariant() ?? string.Empty;
                if (i < _tileTags.Count)
                    _tileTags[i] = facts.Tags.Select(tag => tag.ToLowerInvariant()).ToArray();
                break;
            }
        }
    }
}
