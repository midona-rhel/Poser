using Poser.Domain.Scene;
using Poser.Application.Scene;
using Poser.Scene;
using Poser.Domain.Posing;
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

/// <summary>Activating a tile: apply, spawn, load a scene, apply a character file.</summary>
public sealed partial class PoseLibraryPane
{
    private void ActivateObject(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count || index >= _tileKinds.Count) return;
        var result = _libraryScene.SpawnEntry(_vm.Tiles[index].ThumbKey, _tileKinds[index], EffectiveMode());
        if (!result.Success) _notices.Failed(result.Detail ?? "The entry could not be loaded.");
    }

    private void LoadScene(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count) return;
        var result = _libraryScene.LoadScene(_vm.Tiles[index].ThumbKey, EffectiveMode());
        if (!result.Success) _notices.Failed(result.Detail ?? "The scene could not be loaded.");
    }

    private void Apply(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        double now = ImGui.GetTime();
        if (index == _lastAppliedTile && now - _lastAppliedAt < ReactivationSwallow)
            return;
        _lastAppliedTile = index;
        _lastAppliedAt = now;

        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index);
            return;
        }

        if (TargetActor() is not { } actor || !_imports.HasPosableSkeleton(actor))
        {
            _notices.Refused("Select an actor to apply a pose to.");
            return;
        }
        ApplyTo(index, actor);
    }

    /// <summary>The one apply: a tile onto an EXPLICIT actor — the picker's
    /// choice or the double-click path's selection target.</summary>
    private void ApplyTo(int index, ActorId actor)
    {
        if (index < 0 || index >= _vm.Tiles.Count)
            return;
        if (_type == LibraryType.Mcdf)
        {
            ApplyCharacterFile(index, actor);
            return;
        }
        if (!_imports.HasPosableSkeleton(actor))
        {
            _notices.Refused("That actor has no skeleton to pose.");
            return;
        }
        var path = _vm.Tiles[index].ThumbKey;
        // Brio's expression-only .cmp gate: reported, and NOT imported.
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }
        // The target's stance is about to change, so the preview's rebase
        // baseline is stale from this call on — the NEXT tile has to be shown
        // landing on this one, not on what stood before it.
        _previewBinder.InvalidateBaseline();
        var expectedActor = actor;
        var result = _imports.ImportPose(
            expectedActor,
            path,
            BuildImportOptions(path),
            onReceipt: TrackImport(expectedActor));
        if (!result.Success)
            _notices.Failed(Failure(result));
        else if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    /// <summary>
    /// The MCDF apply: the SAME call the appearance pane's Import… dialog
    /// makes (<c>AppearancePane.OpenMcdfImport</c>), so a character file picked
    /// here travels the identical mods/appearance/body-scale pipeline. The
    /// session reports progress and every failure on its own surface; the
    /// notification only carries a refusal to start.
    /// </summary>
    private void ApplyCharacterFile(int index, ActorId? requestedTarget = null)
    {
        if ((requestedTarget ?? TargetActor()) is not { } id)
        {
            _notices.Refused("Select an actor to apply a character file to.");
            return;
        }
        string path = _vm.Tiles[index].ThumbKey;
        var begun = _characterFiles.Import(id, path);
        if (!begun.Success)
            _notices.Failed("Import", begun.Detail ?? "The character file could not be applied.");
    }

    private void Spawn(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count || _type == LibraryType.Mcdf)
            return;

        // The .cmp verdict is taken BEFORE the spawn: an import Brio would
        // refuse must not leave a spare actor standing in the scene.
        var path = _vm.Tiles[index].ThumbKey;
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Refused(cmpNote!);
            return;
        }

        var result = _libraryScene.SpawnPose(path, BuildImportOptions(path));
        if (!result.Success) { _notices.Failed(result.Detail!); return; }
        if (cmpNote is { Length: > 0 })
            _notices.Refused(cmpNote);
    }

    private static string Failure(PoseEditResult result) =>
        "Apply: " + (result.Detail ?? "the pose could not be applied.");
}
