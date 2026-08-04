using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Poser.Entities;
using Poser.Files;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Selective pose import/export controls hosted by the Pose workspace's Actor
/// tab; the actor right-click menu opens the same two dialogs directly. The
/// dialogs are pumped by MainWindow rather than from here, so they survive any
/// tab or rail state change.
/// </summary>
public sealed class PoseFileInspectorSection
{
    private static readonly string[] ScopeOptions =
        ["Full", "Body", "Expression", "Selected"];

    private readonly IPoseFileService _poseFiles;
    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
    private readonly ISkeletonService _skeletons;
    private readonly Config.ConfigurationService _config;
    private readonly IAutoSaveService _autoSave;
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

    /// <summary>Raised by the Library… action and by an Import… that the
    /// library setting has taken over. The UI manager owns the window.</summary>
    public event Action? OnLibraryRequested;

    public PoseFileInspectorSection(
        IPoseFileService poseFiles,
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        ISkeletonService skeletons,
        Config.ConfigurationService config,
        IAutoSaveService autoSave)
    {
        _poseFiles = poseFiles;
        _poseFacade = poseFacade;
        _selection = selection;
        _skeletons = skeletons;
        _config = config;
        _autoSave = autoSave;
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
    }

    public void Draw(Crystarium.FormScope form, ISkeleton skeleton)
    {
        // The Actor tab has row width to spare, so the options pack two to
        // a row instead of stacking six label rows. Descendants stays
        // visible-but-disabled outside the Selected scope: a checkbox that
        // appears and disappears moves every row under it.
        form.Pair(
            "Scope",
            cell =>
            {
                ImGui.SetCursorScreenPos(cell.Center(
                    Crystarium.ActiveTheme.Controls.WorkspaceHeight));
                Crystarium.Dropdown(
                    "##posefile-scope",
                    ScopeOptions,
                    _scope,
                    next => _scope = next,
                    cell.Constrain(ControlStyle.Workspace));
            },
            "Descendants",
            cell => PairCheckbox(
                cell,
                "##posefile-descendants",
                _descendants,
                next => _descendants = next,
                disabled: _scope != 3,
                help: "Include descendants of selected bones"));
        form.Checkboxes(
            "Apply",
            ("Translation", _position, next => _position = next, null),
            ("Rotation", _rotation, next => _rotation = next, null),
            ("Scale", _scale, next => _scale = next, null));
        form.Checkbox(
            "Reset first",
            _reset,
            next => _reset = next,
            help: "Clear every bone in the chosen scope before importing, "
                + "including ones the file does not contain");
        form.Actions("Pose file", actions =>
        {
            actions.Button("Import…", () => OpenImport(skeleton));
            actions.Button("Export…", () => OpenExport(skeleton));
            actions.Button("Library…", () => OnLibraryRequested?.Invoke());
        });

        if (_status.Length > 0)
            form.Status(_status);
    }

    private static void PairCheckbox(
        Crystarium.FormPairCell cell,
        string id,
        bool value,
        Action<bool> onChange,
        bool disabled = false,
        string? help = null)
    {
        ImGui.SetCursorScreenPos(cell.Center(
            Crystarium.ActiveTheme.Controls.CheckboxSize));
        Crystarium.Checkbox(id, value, onChange, default, disabled, help);
    }

    public void OpenImport(ISkeleton skeleton)
    {
        // The library is a full replacement for the file dialog when the user
        // asked for it: this is the ONE import entry point, so the actor
        // context menu is covered by the same redirect.
        if (_config.Config.Library.UseLibraryWhenImporting)
        {
            OnLibraryRequested?.Invoke();
            return;
        }

        BrowseAndImport(skeleton, _lastPath, rememberPath: true);
    }

    /// <summary>
    /// The recovery path: the same import browser rooted at the auto-save
    /// directory, so a recovered pose travels the identical import pipeline.
    /// The library redirect does NOT apply — the library scans configured
    /// source folders, not the auto-save root — and the chosen folder is not
    /// remembered, so the next Import… still opens where the user last was.
    /// </summary>
    public void OpenAutoSaves(ISkeleton skeleton)
    {
        BrowseAndImport(skeleton, _autoSave.RootDirectory, rememberPath: false);
    }

    /// <summary>The one import callback every entry point shares.</summary>
    private void BrowseAndImport(
        ISkeleton skeleton,
        string initialPath,
        bool rememberPath)
    {
        // The actor is frozen at dialog open; the Selected-scope selection
        // freezes as complete BoneIds at dialog confirmation.
        _importBrowser.Open(initialPath, path =>
        {
            if (rememberPath)
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            var imported = _poseFacade.ImportPose(
                skeleton.Actor, path, BuildOptions(), FreezeSelectedScope());
            _status = imported.Success
                ? string.Empty
                : $"Import: {imported.Detail}";
        });
    }

    public void OpenExport(ISkeleton skeleton)
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

    /// <summary>The section's current import options, for surfaces that import
    /// without opening this section's own dialog.</summary>
    public PoseImportOptions BuildImportOptions() => BuildOptions();

    /// <summary>
    /// The Selected-scope bones frozen as complete BoneIds, or null in every
    /// other scope. Taken at the moment the import is confirmed, never earlier:
    /// the facade verifies the exact actor generation these belong to.
    /// </summary>
    public List<BoneId>? FreezeSelectedScope()
    {
        if (_scope != 3)
            return null;
        return _selection.Selected
            .Where(id => id is { Kind: SceneEntityKind.Bone, Bone: not null })
            .Select(id => id.Bone!.Value)
            .ToList();
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
