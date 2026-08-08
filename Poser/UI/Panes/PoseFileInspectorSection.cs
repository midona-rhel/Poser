using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    // 0 Full (Body+Expression), 1 Body, 2 Expression — the import menu's
    // type pair; Selected-bones is the separate _selectedOnly switch.

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
    private bool _selectedOnly;

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
        _menuAnchor = ImGui.GetMousePos();
        _importMenuRequested = true;
    }

    /// <summary>Opens the bone-filter menu on the next pump.</summary>
    public void RequestBoneFilterMenu()
    {
        _menuAnchor = ImGui.GetMousePos();
        _boneFilterRequested = true;
    }

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

    private const string ImportMenuId = "##pose-import-menu";
    private const string ExportMenuId = "##pose-export-menu";
    private const string BoneFilterMenuId = "##pose-bone-filter-menu";
    private Vector2 _menuAnchor;
    private bool _exportMenuRequested;

    /// <summary>Opens the export menu (Brio's DrawExportPoseMenuPopup) on
    /// the next pump.</summary>
    public void RequestExportMenu()
    {
        _menuAnchor = ImGui.GetMousePos();
        _exportMenuRequested = true;
    }

    private void DrawMenus()
    {
        if (_importMenuRequested)
        {
            _importMenuRequested = false;
            Crystarium.OpenPopover(ImportMenuId);
        }
        if (_exportMenuRequested)
        {
            _exportMenuRequested = false;
            Crystarium.OpenPopover(ExportMenuId);
        }
        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            Crystarium.OpenPopover(BoneFilterMenuId);
        }

        Crystarium.FloatingSurface.Popup(
            ImportMenuId,
            new FloatingSurfaceProps
            {
                Width = 300,
                Height = _importMenuWithPresets ? 448f : 392f,
                Padding = 12,
                AnchorMin = _menuAnchor,
                AnchorMax = _menuAnchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawImportMenuBody);

        Crystarium.FloatingSurface.Popup(
            ExportMenuId,
            new FloatingSurfaceProps
            {
                Width = 300,
                Height = 196,
                Padding = 12,
                AnchorMin = _menuAnchor,
                AnchorMax = _menuAnchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawExportMenuBody);

        Crystarium.FloatingSurface.Popup(
            BoneFilterMenuId,
            new FloatingSurfaceProps
            {
                Width = 300,
                Height = 520,
                Padding = 12,
                AnchorMin = _menuAnchor,
                AnchorMax = _menuAnchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawBoneFilterBody);
    }

    private static void MenuHeader(string text)
    {
        ImGui.Spacing();
        // Picto section-caption look: caption size, the form-label color.
        Crystarium.Text(text, new TextStyle
        {
            Size = 11f,
            Color = Crystarium.ActiveTheme.FormLabel,
        });
        ImGui.Spacing();
    }

    private void MenuCheckbox(
        string id, string label, bool value, Action<bool> onChange,
        bool disabled = false, string? help = null)
    {
        Crystarium.Checkbox(id, value, onChange, default, disabled, help);
        ImGui.SameLine();
        Crystarium.Text(label, new TextStyle { Disabled = disabled });
        ImGui.Spacing();
    }

    /// <summary>Two checkboxes on one row — the menus trade a little width
    /// for half the rows (user direction). A null right cell leaves the
    /// half empty.</summary>
    private void MenuCheckboxPair(
        (string Id, string Label, bool Value, Action<bool> OnChange,
            bool Disabled, string? Help) left,
        (string Id, string Label, bool Value, Action<bool> OnChange,
            bool Disabled, string? Help)? right)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float half = ImGui.GetContentRegionAvail().X * 0.5f;

        Crystarium.Checkbox(
            left.Id, left.Value, left.OnChange, default, left.Disabled, left.Help);
        ImGui.SameLine();
        Crystarium.Text(left.Label, new TextStyle { Disabled = left.Disabled });

        if (right is { } cell)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X + half, origin.Y));
            Crystarium.Checkbox(
                cell.Id, cell.Value, cell.OnChange, default, cell.Disabled, cell.Help);
            ImGui.SameLine();
            Crystarium.Text(cell.Label, new TextStyle { Disabled = cell.Disabled });
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + 26f * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>Brio's import popup on the glass surface, two options per
    /// row, mapped onto the FILES scope model: the Body/Expression pair IS
    /// the scope (both = Full). The bone filter opens from its own button
    /// here, Brio's Custom Import Options.</summary>
    private void DrawImportMenuBody()
    {
        Crystarium.Text("Import pose");
        ImGui.Spacing();

        MenuCheckboxPair(
            ("##menu-freeze", "Freeze actor", _freeze, next =>
            {
                _freeze = next;
                _config.Config.FreezeActorOnPoseImport = next;
                _config.Save();
            }, false, "Keep the actor paused after the import"),
            ("##menu-smart", "Smart import", _smartImport,
                next => _smartImport = next, false,
                "Route face-only files as expression imports automatically"));

        MenuHeader("Import type");
        bool body = _scope is 0 or 1;
        bool expression = _scope is 0 or 2;
        MenuCheckboxPair(
            ("##menu-type-body", "Body", body, next =>
                _scope = next && expression ? 0 : next ? 1 : expression ? 2 : 0,
                false, null),
            ("##menu-type-expression", "Expression", expression, next =>
                _scope = body && next ? 0 : body ? 1 : next ? 2 : 0,
                false, null));

        MenuHeader("Transform options");
        bool componentsLocked = _smartImport || (!_selectedOnly && _scope == 2);
        MenuCheckboxPair(
            ("##menu-t", "Position", _position, next => _position = next,
                componentsLocked,
                componentsLocked
                    ? "Expression imports always apply every component"
                    : null),
            ("##menu-r", "Rotation", _rotation, next => _rotation = next,
                componentsLocked, null));
        MenuCheckboxPair(
            ("##menu-s", "Scale", _scale, next => _scale = next,
                componentsLocked, null),
            ("##menu-m", "Model transform", _modelTransform,
                next => _modelTransform = next, false,
                "Also move the actor to the file's placement"));

        MenuHeader("Scope");
        MenuCheckboxPair(
            ("##menu-selected", "Selected bones", _selectedOnly,
                next => _selectedOnly = next, false,
                "Import only the bones selected in the sidebar"),
            ("##menu-descendants", "Descendants", _descendants,
                next => _descendants = next, !_selectedOnly,
                "Include descendants of the selected bones"));
        MenuCheckboxPair(
            ("##menu-reset", "Reset first", _reset, next => _reset = next,
                false,
                "Clear every bone in scope before importing, including ones "
                + "the file does not contain"),
            null);

        Crystarium.Button("Bone filter", () => RequestBoneFilterMenu(),
            id: "##menu-bone-filter",
            help: "Choose which bone categories imports may touch");

        MenuHeader("Import");
        Crystarium.Button("From file", () =>
        {
            if (SelectedSkeleton() is { } skeleton)
                OpenImport(skeleton);
        }, id: "##menu-from-file");

        if (_importMenuWithPresets)
        {
            MenuHeader("Presets");
            var origin = ImGui.GetCursorScreenPos();
            float half = ImGui.GetContentRegionAvail().X * 0.5f;
            Crystarium.Button("A-pose",
                () => ApplyRestPreset(RestPose.APose), id: "##menu-apose");
            ImGui.SetCursorScreenPos(new Vector2(origin.X + half, origin.Y));
            Crystarium.Button("T-pose",
                () => ApplyRestPreset(RestPose.TPose), id: "##menu-tpose");
        }
    }

    /// <summary>Brio's export popup (DrawExportPoseMenuPopup): export to a
    /// file, and the stash copy. Clipboard export is a pending flow of its
    /// own.</summary>
    private void DrawExportMenuBody()
    {
        Crystarium.Text("Export pose");
        ImGui.Spacing();

        Crystarium.Button("To file", () =>
        {
            if (SelectedSkeleton() is { } skeleton)
                OpenExport(skeleton);
        }, id: "##menu-export-file");

        MenuHeader("Copy");
        Crystarium.Button("To stash", () =>
        {
            if (SelectedSkeleton() is { } skeleton)
                _status = _poseFacade.Stash(skeleton.Actor) is
                    { Success: false } failed
                    ? $"Stash: {failed.Detail}"
                    : string.Empty;
        }, id: "##menu-export-stash",
            help: "Hold this pose so it can be applied to another actor "
                + "from the inspector's Transfer group");
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
    /// DrawBoneFilterEditor) on the glass surface: Select all / none, a
    /// toggle-all header per group, a checkbox per category, the whole list
    /// scrolling inside the popover. Checked = the category applies.</summary>
    private void DrawBoneFilterBody()
    {
        Crystarium.Button("Select all", () => _disabledCategories.Clear(),
            id: "##filter-all");
        ImGui.SameLine();
        Crystarium.Button("Select none", () =>
        {
            foreach (var group in Files.ImportBoneCategories.Groups)
                foreach (var category in group.Categories)
                    _disabledCategories.Add(category.Id);
        }, id: "##filter-none");
        ImGui.Spacing();

        Crystarium.ScrollRegion("##filter-scroll", 212f, 452f, _ =>
        {
            foreach (var group in Files.ImportBoneCategories.Groups)
            {
                int enabled = 0;
                foreach (var category in group.Categories)
                {
                    if (!_disabledCategories.Contains(category.Id))
                        enabled++;
                }
                bool all = enabled == group.Categories.Length;
                ImGui.Spacing();
                MenuCheckbox($"##filter-group-{group.Name}", group.Name, all,
                    next =>
                    {
                        foreach (var category in group.Categories)
                        {
                            if (next)
                                _disabledCategories.Remove(category.Id);
                            else
                                _disabledCategories.Add(category.Id);
                        }
                    });
                ImGui.Indent();
                foreach (var category in group.Categories)
                {
                    var id = category.Id;
                    MenuCheckbox($"##filter-{id}", category.Name,
                        !_disabledCategories.Contains(id),
                        next =>
                        {
                            if (next)
                                _disabledCategories.Remove(id);
                            else
                                _disabledCategories.Add(id);
                        });
                }
                ImGui.Unindent();
            }
        });
    }

    public void Draw(Crystarium.FormScope form, ISkeleton skeleton)
    {
        // Brio's shape: one row of three commands, everything else lives in
        // the two menus Import and Export open (the inline option pile is
        // gone — the import menu owns scope, components, freeze, and the
        // bone filter).
        form.Actions("Pose", actions =>
        {
            actions.Button("Import", () => RequestImportMenu(withPresets: true));
            actions.Button("Export", () => RequestExportMenu());
            actions.Button("Library", () => OnLibraryRequested?.Invoke());
        });

        if (_status.Length > 0)
            form.Status(_status);
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
        if (!_selectedOnly)
            return null;
        return _selection.Selected
            .Where(id => id is { Kind: SceneEntityKind.Bone, Bone: not null })
            .Select(id => id.Bone!.Value)
            .ToList();
    }

    private PoseImportOptions BuildOptions()
    {
        // Types: 0 Full (every slot), 1 Body and 2 Expression
        // (Character-only); Selected-bones rides any type and reduces to
        // the slot-qualified filter at the facade.
        bool selected = _selectedOnly;
        bool full = _scope == 0 || selected, expression = !selected && _scope == 2;
        // Brio's dispatch table (FileUIHelpers.cs:697-718): both types =
        // every component, toggles IGNORED; the toggles reach only the
        // body path. A Selected-bones import keeps the toggles — the
        // Ktisis-parity flow poses exactly what you picked, how you picked.
        bool allComponents = _scope == 0 && !selected;
        var options = new PoseImportOptions
        {
            ApplyRotation = allComponents || _rotation,
            ApplyPosition = allComponents || _position,
            ApplyScale = allComponents || _scale,
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
