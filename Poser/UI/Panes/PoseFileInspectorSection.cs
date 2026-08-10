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
    private readonly Game.Preview.PosePreviewService _preview;

    /// <summary>This section's drive of the ONE shared preview, used only while
    /// the import dialog is open — the same binder the library rail runs, so
    /// the highlight/option compare has one implementation.</summary>
    private readonly PosePreviewBinder _importPreview;
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
    private bool _reset;

    // ── the two Brio menus (one shared state for FILES and the library) ──
    // Import menu: Brio's import popup (FileUIHelpers.DrawImportPoseMenuPopup)
    // — Freeze / Smart Import / Import Type / transform toggles / presets.
    // Bone-filter menu: Brio's category filter (PosingEditorCommon.
    // DrawBoneFilterEditor) — group tristates + per-category checkboxes.
    // ON by default — the user's call (2026-08-09), deviating from
    // Brio's unchecked static: smart routing is the wanted default and the
    // Apply/Model toggles wear Brio's smart lock until it is unchecked.
    private bool _smartImport = true;
    private bool _modelTransform;
    // Brio's DefaultImporterOptions filter starts with weapon and ex
    // disabled (PosingService.cs:45-47); the menu edits from there.
    private readonly HashSet<string> _disabledCategories =
        new(StringComparer.Ordinal) { "weapon", "ex" };
    private bool _importMenuRequested;
    private bool _importMenuWithPresets;
    private bool _boneFilterRequested;

    // Seeded from config and written back on toggle: the checkbox IS the
    // persisted FreezeActorOnPoseImport default (Brio's popup checkbox +
    // hidden config flag as one surface).
    private bool _freeze;

    // ── Brio's two popup recall slots (FileUIHelpers.cs:440-441) ──
    // _lastused: whatever the last import came from, recorded by the dispatch
    // itself (:678) so "Reapply Last Pose" repeats it through the CURRENT
    // options rather than the ones it originally landed with. Exactly one of
    // the two is set.
    private string? _lastImportPath;
    private PoseFile? _lastImportPose;

    // _stash: a FULL absolute pose capture the export menu fills and the
    // import menu applies. Distinct from _poseFacade.Stash — that one holds a
    // PortablePose for the inspector's Transfer group and carries authored
    // layers, not a pose file.
    private PoseFile? _poseStash;
    private DateTimeOffset? _poseStashedAt;

    private bool HasLastImport => _lastImportPath != null || _lastImportPose != null;

    /// <summary>Raised by the Library… action and by an Import… that the
    /// library setting has taken over. The UI manager owns the window.</summary>
    public event Action? OnLibraryRequested;

    public PoseFileInspectorSection(
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        Config.ConfigurationService config,
        IAutoSaveService autoSave,
        Game.Preview.PosePreviewService preview)
    {
        _poseFacade = poseFacade;
        _selection = selection;
        _config = config;
        _autoSave = autoSave;
        _preview = preview;
        _importPreview = new PosePreviewBinder(preview);
        _freeze = config.Config.FreezeActorOnPoseImport;

        // The import dialog is a THREE-column surface (user design): the file
        // list, the live preview of what the highlighted file does under the
        // options as they stand, and the options themselves. Declared once —
        // the dialog sizes itself around them.
        _importBrowser.SidePanels.Add(
            new FileSidePanel(ImportPreviewColumnWidth, DrawImportPreviewPanel));
        _importBrowser.SidePanels.Add(
            new FileSidePanel(ImportOptionsColumnWidth, DrawImportOptionsPanel));
    }

    public void DrawBrowsers()
    {
        _importBrowser.Draw();
        _exportBrowser.Draw();
        DrawMenus();
        ReleaseImportPreview();
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

    /// <summary>Opens the bone-filter menu on the next pump — nested
    /// beside the import menu when that popup is open, at the click
    /// otherwise (the library rail's button).</summary>
    public void RequestBoneFilterMenu()
    {
        _filterAnchor = ImGui.GetMousePos();
        _boneFilterRequested = true;
    }

    private Vector2 _filterAnchor;

    /// <summary>The bone-filter menu's verdict folded into any surface's
    /// options: disabled prefix categories become exclusions, the slot rows
    /// (Weapons / Emote Props / Fashion Accessories) gate their slots, and
    /// a disabled Other row bans uncategorized bones. The fold itself lives
    /// in PosingCore beside the catalog, so the tests pin the same code the
    /// popup runs.</summary>
    public PoseImportOptions ApplyCategoryFilter(PoseImportOptions options) =>
        Files.ImportBoneCategories.ApplyDisabledCategories(
            options, _disabledCategories);

    /// <summary>
    /// The actor every command in these menus acts on: the scene selection
    /// first, then the host surface's own apply target.
    ///
    /// <para>The fallback exists because these menus are MOUNTED IN TWO
    /// PLACES. In library mode the scene selection is routinely empty — the
    /// library picks its target itself — so every command that resolved
    /// through the selection alone silently ate the click there ("From
    /// file" did nothing, user 2026-08-10).</para>
    /// </summary>
    private ISkeleton? SelectedSkeleton()
    {
        foreach (var id in _selection.Selected)
        {
            // A BONE selection names its owning actor just as well — the
            // actor-only lookup made every command dead while a bone was
            // selected, which is most of the time in the pose workspace.
            var actorId = id switch
            {
                { Kind: SceneEntityKind.Actor, Actor: { } selected } => selected,
                { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
                _ => (Domain.Identity.ActorId?)null,
            };
            if (actorId is { } resolvedId &&
                _resolveActor?.Invoke(resolvedId) is { HasSkeleton: true } actor)
                return actor.Skeleton;
        }
        if (HostPushLive && _hostTarget is { HasSkeleton: true } fallback)
            return fallback.Skeleton;
        return null;
    }

    /// <summary>
    /// The hosting pane's per-frame push — the same idiom as
    /// <see cref="SetPreviewVisible"/>: WHO an apply would land on when the
    /// scene selection names nobody, and whether the library is the surface
    /// hosting these options.
    ///
    /// <para>Stamped with the frame it arrived on rather than cleared by a
    /// teardown hook: the pane and the menu pump draw in an order neither
    /// owns, so a push is trusted for the frame it was made and the next one,
    /// and a pane that stops drawing stops being consulted on its own.</para>
    /// </summary>
    public void SetHostImportTarget(IActor? target, bool inLibrary)
    {
        _hostTarget = target;
        _hostIsLibrary = inLibrary;
        _hostPushFrame = ImGui.GetFrameCount();
    }

    private IActor? _hostTarget;
    private bool _hostIsLibrary;
    private int _hostPushFrame = int.MinValue;

    private bool HostPushLive => ImGui.GetFrameCount() - _hostPushFrame <= 1;

    /// <summary>Whether these options are being drawn by the library pane —
    /// its "From library" row has nowhere to go.</summary>
    private bool InLibrary => HostPushLive && _hostIsLibrary;

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
    private const float FilterMenuWidth = 216f;
    // 78: the longest label ("Reset first") plus breath — the slack the
    // old 96 left at the label side was exactly what the caption pairs
    // were missing at the right edge (user: almost overflowing).
    private const float MenuLabelColumn = 78f;
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

        // The rail's filter requests have no import menu to nest under;
        // they open here at the click. A request made while the import menu
        // is open is consumed by its nested pump instead — and so is one made
        // from the import DIALOG's options column: that popup belongs to the
        // dialog's window, both in ImGui's id and in the exclusive chain, so
        // the root pump has to keep its hands off it entirely.
        if (!_importBrowser.IsOpen)
        {
            if (_boneFilterRequested && !ImGui.IsPopupOpen(ImportMenuId))
            {
                _boneFilterRequested = false;
                Crystarium.OpenPopover(BoneFilterMenuId);
            }
            DrawBoneFilterMenu(_filterAnchor);
        }

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
        float top = origin.Y - MenuTitleOffset(scale);

        float y = DrawOptionsSections(
            new Vector2(origin.X, top), width, _importMenuWithPresets);

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
        DrawBoneFilterMenu(new Vector2(
            menuPos.X + ImGui.GetWindowSize().X + gap,
            menuPos.Y - gap));
    }

    /// <summary>The filter popup at an anchor. One pump, three mounts: the
    /// root one, the import menu's nested one, and the import dialog's.
    /// </summary>
    private void DrawBoneFilterMenu(Vector2 anchor) =>
        Crystarium.FloatingSurface.Popup(
            BoneFilterMenuId,
            new FloatingSurfaceProps
            {
                Width = FilterMenuWidth,
                Height = _boneFilterHeight,
                Padding = MenuPadding,
                AnchorMin = anchor,
                AnchorMax = anchor,
                Treatment = FloatingSurfaceTreatment.Glass,
            },
            DrawBoneFilterBody);

    /// <summary>The library-mode inspector rail: the SAME option sections
    /// the import menu shows, hosted where the rail's selection sections
    /// would be — the user's placement. Presets stay actor-side; the bone
    /// filter button opens through the root pump since no menu popup hosts
    /// a nested one here.</summary>
    public void DrawOptionsRail(Vector2 origin, Vector2 size)
    {
        DrawOptionsSections(
            origin, size.X, withPresets: false,
            previewCap: size.Y * PreviewRailShare);
    }

    // ── the import dialog's two columns ──────────────────────────────────
    // The user's design: pick a file, see exactly what the options standing
    // right now would make of it, confirm. The dialog's own Load button is the
    // import, so neither column carries an action that leaves it.

    /// <summary>The preview column, logical px: the width at which Ktisis'
    /// portrait aspect fills the dialog's body height exactly — 176 less two
    /// page insets is 152, and 152 by that aspect is the 253 the body leaves
    /// once the camera band is off it. Wider only pads the column: the block
    /// narrows the render to hold the aspect rather than stretch it.</summary>
    private const float ImportPreviewColumnWidth = 176f;

    /// <summary>The options column, logical px — the import menu's own width,
    /// which is what the section stack was tuned against.</summary>
    private const float ImportOptionsColumnWidth = MenuWidth;

    /// <summary>Shown in the empty well before any file is highlighted.
    /// </summary>
    private const string ImportPreviewIdleText = "Pick a pose file to preview.";

    private const string ImportOptionsScrollId = "##import-dialog-options";

    /// <summary>The actor the OPEN dialog's import will land on, captured with
    /// the dialog exactly as the confirm callback captures its skeleton: the
    /// preview borrows the appearance of the body the file is going to.
    /// </summary>
    private IActor? _importTarget;

    /// <summary>Whether the dialog was driving the shared preview last frame —
    /// the edge <see cref="ReleaseImportPreview"/> hands the seat back on.
    /// </summary>
    private bool _importPreviewOwned;

    /// <summary>The dialog's PREVIEW column: the highlighted file on a hidden
    /// body, with the inspector rail's own interactions — one block, two
    /// mounts.</summary>
    private void DrawImportPreviewPanel(
        Vector2 origin, Vector2 size, string? highlighted)
    {
        float inset =
            Crystarium.ActiveTheme.Page.Inset
            * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        SyncImportPreview(highlighted);
        DrawPreviewBlock(
            origin + new Vector2(inset),
            size - new Vector2(inset * 2f),
            ImportPreviewIdleText);
    }

    /// <summary>
    /// The dialog's OPTIONS column: the same section stack the import menu
    /// shows, minus every action and minus the preview block — this dialog has
    /// a preview column of its own — scrolling inside its own column. NO right
    /// padding on the region: the scrollbar sits on the column's edge and IS
    /// the trailing inset, the same rule the file list follows.
    /// </summary>
    private void DrawImportOptionsPanel(
        Vector2 origin, Vector2 size, string? highlighted)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        float inset = Crystarium.ActiveTheme.Page.Inset * scale;
        ImGui.SetCursorScreenPos(origin + new Vector2(inset));
        Crystarium.ScrollRegion(
            ImportOptionsScrollId,
            (size.X - inset) / scale,
            (size.Y - inset * 2f) / scale,
            region =>
            {
                var top = ImGui.GetCursorScreenPos();
                float y = DrawOptionsSections(
                    top,
                    region.ContentWidth * scale,
                    withPresets: false,
                    withActions: false);
                ImGui.SetCursorScreenPos(new Vector2(top.X, y));
                ImGui.Dummy(new Vector2(1f, 1f));
            });
        DrawNestedBoneFilter();
    }

    /// <summary>
    /// The bone-filter popup, opened AND pumped inside the dialog's window.
    /// Both halves have to happen here: a claim nests under whatever owns the
    /// frame it is made in, so opening this from the root pump would truncate
    /// the dialog's own link out of the exclusive chain and close it — and
    /// ImGui keys a popup on the window that opened it either way.
    /// </summary>
    private void DrawNestedBoneFilter()
    {
        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            Crystarium.OpenPopover(BoneFilterMenuId);
        }
        DrawBoneFilterMenu(_filterAnchor);
    }

    /// <summary>
    /// What the preview shows while the dialog is open: the HIGHLIGHTED file on
    /// the actor the confirm would land on, through the options as they stand
    /// right now, trimmed to a pose. Re-poses on a highlight change AND on an
    /// option change — the binder's compare is the library rail's.
    ///
    /// <para>A highlight that is not a pose file — a folder row, and the empty
    /// selection every folder change leaves — holds whatever stands rather than
    /// tearing the CharaView down: navigating a tree would otherwise release
    /// and re-initialise the render on every step.</para>
    ///
    /// <para>Brio's .cmp preset substitution is part of what the options
    /// PRODUCE, so the preview shows it. Smart Import is not: its routing
    /// MUTATES the type pair, and merely highlighting a file must never flip
    /// the checkboxes beside it.</para>
    /// </summary>
    private void SyncImportPreview(string? highlighted)
    {
        if (highlighted is null
            || !IsPoseFile(highlighted)
            || _importTarget is not { } source)
            return;

        var built = CmpImportOverride(highlighted, out bool blocked, out _);
        if (blocked)
            return;
        var options = PosePreviewBinder.Trim(built ?? BuildOptions());
        if (_importPreview.Begin(source, highlighted, options))
            _importPreview.Pose(highlighted, options);
    }

    private static bool IsPoseFile(string path) =>
        path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The preview handshake's other half. While the browser is open the dialog
    /// DRIVES the one shared preview and the library pane stands down; the
    /// frame it closes, the seat goes back — to the library if it still claims
    /// it, and to the game otherwise, which is a close.
    /// </summary>
    private void ReleaseImportPreview()
    {
        if (_importBrowser.IsOpen)
        {
            _importPreviewOwned = true;
            return;
        }
        if (!_importPreviewOwned)
            return;
        _importPreviewOwned = false;
        _importTarget = null;
        if (PreviewClaimed)
            _importPreview.StandDown();
        else
            _importPreview.Close();
    }

    /// <summary>The library pane's push: whether the rail leads with the live
    /// pose preview. Restated every frame the pane draws, and false the moment
    /// it stops — the section must never draw a preview the pane has closed.
    /// </summary>
    public void SetPreviewVisible(bool visible) => _previewVisible = visible;

    private bool _previewVisible;

    /// <summary>
    /// The library pane's other push: whether it WOULD be driving the shared
    /// preview if the import dialog were not. Stated every frame the pane
    /// draws, with the same frame-count slack as the host target — the pane and
    /// the browser pump draw in an order neither owns — and it is what decides
    /// whether closing the dialog hands the seat back or gives it up.
    /// </summary>
    public void SetPreviewClaim(bool claimed)
    {
        _previewClaimed = claimed;
        _previewClaimFrame = ImGui.GetFrameCount();
    }

    private bool _previewClaimed;
    private int _previewClaimFrame = int.MinValue;

    private bool PreviewClaimed =>
        _previewClaimed && ImGui.GetFrameCount() - _previewClaimFrame <= 1;

    /// <summary>Whether the import dialog is driving the shared pose preview.
    /// There is ONE CharaView, so the library pane stands down while this is
    /// true — without closing the service, which the dialog is feeding.
    /// </summary>
    public bool IsImportPreviewActive => _importBrowser.IsOpen;

    /// <summary>An orbit drag holds the pointer — see
    /// <see cref="DrawPreviewInput"/>.</summary>
    private bool _previewDragging;

    /// <summary>The most of the RAIL the preview's image may take, so the
    /// import options under it stay usable at any window height. Raised from
    /// 0.45 on user request (2026-08-09, "a tiny bit bigger"): at 0.45 the cap
    /// bit on typical rail heights and narrowed the box off its full width.
    /// </summary>
    private const float PreviewRailShare = 0.58f;

    /// <summary>Ktisis' preview node, and so the image box's ASPECT: the whole
    /// render is stretched into a 192x320 portrait there, which is why the box
    /// never letterboxes and never consults the render's own size.</summary>
    private static readonly Vector2 PreviewAspect = new(192f, 320f);

    /// <summary>Shown while the service has stated no reason of its own — the
    /// first frames of a render.</summary>
    private const string PreviewWaitingText = "Preparing preview…";

    /// <summary>Camera distance per zoom BUTTON click. User-tuned in game
    /// (2026-08-09): 10 was far too coarse, halved on request. Zoom in =
    /// negative delta.</summary>
    private const float PreviewZoomButtonStep = 5f;

    /// <summary>Camera distance per wheel notch. User-tuned in game: the
    /// original fine 0.25 felt right and this is exactly twice it, per
    /// request — the wheel accumulates, so it stays much finer than the
    /// buttons.</summary>
    private const float PreviewZoomWheelStep = 0.5f;

    /// <summary>Degrees of yaw per pixel dragged sideways across the render.
    /// </summary>
    private const float PreviewDragYawScale = 0.5f;

    /// <summary>View travel per pixel dragged vertically across the render, in
    /// native world units — the ~430px tall image shows about 2.5 units, so a
    /// drag carries the render with the cursor at roughly one to one. Sign
    /// user-tuned in game (2026-08-09): drag GRABS the render (down pulls the
    /// body down, the view climbs).</summary>
    private const float PreviewDragPanScale = 0.006f;

    /// <summary>The camera band's groups in order — the zoom pair (for wheels
    /// the user doesn't have) and the reset. Rotate and pan buttons are gone
    /// by user call (2026-08-09): the drag IS the rotate/pan surface.
    /// </summary>
    private static readonly int[] PreviewCameraGroups = [2, 1];

    /// <summary>The import-option section stack, shared verbatim by the
    /// popup body, the library rail and the import dialog's options column.
    /// Returns the y past the last section.</summary>
    /// <param name="previewCap">The tallest the preview image may be, in
    /// screen px; zero keeps the preview out entirely — the popup mount has
    /// no room for one and never asks.</param>
    /// <param name="withActions">The Import section — from file, clipboard,
    /// recall, presets. False leaves the OPTIONS alone: the import dialog's
    /// own confirm button is its import, and every source row there would be
    /// a way out of the dialog it is standing in.</param>
    private float DrawOptionsSections(
        Vector2 origin, float width, bool withPresets, float previewCap = 0f,
        bool withActions = true)
    {
        float y = origin.Y;

        bool preview = _previewVisible && previewCap > 0f;
        if (preview)
            y += Crystarium.Section(
                "##pose-preview", "Preview",
                new Vector2(origin.X, y), width, true, null,
                form => DrawPreviewBody(form, width, previewCap),
                divider: false,
                labelColumnWidth: MenuLabelColumn);

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
            // The rule is a divider BETWEEN sections: this one leads the stack
            // only when the preview does not.
            divider: preview,
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
                // The component trio takes its OWN full-width row under
                // the label (user placement).
                form.Label("Apply");
                form.Checkboxes(
                    string.Empty,
                    locked,
                    fullWidth: true,
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
                // Brio's popup has no selected-bones or descendants row —
                // both were Ktisis imports and are gone (user 2026-08-10).
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

        if (!withActions)
            return y;

        y += Crystarium.Section(
            "##import-menu-import", "Import",
            new Vector2(origin.X, y), width, true, null,
            form =>
            {
                // Brio's order (FileUIHelpers.cs:568-575): From File, then
                // From Clipboard. From library is Poser's stand-in for Brio's
                // library-when-importing redirect.
                form.Actions("File", actions =>
                {
                    actions.Button("From file", () =>
                    {
                        if (SelectedSkeleton() is { } skeleton)
                            OpenImport(skeleton);
                        else
                            _status = "Select an actor first.";
                    });
                    // Disabled rather than hidden in library mode: the row
                    // geometry stays put and the reason is readable.
                    actions.Button("From library",
                        () => OnLibraryRequested?.Invoke(),
                        disabled: InLibrary,
                        help: InLibrary ? "The library is already open" : null);
                });
                form.Actions("Clipboard", actions => actions.Button(
                    "From clipboard", ImportFromClipboard,
                    help: "Import the pose held on the clipboard — Brio's "
                        + "copy is read as-is"));
                // Brio's next two rows (FileUIHelpers.cs:597-607), both
                // disabled until their slot holds something.
                form.Actions("Recall", actions =>
                {
                    actions.Button(
                        "Reapply last", ReapplyLastPose,
                        disabled: !HasLastImport,
                        help: HasLastImport
                            ? "Import the last pose again, through the "
                                + "options set here now"
                            : "Nothing has been imported yet");
                    actions.Button(
                        "From stash", ImportFromStash,
                        disabled: _poseStash == null,
                        help: _poseStash == null
                            ? "Nothing is stashed — use Export ▸ To stash first"
                            : $"Apply the stashed pose (stashed {_poseStashedAt:HH:mm:ss} UTC)");
                });
                if (withPresets)
                    form.Actions("Presets", actions =>
                    {
                        actions.Button("A-pose",
                            () => ApplyRestPreset(RestPose.APose));
                        actions.Button("T-pose",
                            () => ApplyRestPreset(RestPose.TPose));
                    });
                // The popup is where a clipboard paste fails, so it is where
                // the reason has to appear — the FILES area's own status row
                // is behind it, and the library rail has none at all.
                if (_status.Length > 0)
                    form.Status(_status);
            },
            labelColumnWidth: MenuLabelColumn);

        return y;
    }

    /// <summary>
    /// The live render and its seven camera commands, seated as two canvas rows
    /// so the section owns the flow and the block owns nothing but its band.
    /// The image box keeps KTISIS' node aspect and the render fills it wall to
    /// wall — no letterbox bars beside a portrait — capped so the option
    /// sections under it stay reachable.
    /// </summary>
    private void DrawPreviewBody(
        Crystarium.FormScope form, float width, float cap)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var box = PreviewBox(width, cap);
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        form.Canvas("preview-image", box.Y / scale,
            (min, size) => DrawPreviewImage(min, size, box.X, scale, theme));
        int rows = PreviewCameraRows(width, scale, theme);
        form.Canvas(
            "preview-camera",
            PreviewCameraHeight(rows, theme),
            (min, size) => DrawPreviewCamera(
                min + new Vector2(0f, theme.Spacing.Three * scale),
                size.X, scale, theme, rows));
    }

    /// <summary>
    /// The same block against a plain (origin, size) box rather than a form
    /// flow — the import dialog's preview column. The camera band is taken off
    /// the bottom first, so the image gets everything the buttons leave and the
    /// two mounts share every rule that shapes them.
    /// </summary>
    private void DrawPreviewBlock(
        Vector2 origin, Vector2 size, string? emptyText)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        int rows = PreviewCameraRows(size.X, scale, theme);
        float camera = PreviewCameraHeight(rows, theme) * scale;
        var box = PreviewBox(size.X, MathF.Max(0f, size.Y - camera));
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        DrawPreviewImage(
            origin, new Vector2(size.X, box.Y), box.X, scale, theme, emptyText);
        DrawPreviewCamera(
            origin + new Vector2(0f, box.Y + theme.Spacing.Three * scale),
            size.X, scale, theme, rows);
    }

    /// <summary>The image box for a band of this width under this height cap:
    /// Ktisis' portrait aspect, and where the cap bites, a narrower box that
    /// holds the aspect and centres in the band rather than a stretched
    /// render.</summary>
    private static Vector2 PreviewBox(float width, float cap)
    {
        float height = width * (PreviewAspect.Y / PreviewAspect.X);
        return height > cap
            ? new Vector2(cap * (PreviewAspect.X / PreviewAspect.Y), cap)
            : new Vector2(width, height);
    }

    /// <summary>What the camera band spends under the image, unscaled: its
    /// leading gap, the button rows, and the gap between them.</summary>
    private static float PreviewCameraHeight(int rows, Theme theme) =>
        theme.Spacing.Three
        + theme.Floating.CloseActionSize * rows
        + theme.Page.ActionGap * (rows - 1);

    /// <param name="boxWidth">The image box's own width in screen px, which is
    /// the band's except where the height cap narrowed it; the box then
    /// centres in the band.</param>
    /// <param name="emptyText">What the empty well says when the service has
    /// stated no reason of its own — the dialog's column has one before any
    /// file is highlighted, the rail never does.</param>
    private void DrawPreviewImage(
        Vector2 min, Vector2 size, float boxWidth, float scale, Theme theme,
        string? emptyText = null)
    {
        var boxMin = theme.Optical.Snap(
            min + new Vector2((size.X - boxWidth) * 0.5f, 0f));
        var boxSize = new Vector2(boxWidth, size.Y);
        var boxMax = boxMin + boxSize;
        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;

        var handle = _preview.TextureHandle;
        if (handle != 0)
        {
            // Ktisis PreviewNode: the WHOLE render, uv 0..1, into the node —
            // no aspect fit, so no well shows beside it.
            draw.AddImage(
                new ImTextureID(handle),
                boxMin,
                boxMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Vector4.One)));
            DrawPreviewInput(boxMin, boxSize);
        }
        else
        {
            // Nothing to show: the box is a plain well carrying the reason.
            // The service's own wins; "preparing" is only what the frames
            // before it has one say.
            draw.AddRectFilled(
                boxMin, boxMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.InputWell)),
                radius);
            Crystarium.TextInBand(
                boxMin,
                boxSize,
                _preview.StatusText ?? emptyText ?? PreviewWaitingText,
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = theme.FormHint,
                },
                TextAlign.Center);
        }
        Crystarium.FloatingSurface.DrawBorder(boxMin, boxMax, radius);
    }

    /// <summary>
    /// The render is the camera's own control surface: left-drag orbits it
    /// sideways and pans it vertically (the banner editor's split), and the
    /// wheel dollies it on the ZoomIn button's convention — wheel up = closer =
    /// negative delta.
    ///
    /// One invisible button carries both, which is also what makes them safe.
    /// The DRAG capture is ImGui's active id, the same handshake
    /// <see cref="Interactive.Reserve"/> runs: the press has to land on the
    /// image to take it, it holds until release however far the pointer
    /// strays, and while it holds nothing underneath — the rail, the tile grid
    /// — sees the button at all. Ownership is taken on the activation EDGE
    /// alone, so a press that landed under a floating surface never drags and
    /// a surface opening mid-drag never cancels one.
    ///
    /// The WHEEL has to be claimed rather than merely read: this band sits
    /// inside the shell rail's scrolling child, and an unclaimed notch scrolls
    /// the rail out from under the pointer. <c>SetItemUsingMouseWheel</c> is
    /// ImGui's own claim — it marks the hovered item as the wheel's owner and
    /// the next frame's scroll pass skips the window entirely.
    /// </summary>
    private void DrawPreviewInput(Vector2 min, Vector2 size)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##pose-preview-canvas", size);
        ImGuiP.SetItemUsingMouseWheel();
        bool occluded = Interactive.PointerOccluded();

        if (ImGui.IsItemActivated() && !occluded)
            _previewDragging = true;
        if (_previewDragging)
        {
            if (!ImGui.IsItemActive())
            {
                _previewDragging = false;
            }
            else
            {
                // Sideways ORBITS, vertically PANS — and the pan axis GRABS
                // the render: dragging down pulls the body down and the view
                // climbs (the user's expectation, inverse of the banner
                // editor's frame-follows-cursor).
                var drag = ImGui.GetIO().MouseDelta;
                if (drag.X != 0f)
                    _preview.Rotate(drag.X * PreviewDragYawScale);
                if (drag.Y != 0f)
                    _preview.Pan(-drag.Y * PreviewDragPanScale);
            }
        }

        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f && ImGui.IsItemHovered() && !occluded)
            _preview.Zoom(-wheel * PreviewZoomWheelStep);
    }

    /// <summary>One band when the buttons fit the rail's content width, two
    /// when they do not. The trimmed set (zoom pair + reset) fits every
    /// theme's rail in one band; the wrap stays for whatever the band grows
    /// next.</summary>
    private static int PreviewCameraRows(float width, float scale, Theme theme)
        => PreviewCameraBandWidth(
            PreviewCameraGroups, 0, PreviewCameraGroups.Length, scale, theme)
            <= width ? 1 : 2;

    /// <summary>The width a run of camera groups occupies: buttons, the tight
    /// gap inside each group, and the wider gap between them.</summary>
    private static float PreviewCameraBandWidth(
        int[] groups, int first, int last, float scale, Theme theme)
    {
        float action = theme.Floating.CloseActionSize * scale;
        float within = theme.Spacing.Two * scale;
        float between = theme.Page.ActionGap * scale;
        float total = -between;
        for (int g = first; g < last; g++)
            total += between + groups[g] * action + (groups[g] - 1) * within;
        return total;
    }

    /// <summary>The camera band, centred under the image: the zoom pair and
    /// the reset — rotate and pan live on the drag. The buttons speak to the
    /// service directly — the camera is the preview's own state and no pane
    /// holds any of it.</summary>
    /// <param name="rows">1 or 2, from <see cref="PreviewCameraRows"/>.</param>
    private void DrawPreviewCamera(
        Vector2 origin, float width, float scale, Theme theme, int rows)
    {
        var groups = PreviewCameraGroups;
        float actionPx = theme.Floating.CloseActionSize * scale;
        float within = theme.Spacing.Two * scale;
        float between = theme.Page.ActionGap * scale;
        var style = ControlStyle.Square(theme.Floating.CloseActionSize);
        int split = rows == 1 ? groups.Length : 2;
        float y = origin.Y;
        int button = 0;

        for (int row = 0; row < rows; row++)
        {
            int first = row == 0 ? 0 : split;
            int last = row == 0 ? split : groups.Length;
            float x = origin.X
                + (width - PreviewCameraBandWidth(
                    groups, first, last, scale, theme)) * 0.5f;
            for (int g = first; g < last; g++)
            {
                for (int i = 0; i < groups[g]; i++)
                {
                    DrawPreviewCameraButton(
                        button++, new Vector2(x, y), style);
                    x += actionPx + (i + 1 < groups[g] ? within : 0f);
                }
                x += between;
            }
            y += actionPx + between;
        }
    }

    private void DrawPreviewCameraButton(
        int index, Vector2 position, ControlStyle style)
    {
        ImGui.SetCursorScreenPos(position);
        switch (index)
        {
            case 0:
                Crystarium.IconButton(
                    TablerIcon.ZoomOut,
                    () => _preview.Zoom(PreviewZoomButtonStep),
                    style: style,
                    help: "Move the preview camera back",
                    id: "##pose-preview-zoom-out");
                break;
            case 1:
                Crystarium.IconButton(
                    TablerIcon.ZoomIn,
                    () => _preview.Zoom(-PreviewZoomButtonStep),
                    style: style,
                    help: "Move the preview camera closer",
                    id: "##pose-preview-zoom-in");
                break;
            default:
                Crystarium.IconButton(
                    TablerIcon.ArrowBackUp,
                    () => _preview.ResetCamera(),
                    style: style,
                    help: "Reset the preview camera",
                    id: "##pose-preview-reset");
                break;
        }
    }

    /// <summary>Brio's export popup (DrawExportPoseMenuPopup): export to a
    /// file, the clipboard copy, and the stash copy.</summary>
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
                        else
                            _status = "Select an actor first.";
                    }));
                form.Actions("Copy", actions =>
                {
                    // Brio's Copy group (FileUIHelpers.cs:781-806): To
                    // Clipboard, then To Stash.
                    actions.Button(
                        "To clipboard", CopyToClipboard,
                        help: "Copy the pose in Brio's clipboard format, so "
                            + "it pastes into Brio as well as Poser");
                    actions.Button(
                        "To stash", StashPose,
                        help: "Hold this pose so the import menu's From "
                            + "stash can apply it to any actor");
                });
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
        // The inspector's own mount knows exactly whose FILES section this
        // is; push it as the host target so a BONE selection (or any
        // selection shape the actor lookup does not recognise) still resolves
        // a target for the two menus this row opens.
        SetHostImportTarget(skeleton.Actor, inLibrary: false);

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
        // freezes as complete BoneIds at dialog confirmation. The preview
        // column borrows this same actor's appearance, so what the highlight
        // shows stands on the body the confirm will pose.
        _importTarget = skeleton.Actor;
        _importBrowser.Open(initialPath, path =>
        {
            if (rememberPath)
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            ImportFromPath(skeleton, path);
        });
    }

    /// <summary>
    /// Brio's ImportPose dispatch for a file on disk (FileUIHelpers.cs:
    /// 671-718) in its exact order: resolve Smart Import first — it MUTATES
    /// the type pair, so everything after it reads the new state — record the
    /// source for "Reapply Last Pose", then the .cmp gates, then the ordinary
    /// build.
    /// </summary>
    private void ImportFromPath(ISkeleton skeleton, string path)
    {
        bool isCmp = path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);
        string notice = string.Empty;
        if (_smartImport && LoadForSmartRouting(path, isCmp) is { } smartFile)
            notice = SmartRoute(skeleton, smartFile);

        // Brio records _lastused before the dispatch (:678), so even a
        // blocked expression-only .cmp is what "Reapply last" repeats.
        _lastImportPath = path;
        _lastImportPose = null;

        var cmp = CmpImportOverride(path, out bool blocked, out var cmpNotice);
        if (cmpNotice != null)
            notice = cmpNotice;
        if (blocked)
        {
            _status = notice;
            return;
        }

        var imported = _poseFacade.ImportPose(
            skeleton.Actor, path, cmp ?? BuildOptions());
        _status = imported.Success ? notice : $"Import: {imported.Detail}";
    }

    /// <summary>
    /// Brio's .cmp gates (FileUIHelpers.cs:680-694), for every surface that
    /// dispatches its own import — this popup and the library tiles, which
    /// list .cmp files too (PoseLibraryService's LegacyExtension).
    ///
    /// <para>A .cmp with NO type checked falls through to the ordinary path,
    /// exactly as Brio's <c>isCMP &amp; (doBody || doExpression)</c> guard
    /// does. Expression is impossible for the format: with Body checked too
    /// it reports and CONTINUES as a body import, alone it reports and
    /// imports nothing.</para>
    /// </summary>
    /// <returns>The preset to substitute, or null when this is not a typed
    /// .cmp (or the import is blocked outright).</returns>
    public PoseImportOptions? CmpImportOverride(
        string path, out bool blocked, out string? notice)
    {
        blocked = false;
        notice = null;
        if (!path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase) ||
            !(_typeBody || _typeExpression))
            return null;

        if (_typeExpression)
        {
            notice = "CMP poses do not support expression import.";
            if (!_typeBody)
            {
                blocked = true;
                return null;
            }
        }
        return CmpImportOptions();
    }

    /// <summary>The in-memory twin of <see cref="ImportFromPath"/> — the
    /// clipboard, the stash and a reapply of either. No .cmp can arrive this
    /// way: the format only exists on disk, and its loader upgrades to a
    /// PoseFile before anything else sees it.</summary>
    private void ImportLoadedPose(
        ISkeleton skeleton, PoseFile pose, string description, string statusPrefix)
    {
        string notice = string.Empty;
        if (_smartImport)
            notice = SmartRoute(skeleton, pose);

        _lastImportPose = pose;
        _lastImportPath = null;

        var imported = _poseFacade.ImportPose(
            skeleton.Actor, pose, BuildOptions(), description);
        _status = imported.Success ? notice : $"{statusPrefix}: {imported.Detail}";
    }

    /// <summary>The file Smart Import classifies. A .cmp is upgraded first,
    /// exactly as Brio's OneOf match does (FileUIHelpers.cs:337-339), and a
    /// .pose has its bone names sanitized before classification (:353) so
    /// legacy Anamnesis names are judged by their game names.</summary>
    private static PoseFile? LoadForSmartRouting(string path, bool isCmp)
    {
        if (isCmp)
        {
            try
            {
                // Upgrade throws on a .cmp with no race; classification is
                // advisory, so an unreadable file just routes nothing and the
                // import itself reports the real failure.
                return CMToolPoseFile.Load(path)?.Upgrade();
            }
            catch (Exception)
            {
                return null;
            }
        }
        if (!path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase))
            return null;
        if (PoseFile.Load(path) is not { } file)
            return null;
        file.SanitizeBoneNames();
        return file;
    }

    /// <summary>Brio's DefaultCMPImporterOptions with the switches that ride
    /// every state (FileUIHelpers.cs:690-691 forwards freezeOnLoad and the
    /// model-transform override, and passes transformComponents null so the
    /// preset's rotation-only mask stands).</summary>
    private PoseImportOptions CmpImportOptions()
    {
        var options = PoseImportOptions.Cmp;
        options.ResetBeforeImport = _reset;
        options.FreezeOnImport = _freeze;
        options.ApplyModelTransform = _modelTransform;
        return options;
    }

    /// <summary>Brio's "Reapply Last Pose" (FileUIHelpers.cs:597-601): the
    /// last imported source again, through the options as they stand NOW —
    /// which is why it re-enters the same dispatch instead of replaying a
    /// stored build.</summary>
    private void ReapplyLastPose()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        if (_lastImportPath is { } path)
            ImportFromPath(skeleton, path);
        else if (_lastImportPose is { } pose)
            ImportLoadedPose(skeleton, pose, "Reapply last pose", "Reapply");
        else
            _status = "Nothing has been imported yet.";
    }

    /// <summary>Brio's "Load From Stash" (FileUIHelpers.cs:603-607): the
    /// stashed pose file through the ordinary import flow.</summary>
    private void ImportFromStash()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        if (_poseStash is not { } pose)
        {
            _status = "Nothing is stashed.";
            return;
        }
        ImportLoadedPose(skeleton, pose, "Import stashed pose", "Stash");
    }

    /// <summary>Brio's export-menu "To Stash": a FULL absolute pose capture
    /// held for the import menu, armed like the file export because the
    /// capture reads the same raw transform caches
    /// (<see cref="CleanPoseFacade.CapturePoseFile"/>).</summary>
    private void StashPose()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        var armed = _poseFacade.CapturePoseFile(skeleton.Actor, pose =>
        {
            if (pose == null)
            {
                _status = "Stash: the pose could not be captured.";
                return;
            }
            _poseStash = pose;
            _poseStashedAt = DateTimeOffset.UtcNow;
            _status = string.Empty;
        });
        if (!armed.Success)
            _status = $"Stash: {armed.Detail}";
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
    /// Brio's four-state dispatch table lives in PosingCore
    /// (<see cref="PoseImportOptions.ForImportType"/>, pinned by
    /// PoseImportTypeMatrixTests); this adds the switches that ride EVERY
    /// state, and hands the bone filter to the one state Brio lets it govern —
    /// its Custom Import Options button is dead the moment a type is checked
    /// (FileUIHelpers.cs:504).
    /// </summary>
    private PoseImportOptions BuildOptions()
    {
        var options = PoseImportOptions.ForImportType(
            _typeBody, _typeExpression, _rotation, _position, _scale,
            // Smart Import locks the component trio to each preset's own
            // (Brio nulls transformComponents every frame it is on,
            // FileUIHelpers.cs:549-552) — which is exactly why the icon row
            // draws disabled under it.
            presetComponents: _smartImport);
        options.ResetBeforeImport = _reset;
        options.FreezeOnImport = _freeze;
        // An expression import never moves the actor: Brio passes
        // applyModelTransformOverride null on that path and skips
        // ImportModelPose outright (FileUIHelpers.cs:710,
        // PosingCapability.cs:235).
        options.ApplyModelTransform = _modelTransform && !options.AsExpression;
        return _typeBody || _typeExpression
            ? options
            : ApplyCategoryFilter(options);
    }

    /// <summary>
    /// Re-derive a build as a different type pair, keeping the switches that
    /// ride every state. Brio's Smart Import works exactly this way: it flips
    /// doBody/doExpression (FileUIHelpers.cs:377-386) and the preset is then
    /// chosen from the pair (:696-717) — it never patches one scope field.
    /// Patching is what a caller must not do here either: setting AsExpression
    /// onto a Body-only build leaves the face already excluded, so the
    /// expression import has nothing left to apply.
    /// </summary>
    public PoseImportOptions RouteAsType(
        PoseImportOptions built, bool body, bool expression)
    {
        var routed = PoseImportOptions.ForImportType(
            body, expression, _rotation, _position, _scale,
            presetComponents: _smartImport);
        routed.ResetBeforeImport = built.ResetBeforeImport;
        routed.FreezeOnImport = built.FreezeOnImport;
        routed.ApplyModelTransform =
            built.ApplyModelTransform && !routed.AsExpression;
        return routed;
    }

    /// <summary>
    /// Brio's ResolveSmartImport (FileUIHelpers.cs:332-438) on a loaded file.
    /// A face-only or expression-tagged file routes to Expression, a
    /// body-tagged or face-less file to Body, and a MIXED file is left alone —
    /// Brio's classification has no else branch.
    ///
    /// <para>The verdict SETS the type pair, exactly as Brio mutates its
    /// popup statics (:377-386): the checkboxes visibly flip, and every later
    /// read — the CMP gates, the options build, the next frame's draw — sees
    /// the routed state rather than a build that silently disagrees with what
    /// the menu shows.</para>
    ///
    /// <para>Then Brio's Dawntrail gate (:388-403): an Expression route needs
    /// BOTH a Dawntrail-capable actor and a pose that looks Dawntrail (the
    /// tongue bone, or a dawntrail/dt tag). Failing it clears only the
    /// Expression route — the import CONTINUES with whatever state remains,
    /// which is Brio's behaviour, not an abort.</para>
    ///
    /// <para>The Model-ID auto-appearance branch (:341-351) has no Poser
    /// equivalent — appearance is delegated to Glamourer.</para>
    /// </summary>
    /// <returns>A status line to show, or empty when nothing was blocked.</returns>
    private string SmartRoute(ISkeleton skeleton, PoseFile file)
    {
        if (PoseFileService.IsExpressionOnlyPose(file))
        {
            _typeExpression = true;
            _typeBody = false;
        }
        else if (PoseFileService.IsBodyOnlyPose(file))
        {
            _typeBody = true;
            _typeExpression = false;
        }

        if (!_typeExpression)
            return string.Empty;
        if (PoseFileService.IsDawntrailSkeleton(skeleton) &&
            PoseFileService.IsLikelyDawntrailPose(file))
            return string.Empty;

        _typeExpression = false;
        return "Smart import: expression skipped — this pose or this actor "
            + "is not Dawntrail-compatible.";
    }

    /// <summary>Brio's "From Clipboard" (FileUIHelpers.cs:574-595): the
    /// clipboard's pose through the SAME options the popup built, so the
    /// import type applies to it exactly as it does to a file.</summary>
    private void ImportFromClipboard()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        string text;
        try
        {
            text = ImGui.GetClipboardText();
        }
        catch (Exception ex)
        {
            _status = $"Clipboard: {ex.Message}";
            return;
        }
        if (PoseClipboard.Decode(text, out var reason) is not { } pose)
        {
            _status = $"Clipboard: {reason}";
            return;
        }
        // Smart Import's file classifier, same as the browse path — the
        // clipboard is just another source of a PoseFile.
        ImportLoadedPose(
            skeleton, pose, "Import pose from clipboard", "Clipboard");
    }

    /// <summary>Brio's "To Clipboard" (FileUIHelpers.cs:784-801). Armed like
    /// the file export — the capture waits for the pass that refreshes the raw
    /// caches it reads — and emits Brio's own compressed payload.</summary>
    private void CopyToClipboard()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        var armed = _poseFacade.CapturePoseFile(skeleton.Actor, pose =>
        {
            if (pose == null || PoseClipboard.Encode(pose) is not { } payload)
            {
                _status = "Clipboard: the pose could not be copied.";
                return;
            }
            try
            {
                ImGui.SetClipboardText(payload);
                _status = string.Empty;
            }
            catch (Exception ex)
            {
                _status = $"Clipboard: {ex.Message}";
            }
        });
        if (!armed.Success)
            _status = $"Clipboard: {armed.Detail}";
    }
}
