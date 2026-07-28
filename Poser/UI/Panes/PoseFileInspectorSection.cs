using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Entities;
using Poser.Files;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.UI;

/// <summary>Selective pose import/export controls hosted by the Pose rail.</summary>
public sealed class PoseFileInspectorSection
{
    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
    private readonly ISkeletonService _skeletons;
    private string _status = string.Empty;
    private readonly Crystarium.FileDialog _importBrowser =
        new("Import Pose", new[] { ".pose", ".cmp" }, isSaveMode: false);
    private readonly Crystarium.FileDialog _exportBrowser =
        new("Export Pose", new[] { ".pose" }, isSaveMode: true);
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private int _scope;
    private bool _rotation = true, _position = true, _scale;
    private bool _descendants = true, _reset;

    public PoseFileInspectorSection(
        IPoseFileService poseFiles,
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        ISkeletonService skeletons)
    {
        _poseFiles = poseFiles;
        _poseFacade = poseFacade;
        _selection = selection;
        _skeletons = skeletons;
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    public void Draw(Crystarium.FormScope form, ISkeleton skeleton)
    {
        form.Dropdown("Scope",
            new[] { "Full", "Body", "Expression", "Selected" },
            _scope,
            next => _scope = next);

        if (_scope == 3)
            form.Checkbox(
                "Descendants",
                _descendants,
                next => _descendants = next,
                help: "Include descendants of selected bones");
        form.Checkbox(
            "Translation", _position, next => _position = next);
        form.Checkbox(
            "Rotation", _rotation, next => _rotation = next);
        form.Checkbox(
            "Scale", _scale, next => _scale = next);
        form.Checkbox(
            "Reset first",
            _reset,
            next => _reset = next,
            help: "Reset affected bones before importing");
        form.Actions("Pose file", actions =>
        {
            actions.Button("Import…", () => OpenImport(skeleton));
            actions.Button("Export…", () => OpenExport(skeleton));
        });

        if (_status.Length > 0)
            form.Status(_status);
    }

    private void OpenImport(ISkeleton skeleton)
    {
        // The actor is frozen at dialog open; the Selected-scope selection
        // freezes as complete BoneIds at dialog confirmation.
        _importBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            List<BoneId>? frozenSelection = null;
            if (_scope == 3)
                frozenSelection = _selection.Selected
                    .Where(id => id is
                        { Kind: SceneEntityKind.Bone, Bone: not null })
                    .Select(id => id.Bone!.Value)
                    .ToList();
            var imported = _poseFacade.ImportPose(
                skeleton.Actor, path, BuildOptions(), frozenSelection);
            _status = imported.Success
                ? string.Empty
                : $"Import: {imported.Detail}";
        });
    }

    private void OpenExport(ISkeleton skeleton)
    {
        _exportBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            bool exported = _poseFiles.ExportPose(
                _skeletons.GetSkeletons(skeleton.Actor), path);
            _status = exported
                ? string.Empty
                : "Export: the pose file could not be written.";
        });
    }

    private PoseImportOptions BuildOptions()
    {
        // Scopes: 0 Full (every slot), 1 Body and 2 Expression
        // (Character-only), 3 Selected (the selected bones' exact slots via
        // the slot-qualified filter).
        bool full = _scope == 0, expression = _scope == 2, selected = _scope == 3;
        var options = new PoseImportOptions
        {
            ApplyRotation = _rotation,
            ApplyPosition = _position,
            ApplyScale = _scale,
            ApplyBody = true,
            AsExpression = expression,
            ApplyFace = full || selected,
            ApplyMainHand = full || selected,
            ApplyOffHand = full || selected,
            ApplyProp = full || selected,
            ApplyOrnament = full || selected,
            ResetBeforeImport = _reset,
            FilterIncludesDescendants = _descendants,
        };
        // The Selected-scope bone filter is NOT built here: the frozen
        // BoneIds travel to the facade, which verifies actor identity and
        // exact generations before reducing them to a slot-qualified
        // filter.
        return options;
    }
}
