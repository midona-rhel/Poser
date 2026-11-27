using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Application.Scene;
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
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.UI.Views;
using Poser.Documents.Files;

namespace Poser.UI;

/// <summary>Activating a tile: apply, spawn, load a scene, apply a character file.</summary>
public sealed partial class PoseLibraryPane
{
    private void ActivateObject(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count || index >= _tileKinds.Count) return;
        var result = _libraryScene.SpawnEntry(_vm.Tiles[index].ThumbKey, _tileKinds[index], EffectiveMode());
        _notices.Report(result, "Spawn");
    }

    private void LoadScene(int index)
    {
        if (index < 0 || index >= _vm.Tiles.Count) return;
        var result = _libraryScene.LoadScene(_vm.Tiles[index].ThumbKey, EffectiveMode(index));
        _notices.Report(result, "Load scene");
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
            _notices.Report(Outcome.Fail("Select an actor to apply a pose to."), "Apply", refusal: true);
            return;
        }
        ApplyTo(index, actor);
    }

    /// <summary>Applies a tile to the actor chosen by the picker or supplied
    /// by the activation path.</summary>
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
            _notices.Report(Outcome.Fail("That actor has no skeleton to pose."), "Apply", refusal: true);
            return;
        }
        var path = _vm.Tiles[index].ThumbKey;
        // Expression-only CMP imports are refused before dispatch.
        _files.CmpImportOverride(path, out bool blocked, out var cmpNote);
        if (blocked)
        {
            _notices.Report(Outcome.Fail(cmpNote!), "Apply", refusal: true);
            return;
        }
        var read = PoseFile.Read(path);
        if (read.Pose is not { } pose)
        {
            _notices.Report(Outcome.Fail(read.Failure!.Detail), "Apply");
            return;
        }
        // The target's stance is about to change, so the preview's rebase
        // baseline is stale from this call on — the NEXT tile has to be shown
        // landing on this one, not on what stood before it.
        _previewBinder.InvalidateBaseline();
        var result = _imports.ImportPose(
            actor,
            pose,
            BuildImportOptions(path, pose),
            $"Import {Path.GetFileName(path)}",
            TrackImport(actor));
        if (_notices.Report(result.Outcome, "Apply") && cmpNote is { Length: > 0 })
            _notices.Report(Outcome.Fail(cmpNote), "Apply", refusal: true);
    }

    /// <summary>
    /// Applies an MCDF through the same character-file session used by the
    /// appearance pane. The session reports progress and terminal failures;
    /// the notification reports only admission failure.
    /// </summary>
    private void ApplyCharacterFile(int index, ActorId? requestedTarget = null)
    {
        if ((requestedTarget ?? TargetActor()) is not { } id)
        {
            _notices.Report(Outcome.Fail("Select an actor to apply a character file to."), "Apply", refusal: true);
            return;
        }
        string path = _vm.Tiles[index].ThumbKey;
        var begun = _characterFiles.Import(id, path);
        _notices.Report(begun.Outcome, "Apply");
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
            _notices.Report(Outcome.Fail(cmpNote!), "Spawn as new actor", refusal: true);
            return;
        }

        var read = PoseFile.Read(path);
        if (read.Pose is not { } pose)
        {
            _notices.Report(Outcome.Fail(read.Failure!.Detail), "Spawn as new actor");
            return;
        }
        var result = _libraryScene.SpawnPose(
            pose, BuildImportOptions(path, pose), $"Import {Path.GetFileName(path)}");
        if (_notices.Report(result, "Spawn as new actor") && cmpNote is { Length: > 0 })
            _notices.Report(Outcome.Fail(cmpNote), "Spawn as new actor", refusal: true);
    }
}
