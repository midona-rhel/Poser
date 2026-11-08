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

namespace Poser.UI;

/// <summary>The pose preview and the character-file state.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>
    /// Chooses the pose and source actor for the inspector's preview.
    /// The preview service manages the hidden actor, camera, and rendered image.
    /// </summary>
    private void SyncPreview()
    {
        // Only pose files can be applied to the preview actor. Check supported
        // tabs explicitly so a new library type does not accidentally enter this path.
        _vm.PreviewAvailable = (_type is LibraryType.Poses or LibraryType.AutoSaves) && !IsSceneTile(_vm.Selected);
        SyncCharacterFile();
        // Preview follows the selected tile automatically on supported tabs.
        bool wanted = _vm.PreviewAvailable
            && _vm.Selected >= 0
            && _vm.Selected < _vm.Tiles.Count;
        var source = wanted ? PreviewSource() : null;

        // Tell the import dialog whether the library still needs the shared
        // preview, so closing the dialog can return control to the library.
        _files.SetPreviewClaim(source is not null);
        if (_files.IsImportPreviewActive)
        {
            // Let the dialog control the shared preview without closing it.
            // Forget the library binding so it is reapplied after the dialog closes.
            // The inspector can display the dialog's preview without a selected tile.
            _previewBinder.StandDown();
            _files.SetPreviewVisible(_vm.PreviewAvailable);
            return;
        }

        if (source is null)
        {
            // Keep the empty preview area visible on supported tabs and explain
            // whether the user needs to select a pose or provide a target actor.
            _previewBinder.Close();
            _files.SetPreviewVisible(
                _vm.PreviewAvailable,
                wanted
                    ? "No actor to preview on."
                    : "Select a pose to preview.");
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
        if (source is { } actorId && _previewBinder.Begin(
                actorId, path, PosePreviewController.Trim(BuildImportOptionsCore()), ImGui.GetFrameCount()))
            _previewBinder.Pose(
                path, PosePreviewController.Trim(BuildImportOptions(path)));

        // The inspector reads the rendered image and status from the preview service.
        _files.SetPreviewVisible(true);
    }

    /// <summary>One highlighted character file's own account of itself, and
    /// the path it belongs to. Replaced WHOLE from the reading task, never
    /// field by field, so the draw thread either sees the previous highlight's
    /// finished state or this one's — the path is what it matches on.</summary>
    private sealed record CharacterFileState(
        string Path, McdfSummary? Summary, string? Status);

    private volatile CharacterFileState? _characterFile;

    /// <summary>
    /// The character-file tab's stand-in for the pose preview: an MCDF cannot
    /// be rendered on the preview body (see
    /// <see cref="PoseFileInspectorSection.SetCharacterFile"/>), so the
    /// inspector shows what the package says about ITSELF instead.
    ///
    /// <para>The read is header-only and takes no actor, no operation
    /// directory and none of the single MCDF operation slot — a highlight may
    /// never spend the machinery an import needs. It still touches the disk,
    /// so it runs off the frame and the panel says it is reading until it
    /// lands; a highlight that moves on first is simply not adopted.</para>
    /// </summary>
    private void SyncCharacterFile()
    {
        string? path = _type == LibraryType.Mcdf
            && _vm.Selected >= 0 && _vm.Selected < _vm.Tiles.Count
            ? _vm.Tiles[_vm.Selected].ThumbKey
            : null;
        if (path == null || Path.GetExtension(path).Equals(".chara", StringComparison.OrdinalIgnoreCase))
        {
            _characterFile = null;
            _files.SetCharacterFile(null, null);
            return;
        }

        var state = _characterFile;
        if (state == null || !string.Equals(state.Path, path, StringComparison.Ordinal))
        {
            state = new CharacterFileState(path, null, "Reading the character file…");
            _characterFile = state;
            string reading = path;
            Task.Run(() =>
            {
                var read = _mcdf.ReadSummary(reading);
                var landed = new CharacterFileState(
                    reading,
                    read.Success ? read.Value : null,
                    read.Success ? null : read.Detail);
                // Only the highlight that asked for this read may adopt it.
                if (_characterFile is { } current
                    && string.Equals(current.Path, reading, StringComparison.Ordinal))
                    _characterFile = landed;
            });
        }
        _files.SetCharacterFile(state.Summary, state.Status);
    }

    /// <summary>The actor the preview borrows an appearance from: the
    /// same resolved target used by Apply and the import options.</summary>
    private ActorId? PreviewSource() =>
        CurrentApplyTarget() is { } actor && _imports.HasPosableSkeleton(actor)
            ? actor
            : null;

    /// <summary>Closes the library preview and hides its inspector section.
    /// Safe to call again when the preview is already closed.</summary>
    private void ClosePreview()
    {
        _previewBinder.Close();
        _files.SetPreviewVisible(false);
    }

    // ── the import components ────────────────────────────────────────────
}
