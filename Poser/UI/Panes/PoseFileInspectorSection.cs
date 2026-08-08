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
    // The import menu's type pair, Brio's exact popup state: both OFF is
    // the DEFAULT path (rotation-only toggles over everything, weapons and
    // ex excluded, the custom bone filter live); Body-only excludes the
    // face and honors the toggles; Expression runs the dance with every
    // component; both = everything, all components, toggles ignored.
    private bool _typeBody;
    private bool _typeExpression;

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
    // Brio's DefaultImporterOptions filter starts with weapon and ex
    // disabled (PosingService.cs:45-47); the menu edits from there.
    private readonly HashSet<string> _disabledCategories =
        new(StringComparer.Ordinal) { "weapon", "ex" };
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

    /// <summary>Opens the bone-filter menu on the next pump, beside the
    /// import menu it nests under.</summary>
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

    private const float MenuPadding = 8f;

    /// <summary>What the first section spends above its title text: the
    /// pre-header padding plus the header band's centering slack. Menus
    /// start their stack this far ABOVE the origin so the title sits at
    /// the window padding.</summary>
    private static float MenuTitleOffset(float scale)
    {
        // Half the first section's pre-title spend: the full compensation
        // glued the title to the window edge, none left it floating — the
        // middle reads right (user round).
        var page = Crystarium.ActiveTheme.Page;
        return (page.SectionPaddingTop
            + (page.SectionHeaderHeight
                - Crystarium.ActiveTheme.Typography.LabelSize) * 0.5f)
            * 0.5f * scale;
    }
    private const float MenuWidth = 320f;
    private const float FilterMenuWidth = 240f;
    private const float MenuLabelColumn = 96f;
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
        Crystarium.FloatingSurface.Popup(
            ImportMenuId,
            new FloatingSurfaceProps
            {
                Width = MenuWidth,
                Height = _importMenuWithPresets
                    ? _importMenuHeightPresets
                    : _importMenuHeightPlain,
                Padding = MenuPadding,
                AnchorMin = _menuAnchor,
                AnchorMax = _menuAnchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawImportMenuBody);

        Crystarium.FloatingSurface.Popup(
            ExportMenuId,
            new FloatingSurfaceProps
            {
                Width = MenuWidth,
                Height = _exportMenuHeight,
                Padding = MenuPadding,
                AnchorMin = _menuAnchor,
                AnchorMax = _menuAnchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawExportMenuBody);

    }

    /// <summary>Self-measured popup heights (unscaled): the section stack
    /// reports its real height as it draws — plus the page inset Complete()
    /// extends the cursor extent by, so the window never scrolls — and the
    /// next frame's popup fits exactly. Per variant: the presets section
    /// changes the actor-side height.</summary>
    private float _importMenuHeightPlain = 430f;
    private float _importMenuHeightPresets = 480f;
    private float _exportMenuHeight = 190f;
    private float _boneFilterHeight = 520f;

    /// <summary>
    /// Brio's import popup, composed from the SAME form idioms every pane
    /// uses (standalone Crystarium.Section + form rows — never hand-rolled
    /// columns): paired options share rows through form.Checkboxes, whose
    /// gap is the theme's, not a constant.
    /// </summary>
    private void DrawImportMenuBody()
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float y = origin.Y - MenuTitleOffset(scale);

        y += Crystarium.Section(
            "##import-menu-head", "Import pose",
            new Vector2(origin.X, y), width, true, null,
            form =>
            {
                form.Checkboxes(
                    "Options",
                    ("Freeze", _freeze, next =>
                    {
                        _freeze = next;
                        _config.Config.FreezeActorOnPoseImport = next;
                        _config.Save();
                    }, "Keep the actor paused after the import"),
                    ("Smart", _smartImport, next => _smartImport = next,
                        "Route face-only files as expression imports automatically"));
                form.Checkboxes(
                    "Type",
                    ("Body", _typeBody,
                        next => _typeBody = next,
                        "Import the body. With Expression too, everything "
                        + "imports with every component"),
                    ("Expression", _typeExpression,
                        next => _typeExpression = next,
                        "Import the face as an expression — always every "
                        + "component"));
            },
            divider: false,
            labelColumnWidth: MenuLabelColumn);

        y += Crystarium.Section(
            "##import-menu-transform", "Transform",
            new Vector2(origin.X, y), width, true, null,
            form =>
            {
                // Brio's icon row disables under Smart Import and whenever
                // Expression is checked (FileUIHelpers.cs:514-516) — the
                // engine forces every component on those paths. The Model
                // toggle sits only under the OUTER Smart disable, like
                // Brio's model-transform icon.
                bool locked = _typeExpression || _smartImport;
                string? why = locked
                    ? "Expression imports always apply every component"
                    : null;
                form.Checkboxes(
                    "Apply",
                    locked,
                    ("Position", _position, next => _position = next, why),
                    ("Rotation", _rotation, next => _rotation = next, why),
                    ("Scale", _scale, next => _scale = next, why));
                form.Checkbox(
                    "Model", _modelTransform,
                    next => _modelTransform = next,
                    help: "Also move the actor to the file's placement "
                        + "(model transform)",
                    disabled: _smartImport);
            },
            labelColumnWidth: MenuLabelColumn);

        y += Crystarium.Section(
            "##import-menu-scope", "Scope",
            new Vector2(origin.X, y), width, true, null,
            form =>
            {
                form.Checkboxes(
                    "Bones",
                    ("Selected", _selectedOnly, next => _selectedOnly = next,
                        "Import only the bones selected in the sidebar"),
                    ("Descendants", _descendants, next => _descendants = next,
                        "Include descendants of the selected bones"));
                form.Checkbox(
                    "Reset first", _reset, next => _reset = next,
                    help: "Clear every bone in scope before importing, "
                        + "including ones the file does not contain");
                // Brio: Custom Import Options is live ONLY when neither
                // type is checked (FileUIHelpers.cs:504) — the filter
                // shapes the DEFAULT import path alone.
                bool typed = _typeBody || _typeExpression;
                form.Actions("Filter", actions => actions.Button(
                    "Bone filter", () => RequestBoneFilterMenu(),
                    disabled: typed,
                    help: typed
                        ? "The bone filter shapes the default import; "
                            + "uncheck Body and Expression to edit it"
                        : "Choose which bone categories imports may touch"));
            },
            labelColumnWidth: MenuLabelColumn);

        y += Crystarium.Section(
            "##import-menu-import", "Import",
            new Vector2(origin.X, y), width, true, null,
            form =>
            {
                form.Actions("File", actions => actions.Button(
                    "From file", () =>
                    {
                        if (SelectedSkeleton() is { } skeleton)
                            OpenImport(skeleton);
                    }));
                if (_importMenuWithPresets)
                    form.Actions("Presets", actions =>
                    {
                        actions.Button("A-pose",
                            () => ApplyRestPreset(RestPose.APose));
                        actions.Button("T-pose",
                            () => ApplyRestPreset(RestPose.TPose));
                    });
            },
            labelColumnWidth: MenuLabelColumn);

        float measured = (y - origin.Y) / scale
            + Crystarium.ActiveTheme.Page.Inset + MenuPadding * 2f;
        if (_importMenuWithPresets)
            _importMenuHeightPresets = measured;
        else
            _importMenuHeightPlain = measured;

        // Nested INSIDE the parent body: the open registers on the parent
        // popup's ID stack, so ImGui stacks the two and the import menu
        // stays under the filter instead of vanishing (user round 11).
        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            Crystarium.OpenPopover(BoneFilterMenuId);
        }
        // Beside the import menu, top-aligned: same-anchor stacking hid the
        // parent under the filter and read as the menu closing.
        var menuPos = ImGui.GetWindowPos();
        float gap = Crystarium.ActiveTheme.Floating.AnchorGap * scale;
        var beside = new Vector2(
            menuPos.X + ImGui.GetWindowSize().X + gap,
            menuPos.Y - gap);
        Crystarium.FloatingSurface.Popup(
            BoneFilterMenuId,
            new FloatingSurfaceProps
            {
                Width = FilterMenuWidth,
                Height = _boneFilterHeight,
                Padding = MenuPadding,
                AnchorMin = beside,
                AnchorMax = beside,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawBoneFilterBody);
    }

    /// <summary>Brio's export popup (DrawExportPoseMenuPopup): export to a
    /// file, and the stash copy. Clipboard export is a pending flow of its
    /// own.</summary>
    private void DrawExportMenuBody()
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;

        float top = origin.Y - MenuTitleOffset(scale);
        float y = top + Crystarium.Section(
            "##export-menu", "Export pose",
            new Vector2(origin.X, top), width, true, null,
            form =>
            {
                form.Actions("File", actions => actions.Button(
                    "To file", () =>
                    {
                        if (SelectedSkeleton() is { } skeleton)
                            OpenExport(skeleton);
                    }));
                form.Actions("Copy", actions => actions.Button(
                    "To stash", () =>
                    {
                        if (SelectedSkeleton() is { } skeleton)
                            _status = _poseFacade.Stash(skeleton.Actor) is
                                { Success: false } failed
                                ? $"Stash: {failed.Detail}"
                                : string.Empty;
                    },
                    help: "Hold this pose so it can be applied to another "
                        + "actor from the inspector's Transfer group"));
            },
            divider: false);

        _exportMenuHeight = (y - origin.Y) / scale
            + Crystarium.ActiveTheme.Page.Inset + MenuPadding * 2f;
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

    /// <summary>Brio's bone-filter editor on the glass surface: per-group
    /// non-collapsible sections whose HEADER ROW carries the group's own
    /// tristate checkbox at the control edge — all / none / a dot for
    /// partial — and a form checkbox row per category, the list scrolling
    /// inside the popover. Checked = the category applies.</summary>
    private void DrawBoneFilterBody()
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        var page = Crystarium.ActiveTheme.Page;

        float top = origin.Y - MenuTitleOffset(scale);
        float y = top + Crystarium.Section(
            "##filter-head", "Bone filter",
            new Vector2(origin.X, top), width, true, null,
            form => form.Actions(string.Empty, actions =>
            {
                actions.Button("All", () => _disabledCategories.Clear());
                actions.Button("None", () =>
                {
                    foreach (var group in Files.ImportBoneCategories.Groups)
                        foreach (var category in group.Categories)
                            _disabledCategories.Add(category.Id);
                });
            }, fullWidth: true),
            divider: false,
            labelColumnWidth: MenuLabelColumn);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
        float scrollHeight =
            _boneFilterHeight - (y - origin.Y) / scale
            - page.Inset - MenuPadding * 2f;
        Crystarium.ScrollRegion(
            "##filter-scroll", width / scale + MenuPadding, scrollHeight, _ =>
            {
                var top = ImGui.GetCursorScreenPos();
                // The region reaches the window edge so the scrollbar sits
                // guttered there; the ROWS keep the menu's inset.
                float innerWidth =
                    ImGui.GetContentRegionAvail().X - MenuPadding * scale;
                float sy = top.Y;
                sy += Crystarium.Section(
                    "##filter-list", string.Empty,
                    new Vector2(top.X, sy), innerWidth, true, null,
                    form =>
                    {
                        bool first = true;
                        foreach (var group in Files.ImportBoneCategories.Groups)
                        {
                            var categories = group.Categories;
                            int enabled = 0;
                            foreach (var category in categories)
                            {
                                if (!_disabledCategories.Contains(category.Id))
                                    enabled++;
                            }
                            bool all = enabled == categories.Length;
                            bool partial = enabled > 0 && !all;

                            if (!first)
                                form.Divider();
                            first = false;

                            form.CheckRow(
                                group.Name, all,
                                next =>
                                {
                                    foreach (var category in categories)
                                    {
                                        if (next)
                                            _disabledCategories.Remove(category.Id);
                                        else
                                            _disabledCategories.Add(category.Id);
                                    }
                                },
                                partial: partial,
                                help: partial
                                    ? "Some of this group is on; click for all"
                                    : null);
                            foreach (var category in categories)
                            {
                                var id = category.Id;
                                form.CheckRow(
                                    category.Name,
                                    !_disabledCategories.Contains(id),
                                    next =>
                                    {
                                        if (next)
                                            _disabledCategories.Remove(id);
                                        else
                                            _disabledCategories.Add(id);
                                    },
                                    indent: true);
                            }
                        }
                    },
                    divider: false);
                ImGui.SetCursorScreenPos(new Vector2(top.X, sy));
                ImGui.Dummy(new Vector2(1f, 1f));
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
        // Brio's dispatch table (FileUIHelpers.cs:697-718), all four rows:
        // both types = everything with every component, toggles ignored;
        // Body-only = face excluded, toggles honored; Expression-only = the
        // expression dance (engine forces components); NEITHER — Brio's
        // default state — = rotation-only toggles over everything except
        // what the bone filter excludes (its gear edits exactly this path;
        // DefaultImporterOptions ships weapon+ex disabled). Selected-bones
        // rides any type, keeps the toggles, and reduces to the frozen
        // BoneId filter at the facade.
        bool selected = _selectedOnly;
        bool both = _typeBody && _typeExpression;
        bool neither = !_typeBody && !_typeExpression;
        bool expression = _typeExpression && !_typeBody && !selected;
        bool full = both || selected;
        bool allComponents = both && !selected;
        var options = new PoseImportOptions
        {
            ApplyRotation = allComponents || _rotation,
            ApplyPosition = allComponents || _position,
            ApplyScale = allComponents || _scale,
            ApplyBody = true,
            AsExpression = expression,
            ApplyFace = full || neither,
            ApplyMainHand = full,
            ApplyOffHand = full,
            ApplyProp = full || neither,
            ApplyOrnament = full || neither,
            ResetBeforeImport = _reset,
            FilterIncludesDescendants = _descendants,
            FreezeOnImport = _freeze,
            ApplyModelTransform = _modelTransform,
        };
        return neither && !selected ? ApplyCategoryFilter(options) : options;
    }
}
