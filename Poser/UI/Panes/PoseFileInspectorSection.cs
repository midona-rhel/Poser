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

/// <summary>
/// Selective pose import/export controls hosted by the Pose rail. The section
/// DECLARES its rows into the rail's tree rather than drawing them; the two
/// file dialogs stay imperative and are pumped as a named legacy boundary from
/// the pane's content column.
/// </summary>
public sealed class PoseFileInspectorSection
{
    private static readonly string[] ScopeOptions =
        ["Full", "Body", "Expression", "Selected"];

    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
    private readonly ISkeletonService _skeletons;
    private string _status = string.Empty;
    private readonly LegacyCrystarium.FileDialog _importBrowser =
        new("Import Pose", new[] { ".pose", ".cmp" }, isSaveMode: false);
    private readonly LegacyCrystarium.FileDialog _exportBrowser =
        new("Export Pose", new[] { ".pose" }, isSaveMode: true);
    private string _lastPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private int _scope;
    private bool _rotation = true, _position = true, _scale;
    private bool _descendants = true, _reset;

    /// <summary>The skeleton the build wrote, read by the two dialog
    /// openers.</summary>
    private ISkeleton? _skeleton;

    // ── hoisted handlers ─────────────────────────────────────────────────
    // A build path may allocate no delegate, so every callback the rows name
    // is a field.
    private readonly Action<int> _setScope;
    private readonly Action<bool> _setDescendants;
    private readonly Action<bool> _setPosition;
    private readonly Action<bool> _setRotation;
    private readonly Action<bool> _setScale;
    private readonly Action<bool> _setResetFirst;
    private readonly Action _openImport;
    private readonly Action _openExport;

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
        _setScope = next => _scope = next;
        _setDescendants = next => _descendants = next;
        _setPosition = next => _position = next;
        _setRotation = next => _rotation = next;
        _setScale = next => _scale = next;
        _setResetFirst = next => _reset = next;
        _openImport = () =>
        {
            if (_skeleton is { } skeleton)
                OpenImport(skeleton);
        };
        _openExport = () =>
        {
            if (_skeleton is { } skeleton)
                OpenExport(skeleton);
        };
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    public UiChildren Rows(ISkeleton skeleton)
    {
        _skeleton = skeleton;
        return
        [
            Crystarium.FormDropdown("Scope", ScopeOptions, _scope, _setScope),
            _scope == 3
                ? Crystarium.FormCheckbox(
                    "Descendants",
                    _descendants,
                    _setDescendants,
                    help: "Include descendants of selected bones")
                : UiNode.None,
            Crystarium.FormCheckbox("Translation", _position, _setPosition),
            Crystarium.FormCheckbox("Rotation", _rotation, _setRotation),
            Crystarium.FormCheckbox("Scale", _scale, _setScale),
            Crystarium.FormCheckbox(
                "Reset first",
                _reset,
                _setResetFirst,
                help: "Reset affected bones before importing"),
            Crystarium.FormActions(
                "Pose file",
                [
                    new Button
                    {
                        Label = "Import…",
                        Dense = true,
                        OnClick = _openImport,
                    },
                    new Button
                    {
                        Label = "Export…",
                        Dense = true,
                        OnClick = _openExport,
                    },
                ]),
            Crystarium.FormStatus(_status),
        ];
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
