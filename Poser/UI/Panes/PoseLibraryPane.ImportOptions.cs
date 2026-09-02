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
using Poser.Game.Posing;
using Poser.Game.Preview;
using Poser.Game.Scene;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The import option toggles.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>The action row's per-tab affordances. NO component toggles
    /// live here: the inspector owns which of position/rotation/scale a pose
    /// applies, and an auto-save restore is full-fidelity by contract, so the
    /// second set this row used to carry was both redundant and the reason
    /// that one tab's footer read taller than every other (user 2026-08-14).
    /// </summary>
    private void SyncImportToggles()
    {
        // Favorites are the poses library's — an auto-save snapshot is not a
        // curated entry.
        _vm.ShowImportMenus = false;
        _vm.CanFavorite = _type == LibraryType.Poses;
        // A scene is not spawned as an actor, and saving one belongs where
        // scenes are found rather than behind a menu.
        _vm.ShowSpawn =
            _type is not LibraryType.Scenes and not LibraryType.Objects;
        // Scenes and objects both choose WHERE a load lands (ruled
        // 2026-08-31): the same four-mode dropdown, the same preference.
        // No probe here any more — nothing gates on saved anchors since
        // the centroid fallback made every mode honourable.
        if (_type is LibraryType.Objects or LibraryType.Scenes)
        {
            BuildPlacementChoices();
            _vm.PlacementOptions = _placementChoiceLabels.ToArray();
            _vm.PlacementSelected =
                _placementChoices.IndexOf(EffectiveMode());
            _vm.OnPlacement = next =>
            {
                if (next >= 0 && next < _placementChoices.Count)
                    _placement.Mode = _placementChoices[next];
            };
        }
        else
        {
            _vm.PlacementOptions = null;
        }
        _vm.ShowSaveScene = _type == LibraryType.Scenes;
        _vm.SceneBusy = _scenes.Busy;
        _vm.ShowEditMetadata = _type == LibraryType.Poses;
        _vm.CanEditMetadata = CanEditMetadata(_vm.Selected);
    }

    /// <summary>The library's own import options: full scope with the active
    /// tab's component toggles, and never a bone filter — a catalog apply is
    /// whole-file. A library or auto-save apply has LOAD semantics: clean
    /// slate in scope, then the file — prior in-scope edits (including the
    /// position residue a rotation-only import can never repair) must not
    /// survive a load. The FILES dialog keeps its own explicit "Reset first"
    /// checkbox for opt-in layering; its scope/expression drafts likewise
    /// never reach a library apply.</summary>
    private PoseImportOptions BuildImportOptions(string path)
    {
        // Brio's .cmp preset substitution (FileUIHelpers.cs:690) reaches the
        // library too — its file list carries .cmp entries, and Brio's own
        // library apply goes through the same popup dispatch. The library's
        // load semantics still ride on top.
        if (_files.CmpImportOverride(path, out _, out _) is { } cmp)
        {
            cmp.ResetBeforeImport = true;
            return cmp;
        }

        var options = BuildImportOptionsCore();
        // Smart expression routing (Brio ResolveSmartImport): a face-only
        // .pose can NEVER land through the body path — Dawntrail faces are
        // posed through bone POSITIONS the body path masks — and the library
        // has no import-type control, so such a file applies as an
        // expression; the engine then forces every component exactly as
        // Brio's ExpressionOptions does. The reset keeps expression scope:
        // face bones, never the head. Unconditional — the tile apply has no
        // type control, so this routing is structural, not the import
        // menu's Smart checkbox.
        if (path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) &&
            PoseFile.Load(path) is { } file &&
            PoseFileService.IsExpressionOnlyPose(file))
        {
            // Re-derived as the Expression type pair, never patched onto this
            // build: a rail with Body checked has the face already excluded,
            // and setting AsExpression over that applies nothing (see
            // PoseFileInspectorSection.RouteAsType).
            options = _files.RouteAsType(
                options, body: false, expression: true);
        }
        return options;
    }

    /// <summary>The UI-derived half of <see cref="BuildImportOptions"/>: the
    /// active tab's toggles, the load semantics and the bone-filter
    /// governance. Free of file I/O by contract — the preview polls it every
    /// frame to notice an option change — and free of side effects: the files
    /// section's own build only reads its checkbox state.</summary>
    private PoseImportOptions BuildImportOptionsCore()
    {
        // Poses apply with the SHARED menu options (the rail hosts them in
        // library mode) plus the library's load semantics; an auto-save
        // restore is full-fidelity — every component, no control to say
        // otherwise, because a recovery that silently dropped a component
        // would not be a recovery.
        var options = _type == LibraryType.AutoSaves
            ? new PoseImportOptions
            {
                ApplyPosition = true,
                ApplyRotation = true,
                ApplyScale = true,
            }
            : _files.BuildImportOptions();
        options.ResetBeforeImport = true;
        // The bone filter is NOT re-applied here: the files section's own
        // build already folds it in for the one state Brio lets it govern
        // (neither type checked), and folding it in again would have shrunk
        // a Body/Expression import the same way Brio's disabled Custom Import
        // Options button forbids. An auto-save restore is full-fidelity by
        // contract and never sees the filter at all.
        return options;
    }

    // ── the grid's actions ───────────────────────────────────────────────
}
