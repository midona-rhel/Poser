using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Domain.Library;
using static Poser.UI.Widgets.ActionBarWidgets;
using static Poser.UI.Widgets.ButtonWidgets;
using static Poser.UI.Widgets.DropdownWidgets;
using static Poser.UI.Widgets.FilterPillWidgets;
using static Poser.UI.Widgets.PageForm;
using static Poser.UI.Widgets.ScrollRegionWidgets;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextInputWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.WindowFrameWidgets;

namespace Poser.UI.Widgets;

/// <summary>
/// File browser with navigation, search, optional preview, and caller-supplied panels.
/// Confirmed file selections are delivered after the dialog closes; folder navigation
/// happens while it is open.
/// </summary>
public sealed class FileDialog
{
    /// <summary>
    /// Logical size of Back, Forward, and Up buttons.
    /// </summary>
    private const float NavActionSize = 24f;

    /// <summary>
    /// Logical width reserved for the modified-date text.
    /// </summary>
    private const float ModifiedColumnWidth = 92f;

    /// <summary>A row's leading mark, on the list-row band.</summary>
    private const float EntryIconSlot = 22f;

    /// <summary>
    /// Left margin before a quick-access row's icon.
    /// </summary>
    private const float QuickIconMargin = 2f;

    /// <summary>The row highlight's corner.</summary>
    private const float RowPillRadius = 5f;

    /// <summary>The preview panel's caption band.</summary>
    private const float PreviewCaptionHeight = 20f;

    /// <summary>
    /// The optional rail panel receives 40–60% of rail height; the quick list uses the rest.
    /// </summary>
    private const float RailFooterMinShare = 0.4f;
    private const float RailFooterMaxShare = 0.6f;

    private static readonly string[] SortOptions =
        ["Name", "Newest", "Oldest"];

    private static int CompareEntries(
        FileListingEntry left,
        FileListingEntry right,
        LibraryBrowseSort sort)
    {
        if (left.IsDirectory != right.IsDirectory)
            return left.IsDirectory ? -1 : 1;
        int modified = LibraryBrowseOrdering.CompareModified(
            left.Modified, right.Modified, sort);
        return modified != 0
            ? modified
            : string.Compare(
                left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private readonly Comparison<FileListingEntry> _compareEntries;

    private int CompareEntries(FileListingEntry left, FileListingEntry right) =>
        CompareEntries(left, right, _sort);

    private readonly Action<int> _setSort;

    private void SetSort(int selected)
    {
        if (!Enum.IsDefined((LibraryBrowseSort)selected)
            || selected == (int)_sort)
            return;
        _sort = (LibraryBrowseSort)selected;
        _entries.Sort(_compareEntries);
        _resetEntriesScroll = true;
    }

    private readonly string _title;
    private readonly string[] _extensions;
    private readonly bool _isSaveMode;
    private readonly string _id;

    /// <summary>
    /// Stable widget IDs built once for this dialog instance.
    /// </summary>
    private readonly string _surfaceId;
    private readonly string _navId;
    private readonly string _pathId;
    private readonly string _quickId;
    private readonly string _quickRowPrefix;
    private readonly string _entriesId;
    private readonly string _entryRowPrefix;
    private readonly string _nameId;
    private readonly string _searchId;
    private string _search = string.Empty;
    private string _nameFilter = string.Empty;
    private LibraryBrowseSort _sort;
    private bool _resetEntriesScroll;

    private readonly List<FileQuickEntry> _quick = new();
    private readonly List<FileListingEntry> _entries = new();
    private readonly List<FileListingEntry> _scratch = new();

    /// <summary>
    /// Visited folders, newest last. Ordinary navigation clears the forward history.
    /// </summary>
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();

    private bool _open;
    private string _currentPath = string.Empty;
    private string _fileName = string.Empty;
    private string? _selectedPath;
    private bool _selectedIsDirectory;
    private string? _lastError;
    private string? _pendingSelect;
    private Action<string>? _onSelect;
    public string? ConfirmLabel { get; init; }

    /// <summary>Opt-in open-dialog behavior: one activation navigates a
    /// folder or confirms a file. Save dialogs retain explicit confirmation.</summary>
    public bool SingleClickActivation { get; set; }

    /// <summary>
    /// Path-field draft. Navigation occurs when the edited field is committed.
    /// </summary>
    private string _pathEdit = string.Empty;

    /// <summary>The memoized preview and the path it was resolved for. The
    /// provider is asked once per selection, never per frame.</summary>
    private FilePreviewResult? _preview;
    private string? _previewPath;

    private readonly IFileListingSource _source = LocalFileListing.Instance;

    public FileDialog(
        string title,
        string[] extensions,
        bool isSaveMode = false)
    {
        _title = title;
        _extensions = extensions;
        _isSaveMode = isSaveMode;
        _id = $"##file-dialog-{Guid.NewGuid():N}";
        _surfaceId = $"{_title}{_id}";
        _navId = $"{_id}-nav";
        _pathId = $"{_id}-path";
        _quickId = $"{_id}-quick";
        _quickRowPrefix = $"{_id}-quick-";
        _entriesId = $"{_id}-entries";
        _entryRowPrefix = $"{_id}-entry-";
        _nameId = $"{_id}-name";
        _searchId = $"{_id}-search";
        _compareEntries = CompareEntries;
        _setSort = SetSort;
    }

    /// <summary>
    /// Optional preview provider, called when the selected path changes. A returned
    /// preview takes space from the file-list area; it does not widen the window.
    /// </summary>
    public Func<string, FilePreviewResult?>? FilePreview;

    /// <summary>
    /// Caller-supplied columns, drawn left-to-right after the file list. Their requested
    /// widths and separators are added to the dialog's initial width.
    /// </summary>
    public readonly List<FileSidePanel> SidePanels = new();

    /// <summary>Caller content that spans the body and bottom band.</summary>
    public FileSidePanel? PersistentRightPanel;

    /// <summary>Caller actions placed before Cancel in the footer.</summary>
    public Action<ActionBarScope>? FooterBeforeCancel;

    /// <summary>Caller work completed before the frame is measured.</summary>
    public Action<string?>? BeforeFrame;

    /// <summary>
    /// Optional panel below quick-access folders. Its Width is a minimum rail width;
    /// the dialog's initial width grows by any increase over the normal rail.
    /// </summary>
    public FileSidePanel? RailPanel;

    /// <summary>
    /// Optional band above the footer. Its Width is used as logical height, adding
    /// that height and a separator to the dialog's initial size.
    /// </summary>
    public FileSidePanel? BottomPanel;

    /// <summary>
    /// Additional logical height requested for the dialog body.
    /// </summary>
    public float ExtraHeight;

    /// <summary>Caller-specific width adjustment for this dialog.</summary>
    public float WidthAdjustment;

    /// <summary>Caller-specific height adjustment for this dialog.</summary>
    public float HeightAdjustment;

    public bool IsOpen => _open;

    /// <summary>
    /// Total logical width requested by side panels and their separators.
    /// </summary>
    private float PanelWidth()
    {
        float total = 0f;
        for (int i = 0; i < SidePanels.Count; i++)
            total += SidePanels[i].Width + 1f;
        if (PersistentRightPanel is { } persistent)
            total += persistent.Width + 1f;
        return total;
    }

    /// <summary>
    /// Normal rail width, increased to the optional rail panel's requested width.
    /// </summary>
    private float RailWidth() => MathF.Max(
        ActiveTheme.FileDialog.RailWidth,
        RailPanel?.Width ?? 0f);

    /// <summary>
    /// Extra initial width needed by a wider rail panel.
    /// </summary>
    private float RailExtra() =>
        RailWidth() - ActiveTheme.FileDialog.RailWidth;

    /// <summary>
    /// Initial height added for the bottom panel and separator.
    /// </summary>
    private float BottomExtra() =>
        BottomPanel is { } panel ? panel.Width + 1f : 0f;

    private string SurfaceId => _surfaceId;

    public void Open(string initialPath, Action<string> onSelect)
    {
        _onSelect = onSelect;
        Rehome(initialPath);
        _open = true;
        FloatingSurface.OpenWindow(SurfaceId);
    }

    public void Draw()
    {
        if (_open)
        {
            BeforeFrame?.Invoke(SelectedFile);
            FloatingSurface.Window(
                SurfaceId,
                ref _open,
                ActiveTheme.FileDialog.Width
                    + PanelWidth() + RailExtra() + WidthAdjustment,
                ActiveTheme.FileDialog.Height
                    + ExtraHeight + BottomExtra() + HeightAdjustment,
                DrawFrame);
        }

        if (!_open && _pendingSelect is { } chosen)
        {
            _pendingSelect = null;
            _onSelect?.Invoke(chosen);
        }
    }

    /// <summary>
    /// Sets the initial directory and clears selection, preview, filename, and navigation history.
    /// </summary>
    private void Rehome(string initialPath)
    {
        _selectedPath = null;
        _selectedIsDirectory = false;
        _fileName = string.Empty;
        _lastError = null;
        _preview = null;
        _previewPath = null;
        _back.Clear();
        _forward.Clear();
        if (_quick.Count == 0)
            _source.QuickAccess(_quick);
        if (string.IsNullOrEmpty(initialPath)
            || !_source.DirectoryExists(initialPath))
            initialPath = _source.DefaultPath;
        NavigateTo(initialPath);
    }

    private void DrawFrame(FloatingSurfaceFrame frame) =>
        RenderFrame(frame.Min, frame.Size, hostPaintsChrome: true);

    /// <summary>
    /// The frame, rendered into the product window's caller-owned box.
    /// </summary>
    /// <param name="hostPaintsChrome"><c>FloatingSurface.Window</c> already
    /// draws the glass for every window it hosts, so the product path tells
    /// the frame not to draw a second shadow over the first.</param>
    private void RenderFrame(
        Vector2 origin, Vector2 size, bool hostPaintsChrome = false)
    {
        Theme theme = ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        ResolvePreview();

        bool canConfirm = _isSaveMode
            ? _fileName.Trim().Length > 0
            : SelectedFile is not null;
        string confirmLabel = ConfirmLabel ?? (_isSaveMode ? "Save" : "Load");

        var rects = WindowFrame(
            _id,
            origin,
            size,
            new WindowFrameProps
            {
                Title = _title,
                OnClose = Close,
                CloseHelp = "Close without choosing a file",
                RailWidth = RailWidth(),
                BandHeight = theme.Floating.ModalBarHeight,
                BottomBandHeight = BottomExtra(),
                BottomBandRightInset = PersistentRightPanel is { } panel
                    ? panel.Width + 1f
                    : 0f,
                HostPaintsChrome = hostPaintsChrome,
                FooterRight = right =>
                {
                    FooterBeforeCancel?.Invoke(right);
                    right.Button(
                        "Cancel",
                        Close,
                        style: ControlStyle.Comfortable);
                    right.Button(
                        confirmLabel,
                        Confirm,
                        disabled: !canConfirm,
                        style: ControlStyle.Comfortable,
                        variant: ButtonVariant.Primary);
                },
            });

        DrawNavigation(rects.Band, scale);
        DrawQuick(rects.Rail, scale);
        DrawBody(rects.Body, scale);
        DrawBottomBand(rects.BottomBand, scale);
        DrawPersistentRightPanel(rects.Body, rects.BottomBand, scale);
        DrawFooterFill(rects.Footer, confirmLabel, scale);
    }

    /// <summary>
    /// Refreshes the preview only when the selected path changes.
    /// </summary>
    private void ResolvePreview()
    {
        if (string.Equals(_previewPath, _selectedPath, StringComparison.Ordinal))
            return;
        _previewPath = _selectedPath;
        _preview = _selectedPath is { } path && !_selectedIsDirectory
            ? FilePreview?.Invoke(path)
            : null;
    }

    // ── the frame's slots ────────────────────────────────────────────────

    /// <summary>
    /// Draws Back, Forward, Up, and the editable current path.
    /// </summary>
    private void DrawNavigation(WindowFrameRect band, float scale)
    {
        Theme theme = ActiveTheme;
        float inset = theme.Floating.HeaderInset * scale;
        float gap = theme.Page.ActionGap * scale;
        var square = ControlStyle.Square(NavActionSize);

        ActionBar(
            _navId,
            new Vector2(band.Min.X + inset, band.Min.Y),
            new Vector2(band.Size.X - inset * 2f, band.Size.Y),
            left =>
            {
                left.Icon(
                    TablerIcon.ArrowLeft, Back, "Go back to the previous folder",
                    _back.Count == 0, square);
                left.Icon(
                    TablerIcon.ArrowRight, Forward, "Go forward to the next folder",
                    _forward.Count == 0, square);
                left.Icon(
                    TablerIcon.ArrowUp, Up, "Open the parent folder",
                    _source.Parent(_currentPath) is null, square);
            },
            null,
            ActionBarSeparator.None);

        // Place the path field after the three navigation buttons and align its
        // right edge with the content inset below.
        float control = theme.Controls.ComfortableHeight * scale;
        float pathX = band.Min.X + inset + (NavActionSize * scale + gap) * 3f;
        float pathWidth = band.Max.X - theme.Page.Inset * scale - pathX;
        ImGui.SetCursorScreenPos(new Vector2(
            pathX, band.Min.Y + (band.Size.Y - control) * 0.5f));
        TextInput(
            _pathId,
            _pathEdit,
            next => _pathEdit = next,
            new ControlStyle
            {
                Height = UiHeight.Comfortable,
                Width = UiWidth.Fixed(MathF.Max(1f, pathWidth / scale)),
            },
            placeholder: "Path");
        // Commit when ImGui reports that the edited field was deactivated.
        if (ImGui.IsItemDeactivatedAfterEdit())
            CommitPath();
    }

    /// <summary>
    /// Draws quick-access folders above the optional rail panel.
    /// </summary>
    private void DrawQuick(WindowFrameRect rail, float scale)
    {
        Theme theme = ActiveTheme;
        float inset = theme.Page.Inset;
        WindowFrameRect list = DrawRailFooter(rail, scale);
        int picked = -1;
        // Use the scrollbar gutter as the trailing inset, without a second margin.
        ImGui.SetCursorScreenPos(list.Min + new Vector2(inset * scale));
        ScrollRegion(
            _quickId,
            list.Size.X / scale - inset,
            list.Size.Y / scale - inset * 2f,
            region =>
            {
                float width = RowWidth(region) * scale;
                float gutter = theme.Scrollbar.GutterWidth * scale;
                for (int i = 0; i < _quick.Count; i++)
                {
                    FileQuickEntry entry = _quick[i];
                    var hit = Row(
                        Ids.Join(_quickRowPrefix, entry.Path),
                        width,
                        gutter,
                        string.Equals(
                            entry.Path,
                            _currentPath,
                            StringComparison.OrdinalIgnoreCase),
                        scale);
                    float height = hit.Size.Y;
                    float glyph = theme.Controls.SmallIconSize * scale;
                    var slot = new Vector2(
                        hit.ScreenMin.X + QuickIconMargin * scale,
                        hit.ScreenMin.Y);
                    var mark = slot + new Vector2((height - glyph) * 0.5f);
                    IconIn(mark, mark + new Vector2(glyph), entry.Icon);
                    float labelX = slot.X + height;
                    RowLabel(
                        new Vector2(labelX, hit.ScreenMin.Y),
                        new Vector2(
                            hit.ScreenMax.X - gutter - labelX, height),
                        entry.Name,
                        theme.Text);
                    if (hit.Activated)
                        picked = i;
                }
            },
            edgePadding: ActiveTheme.Spacing.Two);
        // Navigate after drawing the quick-access list.
        if (picked >= 0)
            Travel(_quick[picked].Path);
    }

    /// <summary>
    /// Places the optional panel at the rail bottom, using the quick-list height to
    /// choose its share within the configured 40–60% bounds.
    /// </summary>
    private WindowFrameRect DrawRailFooter(WindowFrameRect rail, float scale)
    {
        if (RailPanel is not { } panel || !(rail.Size.Y > 0f))
            return rail;

        Theme theme = ActiveTheme;
        float rule = MathF.Max(1f, scale);
        float rows = (_quick.Count * theme.Controls.ListRowHeight
            + theme.Page.Inset * 2f) * scale;
        float height = Math.Clamp(
            rail.Size.Y - rows - rule,
            rail.Size.Y * RailFooterMinShare,
            rail.Size.Y * RailFooterMaxShare);
        float top = rail.Max.Y - height;

        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(rail.Min.X, top - rule),
            new Vector2(rail.Max.X, top),
            ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
        panel.Draw(
            new Vector2(rail.Min.X, top),
            new Vector2(rail.Size.X, height),
            SelectedFile);
        return new WindowFrameRect(
            rail.Min, new Vector2(rail.Max.X, top - rule));
    }

    /// <summary>Draws the body left of any persistent right rail.</summary>
    private void DrawBody(WindowFrameRect body, float scale)
    {
        Theme theme = ActiveTheme;
        float rule = MathF.Max(1f, scale);
        float right = ContentRight(body.Max.X, scale);

        if (_preview is { } preview)
        {
            right -= theme.FileDialog.PreviewWidth * scale + rule;
            ColumnRule(right, body, rule);
            DrawPreview(
                new WindowFrameRect(
                    new Vector2(right + rule, body.Min.Y), body.Max),
                preview,
                scale);
        }

        // Allocate columns from the right so declaration order displays left-to-right.
        for (int i = SidePanels.Count - 1; i >= 0; i--)
        {
            FileSidePanel panel = SidePanels[i];
            float column = panel.Width * scale;
            right -= column + rule;
            ColumnRule(right, body, rule);
            panel.Draw(
                new Vector2(right + rule, body.Min.Y),
                new Vector2(column, body.Size.Y),
                SelectedFile);
        }

        DrawEntries(
            new WindowFrameRect(
                body.Min, new Vector2(right, body.Max.Y)),
            scale);
    }

    /// <summary>Draws the bottom band left of any persistent right rail.</summary>
    private void DrawBottomBand(WindowFrameRect band, float scale)
    {
        if (BottomPanel is not { } panel || !(band.Size.Y > 0f))
            return;
        float rule = MathF.Max(1f, scale);
        float right = ContentRight(band.Max.X, scale);
        panel.Draw(
            new Vector2(band.Min.X, band.Min.Y + rule),
            new Vector2(right - band.Min.X, band.Size.Y - rule),
            SelectedFile);
    }

    private float ContentRight(float right, float scale) =>
        PersistentRightPanel is { } panel
            ? right - panel.Width * scale - MathF.Max(1f, scale)
            : right;

    private void DrawPersistentRightPanel(
        WindowFrameRect body, WindowFrameRect bottom, float scale)
    {
        if (PersistentRightPanel is not { } panel)
            return;

        float rule = MathF.Max(1f, scale);
        float left = ContentRight(body.Max.X, scale);
        float top = body.Min.Y;
        float bottomY = MathF.Max(body.Max.Y, bottom.Max.Y);
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(left, top),
            new Vector2(left + rule, bottomY),
            ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
        panel.Draw(
            new Vector2(left + rule, top),
            new Vector2(body.Max.X - left - rule, bottomY - top),
            SelectedFile);
    }

    /// <summary>
    /// Draws a vertical separator across the body height.
    /// </summary>
    private static void ColumnRule(
        float x, WindowFrameRect body, float rule) =>
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(x, body.Min.Y),
            new Vector2(x + rule, body.Max.Y),
            ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));

    /// <summary>
    /// Draws fixed search/sort controls above the scrolling folder listing.
    /// The scrollbar gutter supplies the listing's right margin.
    /// </summary>
    private void DrawEntries(WindowFrameRect body, float scale)
    {
        Theme theme = ActiveTheme;
        float inset = theme.Page.Inset;
        int picked = -1;
        bool second = false;
        // Align glyph centres, not the search field's invisible padding.
        float searchLeft = inset + EntryIconSlot * 0.5f
            - theme.Controls.InputPaddingX - theme.Controls.SmallIconSize * 0.5f;
        float searchHeight = theme.Controls.WorkspaceHeight;
        float sortWidth = 110f;
        float sortGap = theme.Page.ActionGap;
        ImGui.SetCursorScreenPos(body.Min + new Vector2(searchLeft, inset) * scale);
        FilterPill(_searchId, _search, SetSearch, "Search",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(MathF.Max(1f,
                    body.Size.X / scale - searchLeft - theme.Scrollbar.GutterWidth
                    - sortWidth - sortGap)),
            });
        ImGui.SetCursorScreenPos(new Vector2(
            body.Max.X - (theme.Scrollbar.GutterWidth + sortWidth) * scale,
            body.Min.Y + inset * scale));
        Dropdown(
            $"{_id}-sort",
            SortOptions,
            (int)_sort,
            _setSort,
            ControlStyle.Workspace with { Width = UiWidth.Fixed(sortWidth) },
            help: "Sort files and folders");
        float listTop = inset + searchHeight + theme.Spacing.Four;
        ImGui.SetCursorScreenPos(body.Min + new Vector2(inset, listTop) * scale);
        ScrollRegion(
            _entriesId,
            body.Size.X / scale - inset,
            MathF.Max(1f, body.Size.Y / scale - listTop - inset),
            region =>
            {
                if (_resetEntriesScroll)
                {
                    ImGui.SetScrollY(0f);
                    _resetEntriesScroll = false;
                }
                if (_lastError is { } error)
                {
                    Status(region, error, theme.Danger, scale);
                    return;
                }

                if (!_entries.Exists(entry => MatchesSearch(entry.Name)))
                {
                    Status(
                        region, _nameFilter.Length == 0
                            ? "This folder is empty." : "No matching files or folders.",
                        FormHintColor, scale);
                    return;
                }

                float width = RowWidth(region) * scale;
                float gutter = theme.Scrollbar.GutterWidth * scale;
                for (int i = 0; i < _entries.Count; i++)
                {
                    FileListingEntry entry = _entries[i];
                    if (!MatchesSearch(entry.Name))
                        continue;
                    var hit = Row(
                        Ids.Join(_entryRowPrefix, entry.FullPath),
                        width,
                        gutter,
                        string.Equals(
                            entry.FullPath,
                            _selectedPath,
                            StringComparison.OrdinalIgnoreCase),
                        scale);
                    float height = hit.Size.Y;
                    float glyph = theme.Controls.SmallIconSize * scale;
                    var mark = hit.ScreenMin + new Vector2(
                        (EntryIconSlot * scale - glyph) * 0.5f,
                        (height - glyph) * 0.5f);
                    IconIn(
                        mark,
                        mark + new Vector2(glyph),
                        entry.IsDirectory
                            ? TablerIcon.Folder
                            : TablerIcon.FileText);

                    // Keep the modified date before the scrollbar gutter and row padding.
                    float readoutWidth = ModifiedColumnWidth * scale;
                    float readoutX = hit.ScreenMax.X - gutter
                        - theme.Spacing.Four * scale - readoutWidth;
                    float labelX = hit.ScreenMin.X + EntryIconSlot * scale;
                    RowLabel(
                        new Vector2(labelX, hit.ScreenMin.Y),
                        new Vector2(
                            readoutX - theme.Spacing.Two * scale - labelX,
                            height),
                        entry.Name,
                        theme.Text);
                    TextInBand(
                        new Vector2(readoutX, hit.ScreenMin.Y),
                        new Vector2(readoutWidth, height),
                        Modified(entry.Modified),
                        new TextStyle
                        {
                            Size = theme.Typography.CaptionSize,
                            Family = FontFamily.Mono,
                            Color = FormLabelColor,
                        },
                        TextAlign.End);

                    bool singleClick = SingleClickActivation && !_isSaveMode;
                    if (!hit.Activated && !(hit.DoubleClicked && !singleClick))
                        continue;
                    picked = i;
                    second = singleClick || hit.DoubleClicked;
                }
            },
            edgePadding: ActiveTheme.Spacing.Two);
        if (picked >= 0)
            PickEntry(picked, second);
    }

    /// <summary>
    /// Fits the preview image without cropping and reserves space for its caption.
    /// </summary>
    private static void DrawPreview(
        WindowFrameRect column, in FilePreviewResult preview, float scale)
    {
        Theme theme = ActiveTheme;
        float inset = theme.Page.Inset * scale;
        var min = column.Min + new Vector2(inset);
        var max = column.Max - new Vector2(inset);
        bool captioned = !string.IsNullOrEmpty(preview.Caption);
        float caption = captioned ? PreviewCaptionHeight * scale : 0f;
        var box = new Vector2(max.X - min.X, max.Y - min.Y - caption);
        var drawList = ImGui.GetWindowDrawList();

        if (preview.Texture != 0
            && box.X > 0f && box.Y > 0f
            && preview.Size.X > 0f && preview.Size.Y > 0f)
        {
            float fit = MathF.Min(
                box.X / preview.Size.X, box.Y / preview.Size.Y);
            Vector2 size = preview.Size * fit;
            Vector2 imageMin = theme.Optical.Snap(
                min + (box - size) * 0.5f);
            drawList.AddImage(
                new ImTextureID(preview.Texture),
                imageMin,
                imageMin + size,
                Vector2.Zero,
                Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One));
        }

        if (!captioned)
            return;
        TextInBand(
            new Vector2(min.X, max.Y - caption),
            new Vector2(max.X - min.X, caption),
            preview.Caption!,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = FormHintColor,
            },
            TextAlign.Center);
    }

    /// <summary>
    /// Draws the save filename or selected-file label before Cancel and confirmation.
    /// </summary>
    private void DrawFooterFill(
        WindowFrameRect footer, string confirmLabel, float scale)
    {
        Theme theme = ActiveTheme;
        float inset = theme.Floating.HeaderInset * scale;
        float gap = theme.Page.ActionGap * scale;
        float x = footer.Min.X + inset + gap;
        float actions =
            MeasureButton("Cancel", ControlStyle.Comfortable).X
            + gap
            + MeasureButton(confirmLabel, ControlStyle.Comfortable).X;
        float width = MathF.Max(
            0f, footer.Max.X - inset - actions - gap - x);

        if (_isSaveMode)
        {
            float control = theme.Controls.ComfortableHeight * scale;
            ImGui.SetCursorScreenPos(new Vector2(
                x, footer.Min.Y + (footer.Size.Y - control) * 0.5f));
            TextInput(
                _nameId,
                _fileName,
                next => _fileName = next,
                new ControlStyle
                {
                    Height = UiHeight.Comfortable,
                    Width = UiWidth.Fixed(MathF.Max(1f, width / scale)),
                },
                placeholder: "File name");
            return;
        }

        if (!(width > 0f))
            return;
        string text = SelectedFile is { } chosen
            ? Path.GetFileName(chosen)
            : "No file selected";
        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = SelectedFile is null ? FormHintColor : FormValueColor,
        };
        Fitted(
            new Vector2(x, footer.Min.Y),
            new Vector2(width, footer.Size.Y),
            text,
            style,
            besideIcon: false);
    }

    // ── row primitives ───────────────────────────────────────────────────

    /// <summary>
    /// Row hit width, including the scrollbar gutter. Painted content excludes the gutter.
    /// </summary>
    private static float RowWidth(ScrollRegionScope region) =>
        region.ContentWidth + ActiveTheme.Scrollbar.GutterWidth;

    /// <summary>
    /// Reserves a row with no vertical gap and paints its highlight before the scrollbar gutter.
    /// </summary>
    private static InteractionResult Row(
        string id, float width, float gutter, bool selected, float scale)
    {
        Theme theme = ActiveTheme;
        float height = theme.Controls.ListRowHeight * scale;
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        var hit = Interactive.Reserve(
            id,
            new Vector2(MathF.Max(1f, width), height),
            disabled: false);
        ImGui.PopStyleVar();

        var fill = selected
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                new Vector2(hit.ScreenMax.X - gutter, hit.ScreenMax.Y),
                ImGui.ColorConvertFloat4ToU32(fill),
                RowPillRadius * scale);
        return hit;
    }

    /// <summary>A row's name, seated against the mark beside it.</summary>
    private static void RowLabel(
        Vector2 min, Vector2 band, string text, Vector4 color) =>
        Fitted(
            min,
            band,
            text,
            new TextStyle
            {
                Size = ActiveTheme.Typography.BodySize,
                Color = color,
            },
            besideIcon: true);

    /// <summary>
    /// Centers text vertically and applies truncation only if it exceeds the available width.
    /// </summary>
    private static void Fitted(
        Vector2 min, Vector2 band, string text, in TextStyle style,
        bool besideIcon)
    {
        if (!(band.X > 0f))
            return;
        if (MeasureText(text, style).X <= band.X)
            TextInBand(min, band, text, style, TextAlign.Start, besideIcon);
        else
            TextInBand(
                min, band, text, style, TextConstraint.Truncate(band.X),
                TextAlign.Start, besideIcon);
    }

    /// <summary>
    /// Draws the empty-folder or read-error message in place of listing rows.
    /// </summary>
    private static void Status(
        ScrollRegionScope region, string text, Vector4 color, float scale)
    {
        Theme theme = ActiveTheme;
        var band = new Vector2(
            region.ContentWidth * scale,
            theme.Controls.ListRowHeight * scale);
        Fitted(
            ImGui.GetCursorScreenPos(),
            band,
            text,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = color,
            },
            besideIcon: false);
        ImGui.Dummy(band);
    }

    private string? SelectedFile =>
        _selectedIsDirectory ? null : _selectedPath;

    /// <summary>
    /// Modified-date display shared by file-browser and library rows.
    /// </summary>
    private const string ModifiedFormat = "yy-MM-dd HH:mm";

    private static string Modified(DateTime stamp) =>
        stamp == default
            ? "—"
            : stamp.ToString(
                ModifiedFormat,
                System.Globalization.CultureInfo.InvariantCulture);

    // ── dispatch ─────────────────────────────────────────────────────────

    private void Close() => _open = false;

    private void PickEntry(int index, bool second)
    {
        if ((uint)index >= (uint)_entries.Count)
            return;
        FileListingEntry entry = _entries[index];
        _selectedPath = entry.FullPath;
        _selectedIsDirectory = entry.IsDirectory;
        if (!entry.IsDirectory && _isSaveMode)
            _fileName = entry.Name;
        if (!second)
            return;

        if (entry.IsDirectory)
            Travel(entry.FullPath);
        else if (!_isSaveMode)
            Confirm();
    }

    /// <summary>
    /// Navigates to a different folder, adding the old path to Back and clearing Forward.
    /// </summary>
    private void Travel(string path)
    {
        if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (_currentPath.Length > 0)
            _back.Add(_currentPath);
        _forward.Clear();
        NavigateTo(path);
    }

    private void Back()
    {
        if (_back.Count == 0)
            return;
        string target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_currentPath.Length > 0)
            _forward.Add(_currentPath);
        NavigateTo(target);
    }

    private void Forward()
    {
        if (_forward.Count == 0)
            return;
        string target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_currentPath.Length > 0)
            _back.Add(_currentPath);
        NavigateTo(target);
    }

    private void Up()
    {
        if (_source.Parent(_currentPath) is { } parent)
            Travel(parent);
    }

    /// <summary>The path editor's commit. A draft that does not name a
    /// folder is left alone — the field keeps what was typed, and the
    /// listing keeps what it had.</summary>
    private void CommitPath()
    {
        string next = _pathEdit.Trim();
        if (next.Length == 0 || !_source.DirectoryExists(next))
            return;
        Travel(next);
    }

    private void Confirm()
    {
        string path;
        if (_isSaveMode)
        {
            string name = _fileName.Trim();
            if (name.Length == 0)
                return;
            if (_extensions.Length > 0 && !HasKnownExtension(name))
                name += _extensions[0];
            path = Path.Combine(_currentPath, name);
        }
        else
        {
            if (SelectedFile is not { } selected)
                return;
            path = selected;
        }

        // Deliver the callback after closing, so it can open another dialog.
        _open = false;
        _pendingSelect = path;
    }

    private bool HasKnownExtension(string name)
    {
        for (int i = 0; i < _extensions.Length; i++)
            if (name.EndsWith(_extensions[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void NavigateTo(string path)
    {
        _search = _nameFilter = string.Empty;
        _resetEntriesScroll = true;
        _currentPath = path;
        _pathEdit = path;
        _selectedPath = null;
        _selectedIsDirectory = false;
        RefreshEntries();
    }

    private bool MatchesSearch(string name) =>
        name.Contains(_nameFilter, StringComparison.OrdinalIgnoreCase);

    private void SetSearch(string value)
    {
        _search = value;
        _nameFilter = value.Trim();
        _resetEntriesScroll = true;
        if (_selectedPath is not { } selected || MatchesSearch(Path.GetFileName(selected)))
            return;
        if (_isSaveMode && string.Equals(_fileName, Path.GetFileName(selected),
                StringComparison.OrdinalIgnoreCase))
            _fileName = string.Empty;
        _selectedPath = null;
        _selectedIsDirectory = false;
        _preview = null;
        _previewPath = null;
    }

    private void RefreshEntries()
    {
        _entries.Clear();
        _scratch.Clear();
        _lastError = null;
        try
        {
            _source.Enumerate(_currentPath, _scratch);
        }
        catch (Exception ex)
        {
            _lastError = $"This folder could not be read: {ex.Message}";
            return;
        }

        for (int i = 0; i < _scratch.Count; i++)
        {
            FileListingEntry entry = _scratch[i];
            if (!entry.IsDirectory
                && _extensions.Length > 0
                && !MatchesFilter(entry.Name))
                continue;
            _entries.Add(entry);
        }

        _entries.Sort(_compareEntries);
    }

    private bool MatchesFilter(string name)
    {
        string extension = Path.GetExtension(name);
        for (int i = 0; i < _extensions.Length; i++)
            if (string.Equals(
                    extension, _extensions[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>
/// Reads local folders and files, omitting Hidden entries. Sorting and extension
/// filtering belong to FileDialog; hidden entries still exist on disk.
/// </summary>
internal sealed class LocalFileListing : IFileListingSource
{
    internal static readonly LocalFileListing Instance = new();

    public string DefaultPath =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? Parent(string path) =>
        path.Length == 0 ? null : Directory.GetParent(path)?.FullName;

    public void Enumerate(string path, List<FileListingEntry> into)
    {
        foreach (string directory in Directory.GetDirectories(path))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.Hidden) != 0)
                continue;
            into.Add(new FileListingEntry(
                info.Name, directory, IsDirectory: true, info.LastWriteTime));
        }

        foreach (string file in Directory.GetFiles(path))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.Hidden) != 0)
                continue;
            into.Add(new FileListingEntry(
                info.Name, file, IsDirectory: false, info.LastWriteTime));
        }
    }

    public void QuickAccess(List<FileQuickEntry> into)
    {
        void AddSpecial(
            string name, Environment.SpecialFolder folder, TablerIcon icon)
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                into.Add(new FileQuickEntry(name, path, icon));
        }

        AddSpecial("Desktop", Environment.SpecialFolder.Desktop, TablerIcon.DeviceDesktop);
        AddSpecial("Documents", Environment.SpecialFolder.MyDocuments, TablerIcon.FileText);
        AddSpecial("Pictures", Environment.SpecialFolder.MyPictures, TablerIcon.Photo);
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        if (Directory.Exists(downloads))
            into.Add(new FileQuickEntry("Downloads", downloads, TablerIcon.Download));
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
                if (drive.IsReady)
                    into.Add(new FileQuickEntry(
                        drive.Name,
                        drive.RootDirectory.FullName,
                        TablerIcon.Stack2));
        }
        catch
        {
            // Unreadable drive enumeration must not break the dialog.
        }
    }
}
