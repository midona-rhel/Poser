using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.Entities;
using Poser.Files;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Library;
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
    private readonly ITextureProvider _textures;
    private readonly IPoseLibraryService _library;

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
        Game.Preview.PosePreviewService preview,
        ITextureProvider textures,
        IPoseLibraryService library)
    {
        _poseFacade = poseFacade;
        _selection = selection;
        _config = config;
        _autoSave = autoSave;
        _preview = preview;
        _textures = textures;
        _library = library;
        _importPreview = new PosePreviewBinder(preview, poseFacade);
        _freeze = config.Config.FreezeActorOnPoseImport;

        // The import dialog's shape (user 2026-08-10): the three columns —
        // quick access, file list, live preview — on top, and the options in
        // one full-width three-column band UNDER them, above the footer.
        // Declared once — the dialog sizes itself around both, growing taller
        // by the band and by the extra body the preview column asks for.
        _importBrowser.ExtraHeight = ImportDialogExtraHeight;
        _importBrowser.BottomPanel =
            new FileSidePanel(_importBandHeight, DrawImportOptionsBand);
        _importBrowser.SidePanels.Add(
            new FileSidePanel(ImportPreviewColumnWidth, DrawImportPreviewPanel));
    }

    public void DrawBrowsers()
    {
        // Deferred dialog opens run HERE, at the root pump, before anything
        // else claims the frame — see <see cref="OpenBrowser"/>.
        if (_pendingBrowserOpen is { } pendingOpen)
        {
            _pendingBrowserOpen = null;
            pendingOpen();
        }
        // The library-export modal pumps at the root for the same reason the
        // dialogs defer (see OpenBrowser): its claim is made INSIDE
        // Crystarium.Modal on the first pump with the flag set — the menu
        // row only sets the flag — so the claim lands here, root-owned, one
        // frame after the dying menu's, and a root claim re-roots the whole
        // exclusive chain. Verified against ClaimExclusive: a claim with no
        // current owner truncates from index 0, so the closing menu's link
        // is gone before it could ever truncate the modal's. No separate
        // deferral slot needed.
        DrawExportLibraryModal();
        _importBrowser.Draw();
        _exportBrowser.Draw();
        DrawMenus();
        ReleaseImportPreview();
    }

    /// <summary>
    /// Every file-dialog open goes through this ONE-frame deferral. The
    /// import and export commands are invoked from inside popup bodies (the
    /// import menu's "From file", the export menu's rows), and
    /// <c>Interactive.ClaimExclusive</c> chains by the CURRENT owner: a
    /// window claimed from inside a popup nests UNDER the popup's link, and
    /// the popup's release on close — it closes the moment the new window
    /// takes focus — truncates the chain from its own link down, window
    /// included. The dialog then fails its ownership sync one frame after
    /// opening and closes itself ("exporting just dies", user 2026-08-10).
    /// Deferring the open to the top of <see cref="DrawBrowsers"/> claims at
    /// the shell root instead, so the dialog roots the chain and the dying
    /// popup's release no longer reaches it.
    /// </summary>
    private Action? _pendingBrowserOpen;

    private void OpenBrowser(Action open) => _pendingBrowserOpen = open;

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
    private ISkeleton? SelectedSkeleton() => SelectedSkeleton(out _);

    /// <summary>The overload that also names WHO, for the one caller that
    /// needs the identity and not just the skeleton (the library-export
    /// modal's nickname prefill). Null id on the host-fallback path — the
    /// push carries an actor, not a selection identity.</summary>
    private ISkeleton? SelectedSkeleton(out Domain.Identity.ActorId? actorId)
    {
        foreach (var id in _selection.Selected)
        {
            // A BONE selection names its owning actor just as well — the
            // actor-only lookup made every command dead while a bone was
            // selected, which is most of the time in the pose workspace.
            var candidate = id switch
            {
                { Kind: SceneEntityKind.Actor, Actor: { } selected } => selected,
                { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
                _ => (Domain.Identity.ActorId?)null,
            };
            if (candidate is { } resolvedId &&
                _resolveActor?.Invoke(resolvedId) is { HasSkeleton: true } actor)
            {
                actorId = resolvedId;
                return actor.Skeleton;
            }
        }
        actorId = null;
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

    /// <summary>The export menu's surface, logical px — Brio's export popup
    /// is a NARROW action menu (FileUIHelpers MenuWidth ≈ 245), nothing like
    /// the 320 option popup, so it gets its own constant rather than
    /// reusing <see cref="MenuWidth"/>.</summary>
    private const float ExportMenuWidth = 240f;
    private const float FilterMenuWidth = 216f;
    // 78: the longest label ("Reset first") plus breath — the slack the
    // old 96 left at the label side was exactly what the caption pairs
    // were missing at the right edge (user: almost overflowing).
    private const float MenuLabelColumn = 78f;

    /// <summary>The DENSE label column, for the import dialog's band alone:
    /// its labels are the short ones ("Type", "Model", "Reset first") and the
    /// band's whole complaint was empty width (user 2026-08-10), so the
    /// column shrinks to just past the longest of them.</summary>
    private const float DenseLabelColumn = 64f;

    /// <summary>The Options/Type rows' shared checkbox column pitch: wide
    /// enough for "Expression" (box + caption + gap), so Freeze tiles exactly
    /// over Body and Smart over Expression (user 2026-08-11).</summary>
    private const float CheckColumnPitch = 96f;
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

    /// <summary>
    /// Brio's export popup made literal (FileUIHelpers.cs:753-808): a NARROW
    /// action menu, one group of rows, no options and no preview. Brio's
    /// rows are Export / With Metadata… / separator / To Clipboard / To
    /// Stash; ours are the equivalent plus "To library" — a named export
    /// straight into a configured library source folder (user-requested).
    /// "With Metadata" is NOT ported — our PoseFile carries no appearance
    /// ids (ModelId/RaceSexId/FaceID); appearance is Glamourer's business.
    ///
    /// <para>Built at open rather than held static: the To library row's
    /// disabled state reads the CURRENT source list, and FloatingMenu
    /// freezes items at open anyway.</para>
    /// </summary>
    private ContextMenuItem[] BuildExportMenuItems()
    {
        bool noSources = ExportableSources().Count == 0;
        return
        [
            new("Export to file", TablerIcon.DeviceFloppy),
            new("To library", TablerIcon.Folder,
                disabled: noSources,
                help: noSources
                    ? "No library folders configured — add one in Settings"
                    : null),
            ContextMenuItem.Separator,
            new("To clipboard", TablerIcon.FileText),
            new("To stash", TablerIcon.Stack2),
        ];
    }

    /// <summary>The source folders a library export may land in: exactly the
    /// roots the library scans (enabled, with a path), in their configured
    /// order — the dropdown labels them the way the folder rail labels its
    /// roots (the source's own name).</summary>
    private List<LibrarySourceConfig> ExportableSources()
    {
        var sources = new List<LibrarySourceConfig>();
        foreach (var source in _config.Config.Library.Sources)
        {
            if (source.Enabled && !string.IsNullOrWhiteSpace(source.Path))
                sources.Add(source);
        }
        return sources;
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
            Crystarium.FloatingMenu.Open(
                ExportMenuId, _menuAnchor, BuildExportMenuItems(),
                ExportMenuWidth);
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

        // The export menu's pump and dispatch. The click lands AFTER the
        // menu's own draw has ended its owner, so a command that opens the
        // save dialog claims from the ROOT — which, with the deferred
        // browser open, is what keeps the dialog out of the dying menu's
        // exclusive-chain release.
        int exportClicked = Crystarium.FloatingMenu.Draw(ExportMenuId);
        switch (exportClicked)
        {
            case 0:
                if (SelectedSkeleton() is { } exportSkeleton)
                    OpenExport(exportSkeleton);
                else
                    _status = "Select an actor first.";
                break;
            case 1:
                OpenExportToLibrary();
                break;
            case 3:
                CopyToClipboard();
                break;
            case 4:
                StashPose();
                break;
        }
    }

    /// <summary>Self-measured popup heights (unscaled): the section stack
    /// reports its real height as it draws — plus the page inset Complete()
    /// extends the cursor extent by, so the window never scrolls — and the
    /// next frame's popup fits exactly. Per variant: the presets section
    /// changes the actor-side height.</summary>
    private float _importMenuHeightPlain = 430f;
    private float _importMenuHeightPresets = 480f;
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

    // ── the import dialog's two panels ───────────────────────────────────
    // The user's design: pick a file, see exactly what the options standing
    // right now would make of it, confirm. The options run in a full-width
    // three-column band UNDER the columns region and the preview stands right
    // of the listing; the dialog's own Load button is the import, so neither
    // panel carries an action that leaves it.

    /// <summary>Extra body the import dialog asks for over the theme's
    /// default, logical px — the preview column wants a taller render and the
    /// file list gains the same rows (user round: "it should be taller as
    /// well"). The export dialog states nothing and keeps the theme height.
    /// </summary>
    private const float ImportDialogExtraHeight = 100f;

    /// <summary>The preview column, logical px: the width at which Ktisis'
    /// portrait aspect fills the dialog's body height exactly. The grown body
    /// is 308 + 100; less two page insets and the camera band that leaves 354
    /// for the image, and 354 by the 192:320 aspect is 212 — plus the two
    /// insets, 236. Wider only pads the column: the block narrows the render
    /// to hold the aspect rather than stretch it.</summary>
    private const float ImportPreviewColumnWidth = 236f;

    /// <summary>The options band may not grow past this, logical px — the
    /// columns region above it keeps its floor; past the cap each column
    /// scrolls inside its own box.</summary>
    private const float ImportBandMaxHeight = 200f;

    /// <summary>The band's logical height: the tallest option column as last
    /// measured, plus the band's two vertical insets, capped. Seeded with the
    /// DENSE column arithmetic (two checklist rows at 26 + the two insets —
    /// the columns are headerless, user 2026-08-10) and corrected by the
    /// first draw — the popup stack's own self-measure idiom, so every open
    /// after the first fits exactly.</summary>
    private float _importBandHeight = 78f;

    /// <summary>Shown in the empty well before any file is highlighted.
    /// </summary>
    private const string ImportPreviewIdleText = "Pick a pose file to preview.";

    /// <summary>Shown while the binder is capturing the target's own pose —
    /// the stance every previewed file is shown landing ON. Nothing is stated
    /// until it lands, so the well says why.</summary>
    private const string ImportPreviewRebaseText = "Reading the actor's pose…";

    private const string ImportOptionsBandId = "##import-dialog-options";

    /// <summary>The actor the OPEN dialog's import will land on, captured with
    /// the dialog exactly as the confirm callback captures its skeleton: the
    /// preview borrows the appearance of the body the file is going to.
    /// </summary>
    private IActor? _importTarget;

    /// <summary>Whether the dialog was driving the shared preview last frame —
    /// the edge <see cref="ReleaseImportPreview"/> hands the seat back on.
    /// </summary>
    private bool _importPreviewOwned;

    /// <summary>Whether a pose has been stated for THIS dialog session.
    /// The service is deliberately not closed between sessions (the seat
    /// handoff), so on a fresh open its texture still shows the LAST
    /// session's body — until this is set, the dialog's preview box shows
    /// the characterbg backing alone and the render is held at alpha 0
    /// regardless of the texture handle (user 2026-08-10: "still wearing
    /// the last pose"). Reset when the dialog opens; set by the first
    /// <see cref="SyncImportPreview"/> that states a pose.</summary>
    private bool _importPreviewPosed;

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
            _importPreview.IsWaitingForBaseline
                ? ImportPreviewRebaseText
                : ImportPreviewIdleText,
            // Until THIS session has stated a pose, the box is backing and
            // status alone: the service still holds the last session's
            // render and showing it would dress the preview in a stale body.
            showRender: _importPreviewPosed);
    }

    /// <summary>
    /// The dialog's OPTIONS band, full width between the columns region and
    /// the footer: the same option sections the import menu stacks, laid out
    /// in THREE COLUMNS — options/type, transform, scope — one group per
    /// column, minus every action: the dialog's own Load button is its import.
    /// Three equal column regions tile the band past the left inset; each
    /// region's scroll gutter is its own trailing inset (the shell contract),
    /// so the rhythm reads inset, content, gutter, content, gutter, content,
    /// gutter — no second margin anywhere. The columns normally fit whole;
    /// each scrolls inside its own box only when the cap bites.
    /// </summary>
    private void DrawImportOptionsBand(
        Vector2 origin, Vector2 size, string? highlighted)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset;
        float regionWidth = (size.X / scale - inset) / 3f;
        float regionHeight = size.Y / scale - inset;
        float tallest = 0f;
        for (int column = 0; column < 3; column++)
        {
            int mount = column;
            ImGui.SetCursorScreenPos(new Vector2(
                origin.X + (inset + regionWidth * column) * scale,
                origin.Y + inset * scale));
            Crystarium.ScrollRegion(
                $"{ImportOptionsBandId}-{column}",
                regionWidth,
                regionHeight,
                region =>
                {
                    var top = ImGui.GetCursorScreenPos();
                    float width = region.ContentWidth * scale;
                    float height = mount switch
                    {
                        0 => DrawImportTypeSection(
                            top, width, divider: false, dense: true),
                        1 => DrawTransformSection(
                            top, width, divider: false, dense: true),
                        _ => DrawScopeSection(
                            top, width, divider: false, dense: true),
                    };
                    ImGui.SetCursorScreenPos(new Vector2(top.X, top.Y + height));
                    ImGui.Dummy(new Vector2(1f, 1f));
                    tallest = MathF.Max(tallest, height / scale);
                });
        }

        // The popup stack's self-measure idiom: the band as REGISTERED fits
        // the tallest column exactly from the next frame on, and the next
        // open sizes the window around it.
        float fitted = MathF.Min(ImportBandMaxHeight, tallest + inset * 2f);
        if (MathF.Abs(fitted - _importBandHeight) > 0.5f)
        {
            _importBandHeight = fitted;
            _importBrowser.BottomPanel =
                new FileSidePanel(fitted, DrawImportOptionsBand);
        }
        DrawNestedBoneFilter();

        // The band draws AFTER the preview column, so without this a toggle
        // would reach the binder only on the NEXT frame's sync — and the
        // contract is that an option change re-poses within the frame it was
        // made. Restating here costs one compare per frame; the binder's own
        // dedupe keeps an unchanged frame free.
        SyncImportPreview(highlighted);
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
    /// <para>The options travel VERBATIM apart from the two the preview may
    /// never honor (see <see cref="PosePreviewBinder.Trim"/>): "Reset first"
    /// included, because the binder stands the preview body in the target's own
    /// pose first and a layering import must be seen layering.</para>
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
        var candidate = PosePreviewBinder.Trim(built ?? BuildOptions());
        if (_importPreview.Begin(source, highlighted, candidate))
        {
            // The SENT build is its own instance, per the binder's contract:
            // the compare candidate must never alias what the import engine
            // holds across ticks (the library rail's exact shape).
            _importPreview.Pose(
                highlighted,
                PosePreviewBinder.Trim(
                    CmpImportOverride(highlighted, out _, out _)
                        ?? BuildOptions()));
            // A pose has been stated THIS dialog session: the render may
            // fade in over the backing from here on (see
            // DrawImportPreviewPanel).
            _importPreviewPosed = true;
        }
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
    /// <paramref name="idleText"/> is what the empty well says while the seat
    /// is up with no pose stated — the pane's reason ("select a pose"), so the
    /// well is an affordance rather than a mystery box.</summary>
    public void SetPreviewVisible(bool visible, string? idleText = null)
    {
        _previewVisible = visible;
        _previewIdleText = idleText;
    }

    private bool _previewVisible;
    private string? _previewIdleText;

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

    /// <summary>Ktisis PreviewNode's backing layer (ImageBacking →
    /// bgpart->LoadTexture("ui/common/characterbg_hr1.tex")): the
    /// character-card backdrop that stands UNDER the render, always.</summary>
    private const string PreviewBackingPath = "ui/common/characterbg_hr1.tex";

    /// <summary>The backing's SHARED texture, resolved once — the WRAP is
    /// re-resolved every frame, exactly as the gobo and icon resolvers do:
    /// shared textures must never have their wraps cached. A throwing load is
    /// remembered and the box falls back to the plain well fill.</summary>
    private ISharedImmediateTexture? _previewBacking;
    private bool _previewBackingFailed;

    /// <summary>The render's fade ramp, raw 0..1 progress toward "a render
    /// exists", eased through the Picto default curve at draw time.
    /// Constant-rate, so a swap that reverses mid-flight retraces exactly the
    /// distance it covered — the Motion store's own ramp model. ONE ramp per
    /// MOUNT: the rail and the import dialog can now draw the same frame
    /// (the rail mirrors the dialog's preview), and a shared ramp would
    /// double-advance — or, with the mounts' targets split across the
    /// fresh-open backing state, fight to a half-faded standstill.</summary>
    private float _previewFadeRamp;

    /// <summary>The import dialog panel's own ramp — reset at dialog open so
    /// the fresh session starts from the backing (see
    /// <see cref="_importPreviewPosed"/>).</summary>
    private float _dialogFadeRamp;

    /// <summary>The backing's ImGui handle for this frame, or 0 — missing
    /// texture keeps the current well-fill behavior.</summary>
    private nint ResolvePreviewBacking()
    {
        if (_previewBackingFailed)
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            _previewBacking ??= _textures.GetFromGame(PreviewBackingPath);
            wrap = _previewBacking.GetWrapOrDefault();
        }
        catch (Exception)
        {
            _previewBackingFailed = true;
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }

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

        // The rule is a divider BETWEEN sections: the first one leads the
        // stack only when the preview does not.
        y += DrawImportTypeSection(
            new Vector2(origin.X, y), width, divider: preview);
        y += DrawTransformSection(
            new Vector2(origin.X, y), width, divider: true);
        y += DrawScopeSection(
            new Vector2(origin.X, y), width, divider: true);

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

    // ── the three option groups, one Section each ────────────────────────
    // Shared verbatim by every mount: the popup and the library rail stack
    // them; the import dialog's band seats one per column. Splitting at the
    // section boundary is what lets the same content stand in either shape.

    /// <summary>The Options/Type group. Returns the section's height, px.
    /// </summary>
    /// <param name="dense">The import dialog's BAND form: checklist row
    /// pitch, no pre-header padding, the "Options" label dropped — "Freeze /
    /// Smart" speak for themselves — and NO column header at all (user
    /// 2026-08-10: the label rows alone carry it). An empty Section title is
    /// the pure-row-container mount, so the three headerless columns keep
    /// their top edges aligned. The popup and rail mounts keep the ordinary
    /// form.</param>
    private float DrawImportTypeSection(
        Vector2 origin, float width, bool divider, bool dense = false) =>
        Crystarium.Section(
            "##import-menu-head", dense ? string.Empty : "Import pose",
            origin, width, true, null,
            form =>
            {
                // The row label stays in BOTH mounts (user 2026-08-11:
                // headers go, labels stay), and the two rows share one
                // column pitch so Freeze sits exactly over Body and Smart
                // over Expression (same user round).
                form.Checkboxes(
                    "Options",
                    disabled: false,
                    fullWidth: false,
                    CheckColumnPitch,
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
                    disabled: false,
                    fullWidth: false,
                    CheckColumnPitch,
                    ("Body", _typeBody,
                        next => _typeBody = next,
                        "Import the body. With Expression too, everything "
                        + "imports with every component"),
                    ("Expression", _typeExpression,
                        next => _typeExpression = next,
                        "Import the face as an expression — always every "
                        + "component"));
            },
            divider: divider,
            labelColumnWidth: dense ? DenseLabelColumn : MenuLabelColumn,
            dense: dense);

    /// <summary>The Transform group. Returns the section's height, px.
    /// </summary>
    /// <param name="dense">The band form: headerless (the trio says it all)
    /// and the "Apply" label row dropped — the column is two tight rows, the
    /// trio and Model.</param>
    private float DrawTransformSection(
        Vector2 origin, float width, bool divider, bool dense = false) =>
        Crystarium.Section(
            "##import-menu-transform", dense ? string.Empty : "Transform",
            origin, width, true, null,
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
                // The POPUP keeps the trio on its own full-width row under
                // a label row (user placement, e05915c); the BAND labels
                // the row inline (user 2026-08-11: labels stay).
                if (!dense)
                    form.Label("Apply");
                form.Checkboxes(
                    dense ? "Apply" : string.Empty,
                    locked,
                    fullWidth: !dense,
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
            divider: divider,
            labelColumnWidth: dense ? DenseLabelColumn : MenuLabelColumn,
            dense: dense);

    /// <summary>The Scope group — Reset first, then the bone filter. Returns
    /// the section's height, px.</summary>
    /// <param name="dense">The band form: headerless, checklist pitch, the
    /// "Filter" label dropped — the button already says Bone filter — and
    /// the button flush to the column's content right edge (user
    /// 2026-08-10), which IS the gutter contract's trailing inset.</param>
    private float DrawScopeSection(
        Vector2 origin, float width, bool divider, bool dense = false) =>
        Crystarium.Section(
            "##import-menu-scope", dense ? string.Empty : "Scope",
            origin, width, true, null,
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
                form.Actions(dense ? string.Empty : "Filter",
                    actions => actions.Button(
                        "Bone filter", () => RequestBoneFilterMenu(),
                        disabled: typed,
                        help: typed
                            ? "The bone filter shapes the default import; "
                                + "uncheck Body and Expression to edit it"
                            : "Choose which bone categories imports may touch"),
                    alignRight: dense,
                    fullWidth: dense);
            },
            divider: divider,
            labelColumnWidth: dense ? DenseLabelColumn : MenuLabelColumn,
            dense: dense);

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

        // While the import dialog drives the shared service, this rail block
        // is a read-only MIRROR of it (user 2026-08-10: the rail preview
        // vanished for the whole dialog session): same texture, same camera,
        // and the DIALOG's states — its idle/rebase texts and its fresh-open
        // backing hold — so the rail never shows the stale render the dialog
        // itself is hiding.
        // While the import dialog drives, the rail box shows the STATIC
        // backing and nothing else — not a live mirror (user 2026-08-11:
        // "it shouldn't be a live preview, feels very overcomplicated").
        // Same box, no reflow; the live render lives in the dialog alone.
        bool mirror = IsImportPreviewActive;
        form.Canvas("preview-image", box.Y / scale,
            (min, size) => DrawPreviewImage(
                min, size, box.X, scale, theme,
                ref _previewFadeRamp,
                emptyText: mirror ? null : _previewIdleText,
                showRender: !mirror));
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
        Vector2 origin, Vector2 size, string? emptyText,
        bool showRender = true)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        int rows = PreviewCameraRows(size.X, scale, theme);
        float camera = PreviewCameraHeight(rows, theme) * scale;
        var box = PreviewBox(size.X, MathF.Max(0f, size.Y - camera));
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        DrawPreviewImage(
            origin, new Vector2(size.X, box.Y), box.X, scale, theme,
            ref _dialogFadeRamp, emptyText, showRender);
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
    /// <param name="showRender">False forces the render's fade target to 0
    /// whatever the texture handle says — the dialog's fresh-open state,
    /// where the service still renders the LAST session's pose and only the
    /// backing may show. The rail always passes true.</param>
    private void DrawPreviewImage(
        Vector2 min, Vector2 size, float boxWidth, float scale, Theme theme,
        ref float fadeRamp,
        string? emptyText = null, bool showRender = true)
    {
        var boxMin = theme.Optical.Snap(
            min + new Vector2((size.X - boxWidth) * 0.5f, 0f));
        var boxSize = new Vector2(boxWidth, size.Y);
        var boxMax = boxMin + boxSize;
        var draw = ImGui.GetWindowDrawList();
        float radius = theme.Radii.Control * scale;

        // Ktisis PreviewNode's layering: the character-card backdrop ALWAYS
        // paints the box, full-uv into the same rect the render takes, and
        // the render fades in over it — the swap between the empty box and a
        // live character is a fade, never a pop. The box is the same size in
        // every state, so nothing reflows across the swap.
        var handle = _preview.TextureHandle;
        if (!showRender)
            handle = 0;
        fadeRamp = Math.Clamp(
            fadeRamp
                + (handle != 0 ? 1f : -1f) * ImGui.GetIO().DeltaTime
                    / Transition.PictoDefault.DurationSeconds,
            0f, 1f);
        float fade = Transition.PictoDefault.Evaluate(fadeRamp);

        nint backing = ResolvePreviewBacking();
        if (backing != 0)
            draw.AddImageRounded(
                new ImTextureID(backing),
                boxMin,
                boxMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Vector4.One)),
                radius);
        else
            draw.AddRectFilled(
                boxMin, boxMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Chrome.InputWell)),
                radius);

        if (handle != 0)
        {
            // Ktisis PreviewNode: the WHOLE render, uv 0..1, into the node —
            // no aspect fit, so no well shows beside it.
            if (fade > 0f)
                draw.AddImage(
                    new ImTextureID(handle),
                    boxMin,
                    boxMax,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(new Vector4(1f, 1f, 1f, fade))));
            DrawPreviewInput(boxMin, boxSize);
        }
        else
        {
            // No render: the backing carries the reason, centred over it.
            // The service's own wins; "preparing" is only what the frames
            // before it has one say.
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

    private void ApplyRestPreset(RestPose pose)
    {
        if (SelectedSkeleton() is { } skeleton)
        {
            NotePoseApplied();
            _status = _poseFacade.ApplyRestPose(skeleton.Actor, pose) is
                { Success: false } failed
                ? $"Preset: {failed.Detail}"
                : string.Empty;
        }
        else
            _status = "Select an actor first.";
    }

    /// <summary>
    /// Every import THIS section dispatches passes through here first: the
    /// actor it lands on is the one a live preview rebases onto, so the
    /// captured stance is now stale. The dialog's own binder is invalidated
    /// directly; the library rail runs a binder of its own and watches
    /// <see cref="TargetPoseRevision"/> for the same edge — a pull, so neither
    /// surface has to know the other is up.
    /// </summary>
    private void NotePoseApplied()
    {
        TargetPoseRevision++;
        _importPreview.InvalidateBaseline();
    }

    /// <summary>Bumped whenever these menus have posed an actor. A preview
    /// drive compares it against what it last saw and re-captures its
    /// baseline when it moved.</summary>
    public int TargetPoseRevision { get; private set; }

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
        // A fresh session: nothing has been stated yet, so the preview box
        // shows the backing until a highlight poses something — and the
        // DIALOG's fade ramp starts from zero rather than fading the stale
        // render OUT. The rail's own ramp is left alone: its mirror target
        // flips to the backing too and it fades there from wherever it was.
        _importPreviewPosed = false;
        _dialogFadeRamp = 0f;
        OpenBrowser(() => _importBrowser.Open(initialPath, path =>
        {
            if (rememberPath)
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            ImportFromPath(skeleton, path);
        }));
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

        NotePoseApplied();
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

        NotePoseApplied();
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
        OpenBrowser(() => _exportBrowser.Open(_lastPath, path =>
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
        }));
    }

    // ── Export to library ────────────────────────────────────────────────
    // The export menu's "To library" row: a small GlassModal (the rename
    // modal's idiom) asking for a NAME and a LOCATION among the configured
    // library sources, then the ordinary armed export into
    // <source>\<name>.pose and a library rescan so the tile appears.

    private bool _libraryExportOpen;
    private ISkeleton? _libraryExportSkeleton;
    private string _libraryExportName = string.Empty;
    private int _libraryExportSource;
    private List<LibrarySourceConfig> _libraryExportSources = [];
    private string[] _libraryExportLabels = [];

    /// <summary>The last existence-checked candidate path and its verdict —
    /// one File.Exists per name/location CHANGE, not per frame.</summary>
    private string _libraryExportCandidate = string.Empty;
    private bool _libraryExportTaken;

    /// <summary>The rename modal's own name-cleaning: the raw scene name
    /// carries an object-index suffix ("Name (203)") that no file should.
    /// </summary>
    private static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(
            name, @"\s*\(\d+\)$", "");

    /// <summary>Strips every character Windows refuses in a file NAME —
    /// typed or pasted, the input simply never holds one.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return name.IndexOfAny(invalid) < 0
            ? name
            : new string(name.Where(c => Array.IndexOf(invalid, c) < 0)
                .ToArray());
    }

    /// <summary>The menu row's dispatch: freeze the target and the source
    /// list, prefill the name the way the rename modal does (nickname first,
    /// cleaned scene name otherwise), preselect the remembered location, and
    /// raise the flag — the modal itself opens at the root pump.</summary>
    private void OpenExportToLibrary()
    {
        if (SelectedSkeleton(out var actorId) is not { } skeleton)
        {
            _status = "Select an actor first.";
            return;
        }
        var sources = ExportableSources();
        if (sources.Count == 0)
            return; // The row draws disabled; this is belt and braces.

        _libraryExportSkeleton = skeleton;
        _libraryExportSources = sources;
        _libraryExportLabels = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            // The folder rail's root labeling: the source's own name, with
            // the settings surface's fallback for a blank one.
            _libraryExportLabels[i] = string.IsNullOrWhiteSpace(sources[i].Name)
                ? $"Source {i + 1}"
                : sources[i].Name;
        }

        // Last-used location by PATH — stable across source-list edits.
        _libraryExportSource = 0;
        string last = _config.Config.Library.LastExportSourcePath;
        for (int i = 0; i < sources.Count; i++)
        {
            if (string.Equals(
                    sources[i].Path, last, StringComparison.OrdinalIgnoreCase))
            {
                _libraryExportSource = i;
                break;
            }
        }

        _libraryExportName = SanitizeFileName(
            (actorId is { } id ? _config.GetNickname(id.LogicalId) : null)
                ?? DisplayName(skeleton.Actor.Name)).Trim();
        _libraryExportCandidate = string.Empty;
        _libraryExportTaken = false;
        _libraryExportOpen = true;
    }

    /// <summary>The modal, pumped from <see cref="DrawBrowsers"/> every
    /// frame: name input, location dropdown (a static row when only one
    /// source exists), inline validation, and the equal-width Export/Cancel
    /// pair. Export disables — never silently overwrites, never
    /// auto-suffixes — while the name is empty or already taken there.
    /// </summary>
    private void DrawExportLibraryModal()
    {
        if (!_libraryExportOpen || _libraryExportSkeleton is not { } skeleton)
            return;
        Crystarium.Modal(
            "##export-to-library",
            _libraryExportOpen,
            next => _libraryExportOpen = next,
            "Export to library",
            // Fitted, not the Small preset's default: the preset left the
            // body ~half empty below the buttons (user 2026-08-11). Title
            // bar + padded body + the four rows incl. the always-reserved
            // problem line.
            height: 260f,
            body: () =>
        {
            float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance =
                (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Name", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-export-name", _libraryExportName,
                next => _libraryExportName = SanitizeFileName(next),
                placeholder: "Pose name");
            ImGui.Dummy(new Vector2(0f, rowGap));

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Location", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            var sources = _libraryExportSources;
            int selected = Math.Clamp(
                _libraryExportSource, 0, sources.Count - 1);
            if (sources.Count > 1)
            {
                Crystarium.Dropdown(
                    "##library-export-location", _libraryExportLabels,
                    selected, next => _libraryExportSource = next);
                ImGui.Dummy(new Vector2(0f, rowGap));
            }
            else
            {
                // One source: a static row, not a one-item dropdown.
                Crystarium.TextAt(
                    ImGui.GetCursorScreenPos(),
                    _libraryExportLabels[selected],
                    new TextStyle
                    {
                        Size = theme.Typography.BodySize,
                        Color = theme.Text,
                    });
                ImGui.Dummy(new Vector2(
                    1f, (theme.Typography.BodySize + 6f) * scale + rowGap));
            }

            // Inline, honest validation: required name, and no silent
            // overwrite — an existing <folder>\<name>.pose disables Export
            // with the reason on the row, not in a tooltip alone.
            string trimmed = _libraryExportName.Trim();
            string candidate = trimmed.Length == 0
                ? string.Empty
                : System.IO.Path.Combine(
                    sources[selected].Path, trimmed + ".pose");
            if (!string.Equals(
                    candidate, _libraryExportCandidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                _libraryExportCandidate = candidate;
                _libraryExportTaken = candidate.Length > 0
                    && System.IO.File.Exists(candidate);
            }
            string? problem = trimmed.Length == 0
                ? "A name is required."
                : _libraryExportTaken
                    ? "That name already exists here."
                    : null;
            if (problem is not null)
            {
                Crystarium.TextAt(
                    ImGui.GetCursorScreenPos(), problem, captionStyle);
                ImGui.Dummy(new Vector2(1f, captionAdvance));
            }
            ImGui.Dummy(new Vector2(0f, rowGap));

            // The equal-width action pair (the shell rule): two buttons
            // splitting the row, gap between, primary leading.
            float gap = theme.Page.ActionGap * scale;
            float half =
                (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    "Export",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    disabled: problem is not null,
                    help: problem,
                    id: "library-export-confirm"))
            {
                ConfirmExportToLibrary(skeleton, sources[selected], trimmed);
                _libraryExportOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-export-cancel"))
                _libraryExportOpen = false;
        });
    }

    /// <summary>The confirm: remember the location, arm the SAME export the
    /// file row runs (<see cref="CleanPoseFacade.ExportPose"/> self-marshals
    /// and waits for the cache-refresh pass), and on the write landing, kick
    /// a library rescan so the tile appears without a manual refresh. The
    /// modal is already closed; the result lands in <see cref="_status"/> —
    /// success clears, failure explains.</summary>
    private void ConfirmExportToLibrary(
        ISkeleton skeleton,
        LibrarySourceConfig source,
        string name)
    {
        _config.Config.Library.LastExportSourcePath = source.Path;
        _config.Save();

        string path = System.IO.Path.Combine(source.Path, name + ".pose");
        var armed = _poseFacade.ExportPose(skeleton.Actor, path, exported =>
        {
            if (exported)
            {
                _status = string.Empty;
                _library.RequestScan();
            }
            else
                _status = "Library: the pose file could not be written.";
        });
        if (!armed.Success)
            _status = $"Library: {armed.Detail}";
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
