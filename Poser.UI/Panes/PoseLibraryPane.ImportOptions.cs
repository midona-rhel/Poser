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
using Poser.Documents.Files;

namespace Poser.UI;

/// <summary>The footer actions each tab shows, and the import options a pose tile is applied with.</summary>
public sealed partial class PoseLibraryPane
{
    /// <summary>Updates the actions shown for the current library tab.</summary>
    private void SyncTabActions()
    {
        // Favorites are for the poses library; an autosave is not one a person picked.
        _vm.CanFavorite = _type == LibraryType.Poses;
        // A scene is not spawned as an actor, and saving one belongs where
        // scenes are found rather than behind a menu.
        _vm.ShowSpawn =
            !IsSceneTile(_vm.Selected) && _type != LibraryType.Objects;
        // Object placement is shared; scenes additionally resolve the map default
        // and retain an explicit choice for the selected file.
        if (_type == LibraryType.Objects || IsSceneTile(_vm.Selected))
        {
            BuildPlacementChoices();
            _vm.PlacementOptions = _placementChoiceLabels.ToArray();
            _vm.PlacementSelected =
                _placementChoices.IndexOf(EffectiveMode());
            _vm.OnPlacement = next =>
            {
                if (next >= 0 && next < _placementChoices.Count)
                {
                    if (IsSceneTile(_vm.Selected) && _vm.Selected >= 0)
                        _scenePreferences.SetPlacement(_vm.Tiles[_vm.Selected].Id, _placementChoices[next]);
                    _config.Config.DefaultSpawnPlacement = _placementChoices[next];
                    _config.Save();
                }
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

    /// <summary>Builds the options for importing <paramref name="pose"/>, the
    /// file at <paramref name="path"/> that the caller has already read. A .cmp
    /// file uses the CMP options when Body or Expression is ticked. A .pose
    /// file that holds only face bones is imported as an expression, because
    /// the library has no import-type choice and the body import leaves the
    /// face out. Pose tiles use the inspector's choices; autosaves restore
    /// every component.</summary>
    private PoseImportOptions BuildImportOptions(string path, PoseFile pose)
    {
        if (_files.CmpImportOverride(path, out _, out _) is { } cmp)
        {
            cmp.ResetBeforeImport = true;
            return cmp;
        }

        var options = BuildImportOptionsCore();
        if (path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) &&
            PoseFileClassification.IsExpressionOnly(pose))
        {
            // Built again as the Expression type rather than setting
            // AsExpression on these options: with Body ticked the face is
            // already left out, and an expression import of that would apply
            // nothing (see PoseFileInspectorSection.RouteAsType).
            options = _files.RouteAsType(
                options, body: false, expression: true);
        }
        return options;
    }

    /// <summary>Builds options without reading a file. Preview checks these
    /// options every frame and reads the file only when they change.</summary>
    private PoseImportOptions BuildImportOptionsCore()
    {
        // Poses use the options set with the options button beside the
        // preview, which are the Pose tab's import options. An autosave
        // puts back every component: there is no control for it, and a
        // restore that left something out would not restore the pose.
        var options = _type == LibraryType.AutoSaves
            ? new PoseImportOptions
            {
                ApplyPosition = true,
                ApplyRotation = true,
                ApplyScale = true,
            }
            : _files.BuildImportOptions();
        options.ResetBeforeImport = true;
        // The bone filter is not applied again here. BuildImportOptions in
        // the Pose tab already applies it when neither Body nor Expression is
        // ticked, the only case where Brio uses it, and applying it twice
        // would narrow a Body or Expression import. An autosave restore
        // never uses the filter.
        return options;
    }

    // ── the grid's actions ───────────────────────────────────────────────
}
