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

/// <summary>The pose preview and the character-file state.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>
    /// The inspector rail's live preview. The service owns the hidden actor,
    /// the camera and the render, and the inspector section draws it; this only
    /// says WHEN it is wanted, WHICH pose it shows and WHOSE appearance it
    /// borrows. Every gate is here rather than in either drawing surface:
    /// neither has any idea what an MCDF entry is.
    /// </summary>
    private void SyncPreview()
    {
        // A WHITELIST, stated as one: every tab whose entries are pose files —
        // auto-saves included, whose tiles key on the .pose path exactly as
        // the library's do. Written as an exclusion this silently admitted the
        // next tab added; scenes did exactly that, feeding .xivs scene paths
        // into the pose preview binder. Character files never travel the
        // import pipeline, and a scene is not a pose file at all: it has no
        // single skeleton to stand on a preview body.
        _vm.PreviewAvailable = _type is LibraryType.Poses or LibraryType.AutoSaves;
        SyncCharacterFile();
        // No eye anymore (user 2026-08-11): the preview is always live on a
        // tab that can preview, so availability alone gates it.
        bool wanted = _vm.PreviewAvailable
            && _vm.Selected >= 0
            && _vm.Selected < _vm.Tiles.Count;
        var source = wanted ? PreviewSource() : null;

        // The claim is stated whether or not this pane gets to act on it: the
        // import dialog drives the SAME service while it is open, and what it
        // does with the seat when it closes depends on whether anyone else
        // still wants it.
        _files.SetPreviewClaim(source is not null);
        if (_files.IsImportPreviewActive)
        {
            // Stood down, NOT closed — the dialog is driving. The pose it put
            // there is not this pane's to remember, so the binder forgets it
            // and re-states the frame the seat comes back. The rail's preview
            // block STAYS UP as a read-only mirror of what the dialog shows
            // (user 2026-08-10: it vanished for the whole dialog session) —
            // same service, same texture, camera commands included — gated on
            // the user's own preview eye so a switched-off preview does not
            // reappear just because the dialog previews for itself. A tile
            // selection is deliberately NOT required: the dialog's highlight
            // is the subject, not this pane's.
            _previewBinder.StandDown();
            _files.SetPreviewVisible(_vm.PreviewAvailable);
            return;
        }

        if (source is null)
        {
            // The seat STAYS on a preview-capable tab: the empty well and its
            // reason are the affordance that a preview exists at all (user
            // 2026-08-11: nothing indicated one until a tile was clicked).
            // Only the MCDF tab, which can never preview, drops the section.
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
        if (_previewBinder.Begin(
                source, path, PosePreviewBinder.Trim(BuildImportOptionsCore())))
            _previewBinder.Pose(
                path, PosePreviewBinder.Trim(BuildImportOptions(path)));

        // The seat is the inspector rail's, so the section is told to show it;
        // the render and its status are read there, straight off the service.
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
        if (path == null)
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
                var read = _integration.ReadMcdfSummary(reading);
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
    /// selection's actor when it can be posed, else the first actor an apply
    /// would land on — the picker's own leading candidate.</summary>
    private IActor? PreviewSource() =>
        TargetActor() is { HasSkeleton: true } actor
            ? actor
            : FirstApplyTarget();

    /// <summary>Tears the preview down and takes its seat back off the
    /// inspector rail. Idempotent — the frame after a close must not close
    /// again, and the seat is withdrawn either way so the section never draws
    /// a preview this pane has stopped feeding.</summary>
    private void ClosePreview()
    {
        _previewBinder.Close();
        _files.SetPreviewVisible(false);
    }

    // ── the import components ────────────────────────────────────────────
}
