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
using Poser.Application.Operations;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Library;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.UI;

public sealed class PoseFileInspectorSection
{
    private const string NoActorText = "Select an actor first.";

    // Neither type checked uses the default route and category filter; Body and
    // Expression use their typed routes; both uses the full route.
    private bool _typeBody;
    private bool _typeExpression;

    private readonly CleanPoseFacade _poseFacade;
    private readonly SelectionSession _selection;
    private readonly Config.ConfigurationService _config;
    private readonly IAutoSaveService _autoSave;
    private readonly Game.Preview.PosePreviewService _preview;
    private readonly ITextureProvider _textures;
    private readonly IPoseLibraryService _library;

    private readonly PosePreviewBinder _importPreview;

    private readonly UserNotices _notices;
    private readonly Crystarium.FileDialog _importBrowser =
        new("Import Pose", new[] { ".pose", ".cmp" }, isSaveMode: false);
    private readonly Crystarium.FileDialog _exportBrowser =
        new("Export Pose", new[] { ".pose" }, isSaveMode: true);
    private string _lastPath;
    // Rotation is enabled by default; position and scale are opt-in.
    private bool _rotation = true, _position, _scale;
    private bool _reset;
    // Selected scope is dialog-only; confirmation freezes exact BoneIds. An
    // empty or stale frozen set refuses.
    private bool _selectiveImport;
    private bool _selectiveDescendants;
    // Anchor follows the effective position component.
    private bool _selectiveAnchor;
    // Ear exclusion applies on every supported route.
    private bool _excludeEars;
    // Apply-on-select is path-guarded on every supported route.
    private bool _applyOnSelect;
    private string? _appliedOnSelectPath;
    // Reference preset requires two presses: first shows warning, second
    // applies; reopening or another preset clears the arm.
    private bool _referenceArmed;

    private bool _smartImport = true;
    private bool _modelTransform;
    private readonly HashSet<string> _disabledCategories =
        new(StringComparer.Ordinal) { "weapon", "ex" };
    private bool _importMenuRequested;
    private bool _importMenuWithPresets;
    private bool _boneFilterRequested;

    // Freeze mirrors the persisted configuration.
    private bool _freeze;

    // The last import stores one source; reapply uses current options.
    private string? _lastImportPath;
    private PoseFile? _lastImportPose;

    // This stash holds a full PoseFile; the facade stash holds a PortablePose.
    private PoseFile? _poseStash;
    private DateTimeOffset? _poseStashedAt;

    private bool HasLastImport => _lastImportPath != null || _lastImportPose != null;

    public event Action? OnLibraryRequested;

    public PoseFileInspectorSection(
        CleanPoseFacade poseFacade,
        SelectionSession selection,
        Config.ConfigurationService config,
        IAutoSaveService autoSave,
        Game.Preview.PosePreviewService preview,
        ITextureProvider textures,
        IPoseLibraryService library,
        UserNotices notices)
    {
        _notices = notices;
        _poseFacade = poseFacade;
        _selection = selection;
        _config = config;
        _autoSave = autoSave;
        _preview = preview;
        _textures = textures;
        _library = library;
        _importPreview = new PosePreviewBinder(preview, poseFacade);
        _freeze = config.Config.FreezeActorOnPoseImport;
        _lastPath = config.Config.Library.EnsurePoseRootExists();

        _importBrowser.WidthAdjustment = -80f;
        _importBrowser.HeightAdjustment = -12f;
        ConfigureImportBand();
        _importBrowser.BeforeFrame = RefreshImportBand;
        _importBrowser.PersistentRightPanel =
            new FileSidePanel(
                ImportPreviewImageWidth + Crystarium.ActiveTheme.Page.Inset * 2f,
                DrawImportPreviewPanel);
        _importBrowser.FooterBeforeCancel = DrawImportFooterFilter;
    }

    private Action<OperationReceipt> TrackImport(ActorId expectedActor)
    {
        Guid? operation = null;
        return receipt =>
        {
            if (receipt.TargetActorId != expectedActor)
                return;
            if (receipt.State == OperationReceiptState.Pending)
            {
                operation = receipt.OperationId;
                return;
            }
            if (operation != receipt.OperationId)
                return;
            operation = null;
            if (receipt.State is not OperationReceiptState.Applied)
                _notices.Failed(
                    $"Import: {receipt.Detail ?? receipt.State.ToString()}.");
        };
    }

    public void DrawBrowsers()
    {
        // Browser opens are deferred to the root pump so popup teardown cannot
        // remove the dialog's exclusive claim.
        if (_pendingBrowserOpen is { } pendingOpen)
        {
            _pendingBrowserOpen = null;
            pendingOpen();
        }
        DrawExportLibraryModal();
        _importBrowser.Draw();
        _exportBrowser.Draw();
        DrawMenus();
        ReleaseImportPreview();
    }

    // Browser opens are deferred to the root pump so popup teardown cannot
    // remove the dialog's exclusive claim.
    private Action? _pendingBrowserOpen;

    private void OpenBrowser(Action open) => _pendingBrowserOpen = open;

    public void RequestImportMenu(bool withPresets, Vector2? anchor = null)
    {
        _importMenuWithPresets = withPresets;
        _menuAnchor = anchor ?? ImGui.GetMousePos();
        _importMenuRequested = true;
    }

    /// <summary>The library's OWN options menu — the same standing
    /// settings the import flow reads (one state, retained), but options
    /// only: none of the import dialog's actions belong in the library.
    /// It opens to the LEFT of the seat so the preview stays visible
    /// while the options are worked.</summary>
    public void RequestLibraryOptionsMenu(Vector2 seat)
    {
        _libraryMenuSeat = seat;
        _libraryMenuRequested = true;
    }

    private Vector2 _libraryMenuSeat;
    private bool _libraryMenuRequested;
    private float _libraryMenuHeight = 400f;
    private const string LibraryOptionsMenuId = "##library-options-menu";

    public void RequestBoneFilterMenu()
    {
        _filterAnchor = ImGui.GetMousePos();
        _boneFilterRequested = true;
    }

    private Vector2 _filterAnchor;

    public PoseImportOptions ApplyCategoryFilter(PoseImportOptions options) =>
        Files.ImportBoneCategories.ApplyDisabledCategories(
            options, _disabledCategories);

    // Resolve actor selection (actor or bone) first; use the live host target
    // when the library mount has no scene actor.
    private ISkeleton? SelectedSkeleton() => SelectedSkeleton(out _);

    private ISkeleton? SelectedSkeleton(out Domain.Identity.ActorId? actorId)
    {
        foreach (var id in _selection.Selected)
        {
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

    private bool InLibrary => HostPushLive && _hostIsLibrary;

    public Func<Domain.Identity.ActorId, IActor?>? _resolveActor;

    private const float MenuPadding = 8f;

    private static float MenuTitleOffset(float scale)
    {
        var page = Crystarium.ActiveTheme.Page;
        return (page.SectionPaddingTop
            + (page.SectionHeaderHeight
                - Crystarium.ActiveTheme.Typography.LabelSize) * 0.5f)
            * 0.5f * scale;
    }
    private const float MenuWidth = 320f;

    private const float ExportMenuWidth = 240f;
    private const float FilterMenuWidth = 216f;
    private const float MenuLabelColumn = 78f;
    private const float DenseLabelColumn = 64f;
    private const float ImportOptionLabelColumn = 64f;

    private const string ImportMenuId = "##pose-import-menu";
    private const string ExportMenuId = "##pose-export-menu";
    private const string BoneFilterMenuId = "##pose-bone-filter-menu";
    private Vector2 _menuAnchor;
    private bool _exportMenuRequested;

    public void RequestExportMenu()
    {
        _menuAnchor = ImGui.GetMousePos();
        _exportMenuRequested = true;
    }

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
            _referenceArmed = false;
            Crystarium.OpenPopover(ImportMenuId);
        }
        if (_exportMenuRequested)
        {
            _exportMenuRequested = false;
            Crystarium.FloatingMenu.Open(
                ExportMenuId, _menuAnchor, BuildExportMenuItems(),
                ExportMenuWidth);
        }
        if (_libraryMenuRequested)
        {
            _libraryMenuRequested = false;
            Crystarium.OpenPopover(LibraryOptionsMenuId);
        }
        {
            float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            var anchor = _libraryMenuSeat - new Vector2(
                (MenuWidth + MenuPadding) * scale, 0f);
            Crystarium.FloatingSurface.Popup(
                LibraryOptionsMenuId,
                new FloatingSurfaceProps
                {
                    Width = MenuWidth,
                    Height = _libraryMenuHeight,
                    Padding = MenuPadding,
                    AnchorMin = anchor,
                    AnchorMax = anchor,
                    Treatment = FloatingSurfaceTreatment.Glass,
                },
                DrawLibraryOptionsMenuBody);
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

        if (!_importBrowser.IsOpen)
        {
            if (_boneFilterRequested && !ImGui.IsPopupOpen(ImportMenuId))
            {
                _boneFilterRequested = false;
                Crystarium.OpenPopover(BoneFilterMenuId);
            }
            DrawBoneFilterMenu(_filterAnchor);
        }

        int exportClicked = Crystarium.FloatingMenu.Draw(ExportMenuId);
        switch (exportClicked)
        {
            case 0:
                if (SelectedSkeleton() is { } exportSkeleton)
                    OpenExport(exportSkeleton);
                else
                    _notices.Refused(NoActorText);
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

    private float _importMenuHeightPlain = 430f;
    private float _importMenuHeightPresets = 480f;
    private float _boneFilterHeight = 520f;

    /// <summary>Options only, left-aligned in its own surface — the
    /// import menu's actions stay in the import menu.</summary>
    private void DrawLibraryOptionsMenuBody()
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float top = origin.Y - MenuTitleOffset(scale);

        float y = DrawOptionsSections(
            new Vector2(origin.X, top), width,
            withPresets: false, withActions: false);

        _libraryMenuHeight = (y - origin.Y) / scale
            + Crystarium.ActiveTheme.Page.Inset + MenuPadding * 2f;
        DrawNestedBoneFilter();
    }

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

        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            Crystarium.OpenPopover(BoneFilterMenuId);
        }
        var menuPos = ImGui.GetWindowPos();
        float gap = Crystarium.ActiveTheme.Floating.AnchorGap * scale;
        DrawBoneFilterMenu(new Vector2(
            menuPos.X + ImGui.GetWindowSize().X + gap,
            menuPos.Y - gap));
    }

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

    public void DrawOptionsRail(Vector2 origin, Vector2 size)
    {
        DrawOptionsSections(
            origin, size.X, withPresets: false,
            previewCap: size.Y,
            dense: true);
    }


    private const float ImportPreviewImageWidth = 256f;

    private float _importBandHeight;

    private const string ImportPreviewIdleText = "Pick a pose file to preview.";

    private const string ImportPreviewRebaseText = "Reading the actor's pose…";

    private IActor? _importTarget;

    private ISkeleton? _importSkeleton;

    private bool _importPreviewOwned;

    private bool _importPreviewPosed;

    private void DrawImportPreviewPanel(
        Vector2 origin, Vector2 size, string? highlighted)
    {
        SyncImportPreview(highlighted);
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        DrawPreviewBlock(
            origin,
            size,
            _importPreview.IsWaitingForBaseline
                ? ImportPreviewRebaseText
                : ImportPreviewIdleText,
            showRender: _importPreviewPosed,
            horizontalInset: theme.Page.Inset * scale,
            topPadding: PreviewTopPadding(theme) * scale,
            imageWidth: ImportPreviewImageWidth * scale);
    }

    private void ConfigureImportBand()
    {
        var theme = Crystarium.ActiveTheme;
        var grid = PoseImportOptionsGrid.Create(
            width: 0f,
            theme.Page.Inset,
            theme.Spacing.Two,
            theme.Page.ActionGap,
            theme.Controls.ListRowHeight,
            theme.Page.SectionHeaderHeight,
            theme.Page.StatusLineHeight,
            ImportStatusRows());
        if (MathF.Abs(_importBandHeight - grid.Height) < 0.01f
            && _importBrowser.BottomPanel is not null)
            return;
        _importBandHeight = grid.Height;
        _importBrowser.BottomPanel =
            new FileSidePanel(_importBandHeight, DrawImportOptionsBand);
    }

    private void RefreshImportBand(string? highlighted)
    {
        SyncCmpComponentLock(highlighted);
        SyncFaceWarning(highlighted);
        ConfigureImportBand();
    }

    private int ImportStatusRows() =>
        (_faceWarning is null ? 0 : 1)
        + (IsAnyIkArmed?.Invoke() == true ? 1 : 0);

    private void DrawImportOptionsBand(
        Vector2 origin, Vector2 size, string? highlighted)
    {
        SyncCmpComponentLock(highlighted);
        SyncFaceWarning(highlighted);
        SyncApplyOnSelect(highlighted);
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var grid = PoseImportOptionsGrid.Create(
            size.X / scale,
            theme.Page.Inset,
            theme.Spacing.Two,
            theme.Page.ActionGap,
            theme.Controls.ListRowHeight,
            theme.Page.SectionHeaderHeight,
            theme.Page.StatusLineHeight,
            ImportStatusRows());
        float top = origin.Y + grid.RowY(0) * scale;
        float columnWidth = grid.ColumnWidth * scale;
        var optionsTop = new Vector2(
            origin.X + grid.OptionsX * scale, top);
        var continuationTop = new Vector2(
            origin.X + grid.OptionsContinuationX * scale,
            top + theme.Page.SectionHeaderHeight * scale);
        var applyTop = new Vector2(
            origin.X + grid.ApplyX * scale, top);
        DrawImportOptionsMatrix(optionsTop, continuationTop, columnWidth);
        float applyHeight = DrawImportApplyCard(applyTop, columnWidth);
        DrawImportTypeCard(
            new Vector2(applyTop.X, applyTop.Y + applyHeight), columnWidth);
        DrawNestedBoneFilter();

        SyncImportPreview(highlighted);
    }

    private void DrawNestedBoneFilter()
    {
        if (_boneFilterRequested)
        {
            _boneFilterRequested = false;
            Crystarium.OpenPopover(BoneFilterMenuId);
        }
        DrawBoneFilterMenu(_filterAnchor);
    }

    private bool _cmpHighlighted;

    private string? _lastHighlighted;

    private string? _faceWarning;
    private string? _faceWarningPath;

    public Func<bool>? IsAnyIkArmed;

    private void SyncFaceWarning(string? highlighted)
    {
        if (string.Equals(
                highlighted, _faceWarningPath, StringComparison.Ordinal))
            return;
        _faceWarningPath = highlighted;
        _faceWarning = null;
        if (highlighted is null
            || !IsPoseFile(highlighted)
            || _importSkeleton is not { } skeleton)
            return;

        bool isCmp = highlighted.EndsWith(
            ".cmp", StringComparison.OrdinalIgnoreCase);
        if (LoadForSmartRouting(highlighted, isCmp) is not { } file)
            return;
        _faceWarning =
            PoseFileService.CompareFaceGeneration(file, skeleton) switch
            {
                PoseFileService.FaceGenerationMatch
                        .PreDawntrailFileOnDawntrailSkeleton =>
                    "This pose predates the Dawntrail face. Its face rotations "
                        + "will apply; its face positions will not, because "
                        + "they would deform this face.",
                PoseFileService.FaceGenerationMatch
                        .DawntrailFileOnOlderSkeleton =>
                    "This pose carries a Dawntrail face and this model does "
                        + "not have one. Its face bones may land badly or not "
                        + "at all.",
                _ => null,
            };
    }

    private (bool Rotation, bool Position, bool Scale)? _preCmpComponents;

    private void SyncCmpComponentLock(string? highlighted)
    {
        _lastHighlighted = highlighted;
        bool cmp = highlighted is { } path
            && path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);
        _cmpHighlighted = cmp;
        if (cmp && _preCmpComponents == null)
        {
            _preCmpComponents = (_rotation, _position, _scale);
            _rotation = true;
            _position = false;
            _scale = false;
        }
        else if (!cmp && _preCmpComponents is { } restored)
        {
            (_rotation, _position, _scale) = restored;
            _preCmpComponents = null;
        }
    }

    private void SyncApplyOnSelect(string? highlighted)
    {
        if (!_applyOnSelect || highlighted is null || !IsPoseFile(highlighted))
            return;
        if (string.Equals(
                highlighted, _appliedOnSelectPath, StringComparison.Ordinal))
            return;
        _appliedOnSelectPath = highlighted;
        if (_importSkeleton is { } skeleton)
            ImportFromPath(skeleton, highlighted, fromDialog: true);
    }

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
            _importPreview.Pose(
                highlighted,
                PosePreviewBinder.Trim(
                    CmpImportOverride(highlighted, out _, out _)
                        ?? BuildOptions()));
            _importPreviewPosed = true;
        }
    }

    private static bool IsPoseFile(string path) =>
        path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);

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
        SyncCmpComponentLock(null);
        _appliedOnSelectPath = null;
        _faceWarning = null;
        _faceWarningPath = null;
        _importTarget = null;
        _importSkeleton = null;
        if (PreviewClaimed)
            _importPreview.StandDown();
        else
            _importPreview.Close();
    }

    public void SetPreviewVisible(bool visible, string? idleText = null)
    {
        _previewVisible = visible;
        _previewIdleText = idleText;
    }

    private bool _previewVisible;
    private string? _previewIdleText;

    public void SetCharacterFile(McdfSummary? summary, string? status)
    {
        _characterFile = summary;
        _characterFileStatus = status;
        _characterFileStated = summary != null || status != null;
    }

    private McdfSummary? _characterFile;
    private string? _characterFileStatus;
    private bool _characterFileStated;

    private void DrawCharacterFileBody(Crystarium.FormScope form)
    {
        if (_characterFile is not { } file)
        {
            form.Status(_characterFileStatus ?? "Select a character file.");
            return;
        }

        form.ReadOnly("File", file.FileName);
        var carries = new List<string>(3);
        if (file.HasAppearance)
            carries.Add("Appearance");
        if (file.HasBodyProfile)
            carries.Add("Body profile");
        if (file.HasManipulations)
            carries.Add("Meta");
        form.ReadOnly(
            "Carries",
            carries.Count > 0 ? string.Join(" · ", carries) : "Nothing",
            help: "Appearance is the packaged Glamourer state, Body profile "
                + "the Customize+ one, Meta the Penumbra manipulations");
        form.ReadOnly(
            "Mod files",
            file.FileCount == 0
                ? "None"
                : $"{file.FileCount} ({FormatBytes(file.DeclaredBytes)})",
            help: "The size the package DECLARES for its payloads — the "
                + "header is all that was read");
        if (file.SwapCount > 0)
            form.ReadOnly("File swaps", file.SwapCount.ToString());
        if (file.Description.Length > 0)
            form.Status(file.Description);
        form.Status(
            "No render: applying a character file is a scene import, not a "
            + "preview.");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double value = bytes / 1024d;
        if (value < 1024d)
            return $"{value:0.#} KiB";
        value /= 1024d;
        return value < 1024d
            ? $"{value:0.#} MiB"
            : $"{value / 1024d:0.##} GiB";
    }

    public void SetPreviewClaim(bool claimed)
    {
        _previewClaimed = claimed;
        _previewClaimFrame = ImGui.GetFrameCount();
    }

    private bool _previewClaimed;
    private int _previewClaimFrame = int.MinValue;

    private bool PreviewClaimed =>
        _previewClaimed && ImGui.GetFrameCount() - _previewClaimFrame <= 1;

    public bool IsImportPreviewActive => _importBrowser.IsOpen;

    private bool _previewDragging;

    private static readonly Vector2 PreviewAspect = new(192f, 320f);

    private const string PreviewBackingPath = "ui/common/characterbg_hr1.tex";

    private ISharedImmediateTexture? _previewBacking;
    private bool _previewBackingFailed;
    private int _previewBackingPendingFrames;
    private const int PreviewBackingWarmFrames = 30;

    private float _previewFadeRamp;

    private float _dialogFadeRamp;

    private nint ResolvePreviewBacking()
    {
        if (_previewBackingFailed)
            return 0;
        IDalamudTextureWrap? wrap = null;
        try
        {
            _previewBacking ??= _textures.GetFromGame(PreviewBackingPath);
            if (!_previewBacking.TryGetWrap(out wrap, out _))
                return 0;
        }
        catch (Exception)
        {
            _previewBackingFailed = true;
        }
        return wrap is null ? 0 : (nint)wrap.Handle.Handle;
    }

    // Pending preview loading delays primary opening for a bounded interval.
    internal bool PrewarmPreviewBacking()
    {
        if (_previewBackingFailed || ResolvePreviewBacking() != 0)
            return true;
        if (_previewBackingPendingFrames < PreviewBackingWarmFrames)
        {
            _previewBackingPendingFrames++;
            return false;
        }
        return true;
    }

    private const string PreviewWaitingText = "Preparing preview…";

    private const float PreviewZoomButtonStep = 5f;

    private const float PreviewZoomWheelStep = 0.5f;

    private const float PreviewDragYawScale = 0.5f;

    private const float PreviewDragPanScale = 0.006f;

    private static readonly int[] PreviewCameraGroups = [2, 1];

    private static float MenuSection(
        string id,
        string title,
        Vector2 origin,
        float width,
        Action<Crystarium.FormScope> rows,
        bool divider = true,
        bool dense = false,
        float? labelColumnWidth = null,
        bool showTitle = false) =>
        Crystarium.Section(
            id,
            dense && !showTitle ? string.Empty : title,
            origin,
            width,
            true,
            null,
            rows,
            divider: divider,
            labelColumnWidth: labelColumnWidth
                ?? (dense ? DenseLabelColumn : MenuLabelColumn),
            dense: dense);

    /// <summary>The preview alone — the library window's plain right
    /// column. Not a rail and not styled as one.</summary>
    public void DrawPreviewColumn(Vector2 origin, Vector2 size)
        => MenuSection("##library-preview", "Preview",
            origin, size.X,
            form => DrawPreviewBody(form, size.X, size.Y),
            divider: false);

    private float DrawOptionsSections(
        Vector2 origin, float width, bool withPresets, float previewCap = 0f,
        bool withActions = true, bool dense = false)
    {
        float y = origin.Y;

        bool preview = _previewVisible && previewCap > 0f;
        bool leadSection = false;
        if (preview)
        {
            y += MenuSection(
                "##pose-preview", "Preview",
                new Vector2(origin.X, y), width,
                form => DrawPreviewBody(form, width, previewCap),
                divider: false);
            leadSection = true;
        }
        else if (previewCap > 0f && _characterFileStated)
        {
            y += MenuSection(
                "##character-file", "Character file",
                new Vector2(origin.X, y), width,
                DrawCharacterFileBody,
                divider: false);
            leadSection = true;
        }

        y += DrawImportTypeSection(
            new Vector2(origin.X, y), width,
            divider: leadSection, dense: dense);
        y += DenseImportGroupGap(dense);
        y += DrawTransformSection(
            new Vector2(origin.X, y), width, divider: false, dense: dense);
        y += DenseImportGroupGap(dense);
        y += DrawScopeSection(
            new Vector2(origin.X, y), width, divider: false, dense: dense);

        if (!withActions)
            return y;

        y += DenseImportGroupGap(dense);
        y += MenuSection(
            "##import-menu-import", "Import",
            new Vector2(origin.X, y), width,
            form =>
            {
                form.Actions("File", actions =>
                {
                    actions.Button("From file", () =>
                    {
                        if (SelectedSkeleton() is { } skeleton)
                            OpenImport(skeleton);
                        else
                            _notices.Refused(NoActorText);
                    });
                    actions.Button("From library",
                        () => OnLibraryRequested?.Invoke(),
                        disabled: InLibrary,
                        help: InLibrary ? "The library is already open" : null);
                });
                form.Actions("Clipboard", actions => actions.Button(
                    "From clipboard", ImportFromClipboard,
                    help: "Import the pose held on the clipboard — Brio's "
                        + "copy is read as-is"));
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
                        actions.Button(
                            _referenceArmed ? "Confirm reference" : "Reference",
                            ApplyReferencePreset,
                            help: "Restore the skeleton's built-in reference "
                                + "pose — replaces the current pose of every "
                                + "bone; undo restores it");
                    });
            });

        return y;
    }

    private static float DenseImportGroupGap(bool dense) => dense
        ? Crystarium.ActiveTheme.Spacing.Three
            * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale
        : 0f;

    private void DrawImportOptionsMatrix(
        Vector2 optionsOrigin, Vector2 continuationOrigin, float width)
    {
        var scope = BuildImportScopeItems();
        DrawImportOptionsCard(optionsOrigin, width, scope);
        DrawImportOptionsContinuation(continuationOrigin, width, scope);
    }

    private float DrawImportOptionsCard(
        Vector2 origin, float width, IReadOnlyList<Crystarium.CheckItem> scope) =>
        MenuSection(
            "##import-dialog-options-card", "Options",
            origin, width,
            form =>
            {
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem("Freeze", _freeze, next =>
                    {
                        _freeze = next;
                        _config.Config.FreezeActorOnPoseImport = next;
                        _config.Save();
                    }, "Keep the actor paused after the import"));
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    scope[3]);
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    scope[4]);
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    scope[2]);
            },
            divider: false,
            dense: true,
            labelColumnWidth: 0f,
            showTitle: true);

    private float DrawImportTypeCard(Vector2 origin, float width) =>
        MenuSection(
            "##import-dialog-type-card", "Type",
            origin, width,
            form =>
            {
                bool typeLocked = _selectiveImport && !_selectiveDescendants;
                const string typeLockedWhy =
                    "Selected bones import directly — the type gates only "
                    + "descendants (turn on Include descendants to use it)";
                form.Checkboxes(
                    string.Empty,
                    disabled: typeLocked,
                    fullWidth: true,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Body", _typeBody,
                        next => _typeBody = next,
                        typeLocked
                            ? typeLockedWhy
                            : "Import the body. With Expression too, everything "
                                + "imports with every component"),
                    new Crystarium.CheckItem(
                        "Expression", _typeExpression,
                        next => _typeExpression = next,
                        typeLocked
                            ? typeLockedWhy
                            : "Import the face as an expression — always every "
                                + "component"));
                if (_faceWarning is { } faceWarning)
                    form.Status(faceWarning);
                if (IsAnyIkArmed?.Invoke() == true)
                    form.Status(
                        "Live IK is on. It will keep solving after the "
                        + "import and override the limbs the pose places.");
            },
            divider: false,
            dense: true,
            labelColumnWidth: 0f,
            showTitle: true);

    private float DrawImportApplyCard(Vector2 origin, float width) =>
        MenuSection(
            "##import-dialog-apply-card", "Apply",
            origin, width,
            form =>
            {
                bool locked = _cmpHighlighted || _typeExpression || _smartImport;
                string? why = _cmpHighlighted
                    ? "CMTool poses carry rotations only — there is no "
                        + "position or scale in the file to apply"
                    : locked
                        ? "Expression imports always apply every component"
                        : null;
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Position", _position, next => _position = next, why,
                        Disabled: locked),
                    new Crystarium.CheckItem(
                        "Rotation", _rotation, next => _rotation = next, why,
                        Disabled: locked));
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Scale", _scale, next => _scale = next, why,
                        Disabled: locked),
                    new Crystarium.CheckItem(
                        "Model", _modelTransform,
                        next => _modelTransform = next,
                        "Also move the actor to the file's placement "
                            + "(model transform)",
                        Disabled: _smartImport));
            },
            divider: false,
            dense: true,
            labelColumnWidth: 0f,
            showTitle: true);

    private float DrawImportOptionsContinuation(
        Vector2 origin, float width, IReadOnlyList<Crystarium.CheckItem> scope) =>
        MenuSection(
            "##import-dialog-options-continuation", string.Empty,
            origin, width,
            form =>
            {
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    new Crystarium.CheckItem(
                        "Smart", _smartImport, next => _smartImport = next,
                        "Route face-only files as expression imports automatically"));
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    new Crystarium.CheckItem(
                        "Apply on select", _applyOnSelect,
                        next =>
                        {
                            _applyOnSelect = next;
                            _appliedOnSelectPath = next
                                ? _lastHighlighted
                                : null;
                        },
                        "Import a file the moment it is highlighted, "
                            + "instead of waiting for Load"));
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    scope[0]);
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: true,
                    scope[1]);
            },
            divider: false,
            dense: true,
            labelColumnWidth: 0f,
            showTitle: true);

    private List<Crystarium.CheckItem> BuildImportScopeItems()
    {
        var scope = new List<Crystarium.CheckItem>(5);
        bool hasSelection = HasSelectedBonesForImportTarget();
        bool anchorable = SelectiveImportAppliesPosition();
        scope.Add(new Crystarium.CheckItem(
            "Selected bones", _selectiveImport,
            next => _selectiveImport = next,
            hasSelection || _selectiveImport
                ? "Apply the pose only to the bones currently "
                    + "selected on this actor"
                : "Select bones on the target actor first",
            Disabled: !hasSelection && !_selectiveImport));
        scope.Add(new Crystarium.CheckItem(
            "Include descendants", _selectiveDescendants,
            next => _selectiveDescendants = next,
            "Extend the selected-bones scope to every "
                + "descendant of the selected bones",
            Disabled: !_selectiveImport));
        scope.Add(new Crystarium.CheckItem(
            "Anchor positions", _selectiveAnchor,
            next => _selectiveAnchor = next,
            anchorable || !_selectiveImport
                ? "Keep the selected bones (and descendants) "
                    + "where they stand — the file's rotations "
                    + "and scales apply, its positions do not"
                : _smartImport
                    ? "This import applies no position — Smart "
                        + "Import's preset decides the components, "
                        + "and there is nothing to anchor"
                    : "Turn on the Position component first — "
                        + "without it there is nothing to anchor",
            Disabled: !_selectiveImport || !anchorable));
        scope.Add(new Crystarium.CheckItem(
            "Reset first", _reset, next => _reset = next,
            "Clear every bone in scope before importing, "
                + "including ones the file does not contain"));
        scope.Add(new Crystarium.CheckItem(
            "Exclude ear bones", _excludeEars,
            next => _excludeEars = next,
            "Leave ears where they are — the six standard ear "
                + "bones and the Viera ear chains"));
        return scope;
    }

    private void DrawImportFooterFilter(Crystarium.ActionBarScope actions)
    {
        bool typed = _typeBody || _typeExpression;
        actions.Button(
            "Bone filter",
            RequestBoneFilterMenu,
            style: ControlStyle.Comfortable,
            disabled: typed,
            help: typed
                ? "The bone filter shapes the default import; "
                    + "uncheck Body and Expression to edit it"
                : "Choose which bone categories imports may touch");
    }

    private float DrawImportTypeSection(
        Vector2 origin, float width, bool divider, bool dense = false,
        bool selective = false) =>
        MenuSection(
            "##import-menu-head", "Import pose",
            origin, width,
            form =>
            {
                form.Checkboxes(
                    "Options",
                    disabled: false,
                    fullWidth: false,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem("Freeze", _freeze, next =>
                    {
                        _freeze = next;
                        _config.Config.FreezeActorOnPoseImport = next;
                        _config.Save();
                    }, "Keep the actor paused after the import"),
                    new Crystarium.CheckItem(
                        "Smart", _smartImport, next => _smartImport = next,
                        "Route face-only files as expression imports automatically"));
                if (selective)
                    form.Checkbox(
                        "Apply on select", _applyOnSelect,
                        next =>
                        {
                            _applyOnSelect = next;
                            _appliedOnSelectPath = next ? _lastHighlighted : null;
                        },
                        help: "Import a file the moment it is highlighted, "
                            + "instead of waiting for Load");
                if (dense)
                    form.Canvas("type-gap", Crystarium.ActiveTheme.Spacing.Three,
                        static (_, _) => { });
                bool typeLocked =
                    selective && _selectiveImport && !_selectiveDescendants;
                const string typeLockedWhy =
                    "Selected bones import directly — the type gates only "
                    + "descendants (turn on Include descendants to use it)";
                form.Checkboxes(
                    "Type",
                    disabled: typeLocked,
                    fullWidth: false,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Body", _typeBody,
                        next => _typeBody = next,
                        typeLocked
                            ? typeLockedWhy
                            : "Import the body. With Expression too, everything "
                                + "imports with every component"),
                    new Crystarium.CheckItem(
                        "Expression", _typeExpression,
                        next => _typeExpression = next,
                        typeLocked
                            ? typeLockedWhy
                            : "Import the face as an expression — always every "
                                + "component"));
                if (selective)
                {
                    if (_faceWarning is { } faceWarning)
                        form.Status(faceWarning);
                    if (IsAnyIkArmed?.Invoke() == true)
                        form.Status(
                            "Live IK is on. It will keep solving after the "
                            + "import and override the limbs the pose places.");
                }
            },
            divider: divider,
            dense: dense,
            labelColumnWidth: ImportOptionLabelColumn);

    private float DrawTransformSection(
        Vector2 origin, float width, bool divider, bool dense = false) =>
        MenuSection(
            "##import-menu-transform", "Transform",
            origin, width,
            form =>
            {
                bool locked = _cmpHighlighted || _typeExpression || _smartImport;
                string? why = _cmpHighlighted
                    ? "CMTool poses carry rotations only — there is no "
                        + "position or scale in the file to apply"
                    : locked
                        ? "Expression imports always apply every component"
                        : null;
                form.Checkboxes(
                    "Apply",
                    disabled: false,
                    fullWidth: false,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Position", _position, next => _position = next, why,
                        Disabled: locked),
                    new Crystarium.CheckItem(
                        "Rotation", _rotation, next => _rotation = next, why,
                        Disabled: locked));
                form.Checkboxes(
                    string.Empty,
                    disabled: false,
                    fullWidth: false,
                    PoseImportOptionsGrid.CheckboxColumnPitch,
                    new Crystarium.CheckItem(
                        "Scale", _scale, next => _scale = next, why,
                        Disabled: locked),
                    new Crystarium.CheckItem(
                        "Model", _modelTransform,
                        next => _modelTransform = next,
                        "Also move the actor to the file's placement "
                            + "(model transform)",
                        Disabled: _smartImport));
            },
            divider: divider,
            dense: dense,
            labelColumnWidth: ImportOptionLabelColumn);

    private float DrawScopeSection(
        Vector2 origin, float width, bool divider, bool dense = false,
        bool selective = false) =>
        MenuSection(
            "##import-menu-scope", "Scope",
            origin, width,
            form =>
            {
                var scope = new List<Crystarium.CheckItem>(4);
                if (selective)
                {
                    bool hasSelection = HasSelectedBonesForImportTarget();
                    bool anchorable = SelectiveImportAppliesPosition();
                    scope.Add(new Crystarium.CheckItem(
                        "Selected bones", _selectiveImport,
                        next => _selectiveImport = next,
                        hasSelection || _selectiveImport
                            ? "Apply the pose only to the bones currently "
                                + "selected on this actor"
                            : "Select bones on the target actor first",
                        Disabled: !hasSelection && !_selectiveImport));
                    scope.Add(new Crystarium.CheckItem(
                        "Include descendants", _selectiveDescendants,
                        next => _selectiveDescendants = next,
                        "Extend the selected-bones scope to every "
                            + "descendant of the selected bones",
                        Disabled: !_selectiveImport));
                    scope.Add(new Crystarium.CheckItem(
                        "Anchor positions", _selectiveAnchor,
                        next => _selectiveAnchor = next,
                        anchorable || !_selectiveImport
                            ? "Keep the selected bones (and descendants) "
                                + "where they stand — the file's rotations "
                                + "and scales apply, its positions do not"
                            : _smartImport
                                ? "This import applies no position — Smart "
                                    + "Import's preset decides the "
                                    + "components, and there is nothing to "
                                    + "anchor"
                                : "Turn on the Position component first — "
                                    + "without it there is nothing to anchor",
                        Disabled: !_selectiveImport || !anchorable));
                }
                scope.Add(new Crystarium.CheckItem(
                    "Reset first", _reset, next => _reset = next,
                    "Clear every bone in scope before importing, "
                        + "including ones the file does not contain"));
                scope.Add(new Crystarium.CheckItem(
                    "Exclude ear bones", _excludeEars,
                    next => _excludeEars = next,
                    "Leave ears where they are — the six standard ear "
                        + "bones and the Viera ear chains"));
                form.Checkboxes(
                    "Scope", disabled: false, fullWidth: false,
                    scope.ToArray());
                bool typed = _typeBody || _typeExpression;
                if (dense)
                    form.Canvas("scope-filter-gap",
                        Crystarium.ActiveTheme.Spacing.Three,
                        static (_, _) => { });
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
            dense: dense,
            labelColumnWidth: ImportOptionLabelColumn);

    private void DrawPreviewBody(
        Crystarium.FormScope form, float width, float cap)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float topPadding = PreviewTopPadding(theme);
        float imageWidth = MathF.Min(width, ImportPreviewImageWidth * scale);
        int rows = PreviewCameraRows(imageWidth, scale, theme);
        float camera = PreviewCameraHeight(rows, theme) * scale;
        var box = PreviewBox(
            imageWidth,
            MathF.Max(0f, cap - topPadding * scale - camera));
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        bool mirror = IsImportPreviewActive;
        form.Canvas("preview-top-padding", topPadding, static (_, _) => { });
        form.Canvas("preview-image", box.Y / scale,
            (min, _) => DrawPreviewImage(
                min + new Vector2((width - box.X) * 0.5f, 0f),
                new Vector2(box.X, box.Y), box.X, scale, theme,
                ref _previewFadeRamp,
                emptyText: mirror ? null : _previewIdleText,
                showRender: !mirror));
        form.Canvas(
            "preview-camera",
            PreviewCameraHeight(rows, theme),
            (min, _) => DrawPreviewCamera(
                min + new Vector2(
                    (width - box.X) * 0.5f,
                    theme.Spacing.Three * scale),
                box.X, scale, theme, rows));
    }

    private void DrawPreviewBlock(
        Vector2 origin, Vector2 size, string? emptyText,
        bool showRender = true, float horizontalInset = 0f,
        float topPadding = 0f, float imageWidth = 0f)
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float contentWidth = MathF.Max(0f, size.X - horizontalInset * 2f);
        float width = imageWidth > 0f
            ? MathF.Min(contentWidth, imageWidth)
            : contentWidth;
        int rows = PreviewCameraRows(width, scale, theme);
        float camera = PreviewCameraHeight(rows, theme) * scale;
        var box = PreviewBox(
            width,
            MathF.Max(0f, size.Y - topPadding - camera));
        if (!(box.X > 0f) || !(box.Y > 0f))
            return;

        float x = origin.X + horizontalInset + (contentWidth - box.X) * 0.5f;
        float y = origin.Y + topPadding;
        DrawPreviewImage(
            new Vector2(x, y), new Vector2(box.X, box.Y), box.X,
            scale, theme,
            ref _dialogFadeRamp, emptyText, showRender);
        DrawPreviewCamera(
            new Vector2(x, y + box.Y + theme.Spacing.Three * scale),
            box.X, scale, theme, rows);
    }

    private static float PreviewTopPadding(Theme theme) => theme.Page.Inset;

    private static Vector2 PreviewBox(float width, float cap)
    {
        float height = width * (PreviewAspect.Y / PreviewAspect.X);
        return height > cap
            ? new Vector2(cap * (PreviewAspect.X / PreviewAspect.Y), cap)
            : new Vector2(width, height);
    }

    private static float PreviewCameraHeight(int rows, Theme theme) =>
        theme.Spacing.Three
        + theme.Floating.CloseActionSize * rows
        + theme.Page.ActionGap * (rows - 1);

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

        if (showRender && handle != 0
            && _preview.RefusalText is { Length: > 0 } notice)
            DrawPreviewNotice(boxMin, boxSize, radius, scale, theme, notice);

        Crystarium.FloatingSurface.DrawBorder(boxMin, boxMax, radius);
    }

    private static void DrawPreviewNotice(
        Vector2 boxMin, Vector2 boxSize, float radius, float scale,
        Theme theme, string notice)
    {
        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.Warning,
        };
        float inset = theme.Page.Inset * scale;
        float band = Crystarium.MeasureText(notice, style).Y + inset;
        float width = MathF.Max(1f, boxSize.X - inset * 2f);
        var bandMin = new Vector2(boxMin.X, boxMin.Y + boxSize.Y - band);
        ImGui.GetWindowDrawList().AddRectFilled(
            bandMin,
            boxMin + boxSize,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.Chrome.ModalDim)),
            radius,
            ImDrawFlags.RoundCornersBottom);
        Crystarium.TextInBand(
            new Vector2(bandMin.X + inset, bandMin.Y),
            new Vector2(width, band),
            notice,
            style,
            TextConstraint.Truncate(width, TextAlign.Center),
            TextAlign.Center);
    }

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

    private static int PreviewCameraRows(float width, float scale, Theme theme)
        => PreviewCameraBandWidth(
            PreviewCameraGroups, 0, PreviewCameraGroups.Length, scale, theme)
            <= width ? 1 : 2;

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

    private void ApplyReferencePreset()
    {
        if (!_referenceArmed)
        {
            _referenceArmed = true;
            _notices.Refused(
                "Reference pose replaces the current pose of every bone "
                + "(undo restores it). Press Confirm reference to apply.");
            return;
        }
        _referenceArmed = false;
        if (SelectedSkeleton() is not { } skeleton
            || _poseFacade.GetActorId(skeleton.Actor) is not { } expectedActor)
        {
            _notices.Refused(NoActorText);
            return;
        }
        NotePoseApplied();
        if (_poseFacade.ApplyReferencePose(
                skeleton.Actor, TrackImport(expectedActor)) is
            { Success: false } failed)
            _notices.Failed($"Reference: {failed.Detail}");
    }

    private void ApplyRestPreset(RestPose pose)
    {
        _referenceArmed = false;
        if (SelectedSkeleton() is { } skeleton)
        {
            if (_poseFacade.GetActorId(skeleton.Actor) is not { } expectedActor)
            {
                _notices.Refused(NoActorText);
                return;
            }
            NotePoseApplied();
            if (_poseFacade.ApplyRestPose(
                    skeleton.Actor,
                    pose,
                    TrackImport(expectedActor)) is
                { Success: false } failed)
                _notices.Failed($"Preset: {failed.Detail}");
        }
        else
            _notices.Refused(NoActorText);
    }

    private void NotePoseApplied()
    {
        TargetPoseRevision++;
        _importPreview.InvalidateBaseline();
    }

    public int TargetPoseRevision { get; private set; }

    private void DrawBoneFilterBody()
    {
        float scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        var page = Crystarium.ActiveTheme.Page;

        float top = origin.Y - MenuTitleOffset(scale);
        float y = top + MenuSection(
            "##filter-head", "Bone filter",
            new Vector2(origin.X, top), width,
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
            divider: false);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, y));
        float scrollHeight =
            _boneFilterHeight - (y - origin.Y) / scale
            - page.Inset - MenuPadding * 2f;
        Crystarium.ScrollRegion(
            "##filter-scroll", width / scale + MenuPadding, scrollHeight, _ =>
            {
                var top = ImGui.GetCursorScreenPos();
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
        SetHostImportTarget(skeleton.Actor, inLibrary: false);

        form.Actions("Pose", actions =>
        {
            actions.Button("Import", () => RequestImportMenu(withPresets: true));
            actions.Button("Export", () => RequestExportMenu());
            actions.Button("Library", () => OnLibraryRequested?.Invoke());
        });
    }

    public void OpenImport(ISkeleton skeleton)
    {
        if (_config.Config.Library.UseLibraryWhenImporting)
        {
            OnLibraryRequested?.Invoke();
            return;
        }

        BrowseAndImport(skeleton, _lastPath, rememberPath: true);
    }

    public void OpenAutoSaves(ISkeleton skeleton)
    {
        BrowseAndImport(skeleton, _autoSave.RootDirectory, rememberPath: false);
    }

    private void BrowseAndImport(
        ISkeleton skeleton,
        string initialPath,
        bool rememberPath)
    {
        ConfigureImportBand();
        _importTarget = skeleton.Actor;
        _importSkeleton = skeleton;
        _importPreviewPosed = false;
        _dialogFadeRamp = 0f;
        OpenBrowser(() => _importBrowser.Open(initialPath, path =>
        {
            if (rememberPath)
                _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            ImportFromPath(skeleton, path, fromDialog: true);
        }));
    }

    private void ImportFromPath(ISkeleton skeleton, string path, bool fromDialog = false)
    {
        bool isCmp = path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase);
        string notice = string.Empty;
        if (_smartImport && LoadForSmartRouting(path, isCmp) is { } smartFile)
            notice = SmartRoute(skeleton, smartFile);

        _lastImportPath = path;
        _lastImportPose = null;

        var cmp = CmpImportOverride(path, out bool blocked, out var cmpNotice);
        if (cmpNotice != null)
            notice = cmpNotice;
        if (blocked)
        {
            _notices.Refused(notice);
            return;
        }

        NotePoseApplied();
        if (_poseFacade.GetActorId(skeleton.Actor) is not { } expectedActor)
        {
            _notices.Failed("Import: the actor could not be resolved.");
            return;
        }
        IReadOnlyList<BoneId>? frozenSelection = null;
        var options = cmp ?? BuildOptions();
        if (fromDialog && _selectiveImport)
        {
            frozenSelection = FrozenSelectedBones(expectedActor);
            if (!_selectiveDescendants && cmp == null)
                options = RouteAsType(options, body: true, expression: false);
            options.FilterIncludesDescendants = _selectiveDescendants;
            options.AnchorSelectedPositions = _selectiveAnchor;
        }
        var imported = _poseFacade.ImportPose(
            skeleton.Actor,
            path,
            options,
            frozenSelection,
            TrackImport(expectedActor));
        if (!imported.Success)
            _notices.Failed($"Import: {imported.Detail}");
        else if (notice.Length > 0)
            _notices.Refused(notice);
    }

    private bool HasSelectedBonesForImportTarget()
    {
        if (_importTarget is not { } target
            || _poseFacade.GetActorId(target) is not { } actor)
            return false;
        foreach (var id in _selection.Selected)
        {
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } bone }
                && bone.Skeleton.Actor.Equals(actor))
                return true;
        }
        return false;
    }

    private List<BoneId> FrozenSelectedBones(ActorId target)
    {
        var frozen = new List<BoneId>();
        foreach (var id in _selection.Selected)
        {
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } bone }
                && bone.Skeleton.Actor.Equals(target))
                frozen.Add(bone);
        }
        return frozen;
    }

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

    private void ImportLoadedPose(
        ISkeleton skeleton, PoseFile pose, string description, string statusPrefix)
    {
        string notice = string.Empty;
        if (_smartImport)
            notice = SmartRoute(skeleton, pose);

        _lastImportPose = pose;
        _lastImportPath = null;

        NotePoseApplied();
        if (_poseFacade.GetActorId(skeleton.Actor) is not { } expectedActor)
        {
            _notices.Failed($"{statusPrefix}: the actor could not be resolved.");
            return;
        }
        var imported = _poseFacade.ImportPose(
            skeleton.Actor,
            pose,
            BuildOptions(),
            description,
            TrackImport(expectedActor));
        if (!imported.Success)
            _notices.Failed($"{statusPrefix}: {imported.Detail}");
        else if (notice.Length > 0)
            _notices.Refused(notice);
    }

    private static PoseFile? LoadForSmartRouting(string path, bool isCmp)
    {
        if (isCmp)
        {
            try
            {
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

    private PoseImportOptions CmpImportOptions()
    {
        var options = PoseImportOptions.Cmp;
        options.ResetBeforeImport = _reset;
        options.FreezeOnImport = _freeze;
        options.ApplyModelTransform = _modelTransform;
        return options;
    }

    private void ReapplyLastPose()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        if (_lastImportPath is { } path)
            ImportFromPath(skeleton, path);
        else if (_lastImportPose is { } pose)
            ImportLoadedPose(skeleton, pose, "Reapply last pose", "Reapply");
        else
            _notices.Refused("Nothing has been imported yet.");
    }

    private void ImportFromStash()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        if (_poseStash is not { } pose)
        {
            _notices.Refused("Nothing is stashed.");
            return;
        }
        ImportLoadedPose(skeleton, pose, "Import stashed pose", "Stash");
    }

    private void StashPose()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        var armed = _poseFacade.CapturePoseFile(skeleton.Actor, pose =>
        {
            if (pose == null)
            {
                _notices.Failed("Stash: the pose could not be captured.");
                return;
            }
            _poseStash = pose;
            _poseStashedAt = DateTimeOffset.UtcNow;
            _notices.Done("Pose stashed.");
        });
        if (!armed.Success)
            _notices.Failed($"Stash: {armed.Detail}");
    }

    public void OpenExport(ISkeleton skeleton)
    {
        OpenBrowser(() => _exportBrowser.Open(_lastPath, path =>
        {
            _lastPath = System.IO.Path.GetDirectoryName(path) ?? _lastPath;
            var armed = _poseFacade.ExportPose(
                skeleton.Actor,
                path,
                exported =>
                {
                    if (exported)
                        _notices.Done($"Pose saved to {path}.");
                    else
                        _notices.Failed(
                            "Export: the pose file could not be written.");
                });
            if (!armed.Success)
                _notices.Failed($"Export: {armed.Detail}");
        }));
    }

    private bool _libraryExportOpen;
    private ISkeleton? _libraryExportSkeleton;
    private string _libraryExportName = string.Empty;
    private int _libraryExportSource;
    private List<LibrarySourceConfig> _libraryExportSources = [];
    private string[] _libraryExportLabels = [];

    private string _libraryExportCandidate = string.Empty;
    private bool _libraryExportTaken;

    private static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(
            name, @"\s*\(\d+\)$", "");

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return name.IndexOfAny(invalid) < 0
            ? name
            : new string(name.Where(c => Array.IndexOf(invalid, c) < 0)
                .ToArray());
    }

    private void OpenExportToLibrary()
    {
        if (SelectedSkeleton(out var actorId) is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        var sources = ExportableSources();
        if (sources.Count == 0)
            return;

        _libraryExportSkeleton = skeleton;
        _libraryExportSources = sources;
        _libraryExportLabels = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            _libraryExportLabels[i] = string.IsNullOrWhiteSpace(sources[i].Name)
                ? $"Source {i + 1}"
                : sources[i].Name;
        }

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

    private void DrawExportLibraryModal()
    {
        if (!_libraryExportOpen || _libraryExportSkeleton is not { } skeleton)
            return;
        Crystarium.Modal(
            "##export-to-library",
            _libraryExportOpen,
            next => _libraryExportOpen = next,
            "Export to library",
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
                _notices.Done($"{name} saved to {source.Name}.");
                _library.RequestScan();
            }
            else
                _notices.Failed(
                    "Library: the pose file could not be written.");
        });
        if (!armed.Success)
            _notices.Failed($"Library: {armed.Detail}");
    }

    public PoseImportOptions BuildImportOptions() => BuildOptions();

    private PoseImportOptions BuildOptions()
    {
        var options = PoseImportOptions.ForImportType(
            _typeBody, _typeExpression, _rotation, _position, _scale,
            presetComponents: _smartImport);
        options.ResetBeforeImport = _reset;
        options.FreezeOnImport = _freeze;
        options.ApplyModelTransform = _modelTransform && !options.AsExpression;
        return ApplyEarExclusion(
            _typeBody || _typeExpression
                ? options
                : ApplyCategoryFilter(options));
    }

    private PoseImportOptions ApplyEarExclusion(PoseImportOptions options) =>
        _excludeEars
            ? ImportBoneCategories.ExcludeEarBones(options)
            : options;

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
        return ApplyEarExclusion(routed);
    }

    private bool SelectiveImportAppliesPosition() =>
        PoseImportOptions.ForImportType(
            body: !_selectiveDescendants || _typeBody,
            expression: _selectiveDescendants && _typeExpression,
            _rotation, _position, _scale,
            presetComponents: _smartImport).ApplyPosition;

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

    private void ImportFromClipboard()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        string text;
        try
        {
            text = ImGui.GetClipboardText();
        }
        catch (Exception ex)
        {
            _notices.Failed($"Clipboard: {ex.Message}");
            return;
        }
        if (PoseClipboard.Decode(text, out var reason) is not { } pose)
        {
            _notices.Failed($"Clipboard: {reason}");
            return;
        }
        ImportLoadedPose(
            skeleton, pose, "Import pose from clipboard", "Clipboard");
    }

    private void CopyToClipboard()
    {
        if (SelectedSkeleton() is not { } skeleton)
        {
            _notices.Refused(NoActorText);
            return;
        }
        var armed = _poseFacade.CapturePoseFile(skeleton.Actor, pose =>
        {
            if (pose == null || PoseClipboard.Encode(pose) is not { } payload)
            {
                _notices.Failed("Clipboard: the pose could not be copied.");
                return;
            }
            try
            {
                ImGui.SetClipboardText(payload);
                _notices.Done("Pose copied to the clipboard.");
            }
            catch (Exception ex)
            {
                _notices.Failed($"Clipboard: {ex.Message}");
            }
        });
        if (!armed.Success)
            _notices.Failed($"Clipboard: {armed.Detail}");
    }
}
