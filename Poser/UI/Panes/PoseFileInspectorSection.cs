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

    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
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
    // Rotation-only by default, matching Brio's DefaultImporterOptions and
    // Ktisis's ImportPoseTransforms: Translation/Scale are opt-in because a
    // file's baked positions/scales fight IK and Customize+ scaling.
    private bool _rotation = true, _position, _scale;
    private bool _descendants = true, _reset;

    // ── the two Brio menus (one shared state for FILES and the library) ──
    // Import menu: Brio's import popup (FileUIHelpers.DrawImportPoseMenuPopup)
    // — Freeze / Smart Import / Import Type / transform toggles / presets.
    // Bone-filter menu: Brio's category filter (PosingEditorCommon.
    // DrawBoneFilterEditor) — group tristates + per-category checkboxes.
    // Default ON — deviation from Brio's unchecked default, deliberate: the
    // library's tile apply has no import-type control, so smart routing is
    // the only thing that can send a face-only file down the expression
    // path; off would resurrect the broken body-path face import.
    private bool _smartImport = true;
    private bool _modelTransform;
    private readonly HashSet<string> _disabledCategories =
        new(StringComparer.Ordinal);
    private bool _importMenuRequested;
    private bool _importMenuWithPresets;
    private bool _boneFilterRequested;

    /// <summary>Whether library applies run the face-only→expression smart
    /// routing — the import menu's Smart Import checkbox, default on.</summary>
    public bool SmartImportEnabled => _smartImport;
    // Seeded from config and written back on toggle: the checkbox IS the
    // persisted FreezeActorOnPoseImport default (Brio's popup checkbox +
    // hidden config flag as one surface).
    private bool _freeze;

    /// <summary>Raised by the Library… action and by an Import… that the
    /// library setting has taken over. The UI manager owns the window.</summary>
    public event Action? OnLibraryRequested;

    public PoseFileInspectorSection(
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        Config.ConfigurationService config,
        IAutoSaveService autoSave)
    {
        _poseFacade = poseFacade;
        _selection = selection;
        _config = config;
        _autoSave = autoSave;
        _freeze = config.Config.FreezeActorOnPoseImport;
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
        DrawMenus();
    }

    /// <summary>Opens the import-options menu on the next pump. Presets show
    /// only for the actor-side mount (the user's rule: rest poses belong to
    /// the actor part, never the library).</summary>
    public void RequestImportMenu(bool withPresets)
    {
        _importMenuWithPresets = withPresets;
        _importMenuRequested = true;
    }

    /// <summary>Opens the bone-filter menu on the next pump.</summary>
    public void RequestBoneFilterMenu() => _boneFilterRequested = true;

    /// <summary>The bone-filter menu's verdict folded into any surface's
    /// options: disabled prefix categories become exclusions, the slot rows
    /// (Weapons / Emote Props / Fashion Accessories) gate their slots, and
    /// a disabled Other row bans uncategorized bones.</summary>
    public PoseImportOptions ApplyCategoryFilter(PoseImportOptions options)
    {
        if (_disabledCategories.Count == 0)
            return options;
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Files.ImportBoneCategories.Groups)
        {
            foreach (var category in group.Categories)
            {
                if (!_disabledCategories.Contains(category.Id))
                    continue;
                foreach (var prefix in category.Prefixes)
                    prefixes.Add(prefix);
            }
        }
        if (prefixes.Count > 0)
            options.ExcludedBonePrefixes = prefixes;
        options.ExcludeUncategorizedBones = _disabledCategories.Contains("other");
        if (_disabledCategories.Contains("weapon"))
        {
            options.ApplyMainHand = false;
            options.ApplyOffHand = false;
        }
        if (_disabledCategories.Contains("prop"))
            options.ApplyProp = false;
        if (_disabledCategories.Contains("ornament"))
            options.ApplyOrnament = false;
        return options;
    }

    private ISkeleton? SelectedSkeleton()
    {
        foreach (var id in _selection.Selected)
        {
            if (id is { Kind: SceneEntityKind.Actor, Actor: { } actorId } &&
                _resolveActor?.Invoke(actorId) is { HasSkeleton: true } actor)
                return actor.Skeleton;
        }
        return null;
    }

    /// <summary>MainWindow supplies actor resolution (the section itself is
    /// binding-free); null until wired.</summary>
    public Func<Domain.Identity.ActorId, IActor?>? _resolveActor;

    private void DrawMenus()
    {
        if (_importMenuRequested)
        {
            _importMenuRequested = false;
            ImGui.OpenPopup("##pose-import-menu");
        }
        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            ImGui.OpenPopup("##pose-bone-filter-menu");
        }

        if (ImGui.BeginPopup("##pose-import-menu"))
        {
            DrawImportMenu();
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("##pose-bone-filter-menu"))
        {
            DrawBoneFilterMenu();
            ImGui.EndPopup();
        }
    }

    /// <summary>Brio's import popup, mapped onto the FILES scope model:
    /// the Body/Expression strip IS the scope (both = Full, neither = the
    /// rotation-only default over everything).</summary>
    private void DrawImportMenu()
    {
        ImGui.TextDisabled("Import Pose");
        ImGui.Separator();

        bool freeze = _freeze;
        if (ImGui.Checkbox("Freeze actor", ref freeze))
        {
            _freeze = freeze;
            _config.Config.FreezeActorOnPoseImport = freeze;
            _config.Save();
        }
        bool smart = _smartImport;
        if (ImGui.Checkbox("Smart import", ref smart))
            _smartImport = smart;

        ImGui.Separator();
        ImGui.TextDisabled("Import type");
        bool body = _scope is 0 or 1;
        bool expression = _scope is 0 or 2;
        if (ImGui.Checkbox("Body", ref body) ||
            ImGui.Checkbox("Expression", ref expression))
        {
            _scope = body && expression ? 0 : body ? 1 : expression ? 2 : 0;
            if (!body && !expression)
                _scope = 0;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Transform options");
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_smartImport || _scope == 2))
        {
            bool position = _position;
            if (ImGui.Checkbox("Position", ref position)) _position = position;
            bool rotation = _rotation;
            if (ImGui.Checkbox("Rotation", ref rotation)) _rotation = rotation;
            bool scaleOn = _scale;
            if (ImGui.Checkbox("Scale", ref scaleOn)) _scale = scaleOn;
        }
        bool model = _modelTransform;
        if (ImGui.Checkbox("Model transform", ref model)) _modelTransform = model;

        ImGui.Separator();
        if (ImGui.MenuItem("Import from file…"))
        {
            if (SelectedSkeleton() is { } skeleton)
                OpenImport(skeleton);
        }

        if (_importMenuWithPresets)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Presets");
            if (ImGui.MenuItem("Import A-pose"))
                ApplyRestPreset(RestPose.APose);
            if (ImGui.MenuItem("Import T-pose"))
                ApplyRestPreset(RestPose.TPose);
        }
    }

    private void ApplyRestPreset(RestPose pose)
    {
        if (SelectedSkeleton() is { } skeleton)
            _status = _poseFacade.ApplyRestPose(skeleton.Actor, pose) is
                { Success: false } failed
                ? $"Preset: {failed.Detail}"
                : string.Empty;
        else
            _status = "Select an actor first.";
    }

    /// <summary>Brio's bone-filter editor (PosingEditorCommon.
    /// DrawBoneFilterEditor): Select all / none, a toggle-all header per
    /// group, a checkbox per category. Checked = the category applies.</summary>
    private void DrawBoneFilterMenu()
    {
        if (ImGui.SmallButton("Select all"))
            _disabledCategories.Clear();
        ImGui.SameLine();
        if (ImGui.SmallButton("Select none"))
        {
            foreach (var group in Files.ImportBoneCategories.Groups)
                foreach (var category in group.Categories)
                    _disabledCategories.Add(category.Id);
        }

        foreach (var group in Files.ImportBoneCategories.Groups)
        {
            ImGui.Separator();
            int enabled = 0;
            foreach (var category in group.Categories)
            {
                if (!_disabledCategories.Contains(category.Id))
                    enabled++;
            }
            bool all = enabled == group.Categories.Length;
            if (ImGui.Checkbox($"{group.Name}##group", ref all))
            {
                foreach (var category in group.Categories)
                {
                    if (all)
                        _disabledCategories.Remove(category.Id);
                    else
                        _disabledCategories.Add(category.Id);
                }
            }
            ImGui.Indent();
            foreach (var category in group.Categories)
            {
                bool on = !_disabledCategories.Contains(category.Id);
                if (ImGui.Checkbox(category.Name, ref on))
                {
                    if (on)
                        _disabledCategories.Remove(category.Id);
                    else
                        _disabledCategories.Add(category.Id);
                }
            }
            ImGui.Unindent();
        }
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
        form.Checkbox(
            "Freeze actor",
            _freeze,
            next =>
            {
                _freeze = next;
                _config.Config.FreezeActorOnPoseImport = next;
                _config.Save();
            },
            help: "Keep the actor paused after the import instead of "
                + "resuming its animation; resume from the Animation tab");
        form.Actions("Pose file", actions =>
        {
            actions.Button("Import…", () => OpenImport(skeleton));
            actions.Button("Export…", () => OpenExport(skeleton));
            actions.Button("Library…", () => OnLibraryRequested?.Invoke());
        });
        // The two Brio menus, actor-side mount: presets included here and
        // ONLY here (the user's rule — rest poses belong to the actor part).
        form.Actions("Menus", actions =>
        {
            actions.Button("Options…", () => RequestImportMenu(withPresets: true));
            actions.Button("Bones…", () => RequestBoneFilterMenu());
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
            // Armed, not written: the file lands once the update-phase pass
            // has refreshed the raw transform caches it snapshots, so a
            // never-posed actor exports its current pose instead of the
            // build-time one. The status comes from the callback.
            var armed = _poseFacade.ExportPose(
                skeleton.Actor,
                path,
                exported => _status = exported
                    ? string.Empty
                    : "Export: the pose file could not be written.");
            if (!armed.Success)
                _status = $"Export: {armed.Detail}";
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
            // Brio's dispatch table (FileUIHelpers.cs:697-718): with BOTH
            // import types selected — its popup's everyday state — the
            // import runs DefaultIPCImporterOptions, TransformComponents.All
            // on every bone, transform icons IGNORED (passed null). The
            // icons only reach the body-only path. Full mirrors that
            // exactly: every component, toggles ignored; Body honors the
            // toggles; Expression forces All at the engine.
            ApplyRotation = full || _rotation,
            ApplyPosition = full || _position,
            ApplyScale = full || _scale,
            ApplyBody = true,
            AsExpression = expression,
            ApplyFace = full || selected,
            ApplyMainHand = full || selected,
            ApplyOffHand = full || selected,
            ApplyProp = full || selected,
            ApplyOrnament = full || selected,
            ResetBeforeImport = _reset,
            FilterIncludesDescendants = _descendants,
            FreezeOnImport = _freeze,
            ApplyModelTransform = _modelTransform,
        };
        // The Selected-scope bone filter is NOT built here: the frozen
        // BoneIds travel to the facade, which verifies actor identity and
        // exact generations before reducing them to a slot-qualified
        // filter.
        return ApplyCategoryFilter(options);
    }
}
