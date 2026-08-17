using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// What a preview provider answers with. The dialog owns no rendering: a
/// provider resolves the selection into a host texture and its natural size,
/// and the panel aspect-fits that into whatever column the frame left it.
/// A provider that has nothing to show answers <c>null</c>, and the column
/// does not exist that frame.
/// </summary>
public readonly record struct FilePreviewResult(
    nint Texture, Vector2 Size, string? Caption);

/// <summary>
/// One caller-drawn column right of the file list. The dialog owns the
/// geometry and nothing else: the panel is handed its box in screen space and
/// the full path the list is HIGHLIGHTING — null for a folder row, and for no
/// selection at all — and whatever it draws there is its own business,
/// scrolling included.
/// </summary>
/// <param name="Width">Logical column width. Unlike
/// <see cref="Crystarium.FileDialog.FilePreview"/>, which steals width from the
/// listing, this is ADDED to the dialog: the browser keeps its own width
/// whatever a consumer bolts on beside it.</param>
public readonly record struct FileSidePanel(
    float Width, Action<Vector2, Vector2, string?> Draw);

/// <summary>One listing row, as the dialog sees it.</summary>
internal readonly record struct FileListingEntry(
    string Name, string FullPath, bool IsDirectory, DateTime Modified);

/// <summary>One quick-menu destination.</summary>
internal readonly record struct FileQuickEntry(
    string Name, string Path, TablerIcon Icon);

/// <summary>
/// THE DIALOG'S VIEW OF THE DISK, and the only one. Ordering, extension
/// filtering and the error line are the DIALOG's policy and stay above this
/// line; hidden-attribute suppression is the filesystem's own and stays below.
/// </summary>
internal interface IFileListingSource
{
    /// <summary>Fills <paramref name="into"/> with the folder's contents, in
    /// no particular order. Throwing is the contract for an unreadable folder:
    /// the dialog turns the message into its error line.</summary>
    void Enumerate(string path, List<FileListingEntry> into);

    void QuickAccess(List<FileQuickEntry> into);

    bool DirectoryExists(string path);

    string? Parent(string path);

    string DefaultPath { get; }
}

public static partial class Crystarium
{
    /// <summary>
    /// The file surface: the shared <see cref="WindowFrame"/> is the whole
    /// chassis — chrome, title bar, navigation band, quick rail and footer —
    /// and this fills the rectangles it hands back. A navigation callback is
    /// invoked only after the dialog has closed.
    ///
    /// <para>THE PUBLIC SHAPE IS FROZEN: the constructor, <see cref="Open"/>
    /// and <see cref="Draw"/> are what four call sites already speak.</para>
    /// </summary>
    public sealed class FileDialog
    {
        /// <summary>The navigation band's affordance size — the window
        /// chassis' own close-action square, so back/forward/up read as one
        /// family with the title bar's X above them.</summary>
        private const float NavActionSize = 24f;

        /// <summary>The modified column. Wide enough for
        /// <see cref="ModifiedFormat"/> in the mono readout face, which is the
        /// one string it ever holds.</summary>
        private const float ModifiedColumnWidth = 92f;

        /// <summary>A row's leading mark, on the list-row band.</summary>
        private const float EntryIconSlot = 22f;

        /// <summary>A rail row's glyph slot: a 2px left margin, then a
        /// row-height square; the label starts where it ends.</summary>
        private const float QuickIconMargin = 2f;

        /// <summary>The row highlight's corner.</summary>
        private const float RowPillRadius = 5f;

        /// <summary>The preview panel's caption band.</summary>
        private const float PreviewCaptionHeight = 20f;

        /// <summary>
        /// What a <see cref="RailPanel"/> may take of the rail, at least and at
        /// most. The panel is handed everything the quick list does not need,
        /// held between these: below the floor a real options stack is unusable,
        /// and above the ceiling the destinations it sits under are crushed —
        /// both ends scroll inside their own region rather than growing.
        /// </summary>
        private const float RailFooterMinShare = 0.4f;
        private const float RailFooterMaxShare = 0.6f;

        private static readonly Comparison<FileListingEntry> ByKind =
            static (left, right) =>
            {
                if (left.IsDirectory != right.IsDirectory)
                    return left.IsDirectory ? -1 : 1;
                return string.Compare(
                    left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            };

        private readonly string _title;
        private readonly string[] _extensions;
        private readonly bool _isSaveMode;
        private readonly string _id;

        /// <summary>The dialog's fixed identities, minted ONCE with the
        /// instance id they hang off — a surface that re-interpolates its ids
        /// allocates for every frame it stays open.</summary>
        private readonly string _surfaceId;
        private readonly string _navId;
        private readonly string _pathId;
        private readonly string _quickId;
        private readonly string _quickRowPrefix;
        private readonly string _entriesId;
        private readonly string _entryRowPrefix;
        private readonly string _nameId;

        private readonly List<FileQuickEntry> _quick = new();
        private readonly List<FileListingEntry> _entries = new();
        private readonly List<FileListingEntry> _scratch = new();

        /// <summary>The history stack pair. BACK holds the folders walked away
        /// from, newest last; FORWARD holds the ones walked back past, and is
        /// cleared by any navigation that is not a step through it — which is
        /// the whole of the browser rule.</summary>
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

        /// <summary>The draft the path editor holds. It is the FIELD's value
        /// every frame; committing it is what navigates.</summary>
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
        }

        /// <summary>
        /// The preview seam. The dialog knows nothing about what a pose or a
        /// character file LOOKS like — the game-side provider renders it — so
        /// the surface asks for a texture whenever the selection changes and
        /// grows its right column only while one comes back. A dialog with no
        /// provider never shows the column at all.
        /// </summary>
        public Func<string, FilePreviewResult?>? FilePreview;

        /// <summary>
        /// The caller's own columns, left to right, right of the file list.
        /// They are part of the dialog's SIZE — the window opens wider by
        /// exactly what they ask for — and part of its chrome: each stands
        /// behind the same rule the frame bridges its rail with. Stated once
        /// by the consumer; the dialog never mutates the list.
        /// </summary>
        public readonly List<FileSidePanel> SidePanels = new();

        /// <summary>Caller content that spans the body and bottom band.</summary>
        public FileSidePanel? PersistentRightPanel;

        /// <summary>Caller actions placed before Cancel in the footer.</summary>
        public Action<Crystarium.ActionBarScope>? FooterBeforeCancel;

        /// <summary>
        /// The caller's own panel UNDER the quick-access list, filling the rest
        /// of the navigation rail. Its <see cref="FileSidePanel.Width"/> is a
        /// MINIMUM rail width rather than a column of its own — the rail widens
        /// to it, and the dialog widens by the difference — and its draw is
        /// handed the box in screen space exactly as a side panel's is, content
        /// inset and scrolling included.
        /// </summary>
        public FileSidePanel? RailPanel;

        /// <summary>
        /// The caller's own full-width band UNDER the columns region and above
        /// the footer. <see cref="FileSidePanel.Width"/> is reinterpreted as
        /// the band's logical HEIGHT — the dialog opens taller by exactly that
        /// plus the band's own top rule, the columns keep their height, and
        /// the draw is handed the band's box in screen space exactly as a side
        /// panel's is.
        /// </summary>
        public FileSidePanel? BottomPanel;

        /// <summary>
        /// Logical height ADDED to the dialog's window, all of it going to the
        /// columns region — a consumer whose preview column wants more body
        /// than the theme's default states it here. Zero for every plain
        /// dialog.
        /// </summary>
        public float ExtraHeight;

        /// <summary>Caller-specific width adjustment for this dialog.</summary>
        public float WidthAdjustment;

        /// <summary>Caller-specific height adjustment for this dialog.</summary>
        public float HeightAdjustment;

        public bool IsOpen => _open;

        /// <summary>What the panels add to the dialog's width: the columns and
        /// the rule each one stands behind, logical.</summary>
        private float PanelWidth()
        {
            float total = 0f;
            for (int i = 0; i < SidePanels.Count; i++)
                total += SidePanels[i].Width + 1f;
            if (PersistentRightPanel is { } persistent)
                total += persistent.Width + 1f;
            return total;
        }

        /// <summary>The rail's logical width, rule included: its own, or the
        /// footer panel's when that asks for more.</summary>
        private float RailWidth() => MathF.Max(
            Crystarium.ActiveTheme.FileDialog.RailWidth,
            RailPanel?.Width ?? 0f);

        /// <summary>What the widened rail adds to the dialog — the browser
        /// keeps its own width whatever the rail carries, exactly as a side
        /// panel's column does.</summary>
        private float RailExtra() =>
            RailWidth() - Crystarium.ActiveTheme.FileDialog.RailWidth;

        /// <summary>What the bottom band adds to the dialog's height: the
        /// band's own height plus its opening rule, logical. The columns keep
        /// their height whatever the consumer bolts on under them.</summary>
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
                FloatingSurface.Window(
                    SurfaceId,
                    ref _open,
                    Crystarium.ActiveTheme.FileDialog.Width
                        + PanelWidth() + RailExtra() + WidthAdjustment,
                    Crystarium.ActiveTheme.FileDialog.Height
                        + ExtraHeight + BottomExtra() + HeightAdjustment,
                    DrawFrame);

            if (!_open && _pendingSelect is { } chosen)
            {
                _pendingSelect = null;
                _onSelect?.Invoke(chosen);
            }
        }

        /// <summary>
        /// Points the dialog at a folder and clears everything a session owns.
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
            Theme theme = Crystarium.ActiveTheme;
            float scale = ImGuiHelpers.GlobalScale;
            ResolvePreview();

            bool canConfirm = _isSaveMode
                ? _fileName.Trim().Length > 0
                : SelectedFile is not null;
            string confirmLabel = _isSaveMode ? "Save" : "Load";

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

        /// <summary>Asking the provider is a SELECTION-change edge, not a frame
        /// one: the column would otherwise cost a host render every frame.
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
        /// The navigation band: the history pair, the parent step, and the path
        /// editor filling everything they leave. History wears PLAIN left/right
        /// arrows — the undo pair means edit history, not travel (user).
        /// </summary>
        private void DrawNavigation(WindowFrameRect band, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
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

            // The editor takes the band's whole middle: past the three actions,
            // ending at the PAGE inset — the same trailing inset the preview
            // box and every column's content stand behind, so the field's
            // right edge lines up with the panels below it (user round:
            // the header inset left it hanging short).
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
            // Committing is leaving the field having edited it — Enter
            // deactivates a native InputText, so one test covers both the
            // keypress and the click away.
            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitPath();
        }

        /// <summary>The rail's content: the frame owns the band and its rule,
        /// this owns the inset, the rows, and the split when a footer panel
        /// stands under them.</summary>
        private void DrawQuick(WindowFrameRect rail, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
            float inset = theme.Page.Inset;
            WindowFrameRect list = DrawRailFooter(rail, scale);
            int picked = -1;
            // The region reaches the rail's right edge so the bar sits
            // guttered there — the gutter is the rows' trailing inset, not a
            // second right margin (the library rail's contract).
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
                });
            // Applied after the region closes: travelling refills the listing
            // the loop is walking.
            if (picked >= 0)
                Travel(_quick[picked].Path);
        }

        /// <summary>
        /// Seats <see cref="RailPanel"/> at the BOTTOM of the rail and returns
        /// what is left for the quick list. The panel takes everything the
        /// destinations do not need — they are a known count of rows, so the
        /// split is measured, not guessed — held between the two shares so
        /// neither side can crush the other; both scroll inside their own box.
        /// The seam is the frame's own rule, run the rail's full width.
        /// </summary>
        private WindowFrameRect DrawRailFooter(WindowFrameRect rail, float scale)
        {
            if (RailPanel is not { } panel || !(rail.Size.Y > 0f))
                return rail;

            Theme theme = Crystarium.ActiveTheme;
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
            Theme theme = Crystarium.ActiveTheme;
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

            // Carved right to left, so the FIRST panel lands nearest the
            // listing: a consumer's declaration order reads left to right.
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

        /// <summary>A column's left edge — the same rule the frame bridges its
        /// rail with, run the body's full height.</summary>
        private static void ColumnRule(
            float x, WindowFrameRect body, float rule) =>
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(x, body.Min.Y),
                new Vector2(x + rule, body.Max.Y),
                ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));

        /// <summary>
        /// The explorer. NO right padding on the region: the bar sits on the
        /// window edge and IS the right inset; the row box bleeds under it
        /// while the pill and its content stop a gutter early.
        /// </summary>
        private void DrawEntries(WindowFrameRect body, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
            float inset = theme.Page.Inset;
            int picked = -1;
            bool second = false;
            ImGui.SetCursorScreenPos(body.Min + new Vector2(inset * scale));
            ScrollRegion(
                _entriesId,
                body.Size.X / scale - inset,
                body.Size.Y / scale - inset * 2f,
                region =>
                {
                    if (_lastError is { } error)
                    {
                        Status(region, error, theme.Danger, scale);
                        return;
                    }

                    if (_entries.Count == 0)
                    {
                        Status(
                            region, "This folder is empty.",
                            FormHintColor, scale);
                        return;
                    }

                    float width = RowWidth(region) * scale;
                    float gutter = theme.Scrollbar.GutterWidth * scale;
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        FileListingEntry entry = _entries[i];
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

                        // The date breathes off the pill's right edge, which
                        // itself stops a gutter early — the library rail's
                        // badge math, so the readout never rides the bar.
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

                        if (!hit.Activated && !hit.DoubleClicked)
                            continue;
                        picked = i;
                        second = hit.DoubleClicked;
                    }
                });
            if (picked >= 0)
                PickEntry(picked, second);
        }

        /// <summary>
        /// The preview column. The image is aspect-fitted into whatever the
        /// caption leaves: the host renders a portrait or a landscape and the
        /// column has to seat either without cropping.
        /// </summary>
        private static void DrawPreview(
            WindowFrameRect column, in FilePreviewResult preview, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
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
        /// The footer's left slot STRETCHES: the name a save is about, or the
        /// name a load has chosen, reaching the actions. It starts one action
        /// gap past the header inset, where the bar's empty left cluster ends.
        /// </summary>
        private void DrawFooterFill(
            WindowFrameRect footer, string confirmLabel, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
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

        /// <summary>The row's own box, which is the region's FULL width: the
        /// gutter is the row's TRAILING inset, so the box bleeds under the bar
        /// while the pill and its content stop a gutter early — the
        /// ShellSidebar/library-rail contract.</summary>
        private static float RowWidth(ScrollRegionScope region) =>
            region.ContentWidth + Crystarium.ActiveTheme.Scrollbar.GutterWidth;

        /// <summary>
        /// One list row: the reserve at the FULL row width, the highlight
        /// stopping <paramref name="gutter"/> early so the pill's right edge
        /// stays visible beside the scroll bar. Rows stack flush at the row
        /// height — the ambient vertical spacing is the surrounding flow's,
        /// not the list's.
        /// </summary>
        private static InteractionResult Row(
            string id, float width, float gutter, bool selected, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
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
                    Size = Crystarium.ActiveTheme.Typography.BodySize,
                    Color = color,
                },
                besideIcon: true);

        /// <summary>Band-centered text, constrained ONLY on overflow: the
        /// truncate clip's snapped edge shaves a fitting run's descender
        /// otherwise.</summary>
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

        /// <summary>The listing's stand-in line — an unreadable folder's
        /// message or an empty one's — on a row-shaped band.</summary>
        private static void Status(
            ScrollRegionScope region, string text, Vector4 color, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
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

        /// <summary>The modified column's one string. Crystarium cannot see
        /// the app's <c>LibraryStamp</c> — it references nothing — so the
        /// format is restated here; it must stay identical, a browser row and
        /// a library tile show the same fact about the same file.</summary>
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

        /// <summary>A navigation the USER asked for: it remembers where it came
        /// from and forfeits whatever was ahead.</summary>
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

            // Close BEFORE the callback, exactly as the imperative dialog did:
            // a consumer that opens the second dialog from the first's callback
            // must not be fighting a window that is still up.
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
            _currentPath = path;
            _pathEdit = path;
            _selectedPath = null;
            _selectedIsDirectory = false;
            RefreshEntries();
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

            _entries.Sort(ByKind);
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
    /// The real disk. The hidden-attribute suppression lives here because it is
    /// the FILESYSTEM's notion of what exists; everything the dialog decides —
    /// order, extension filter, the error line — is above the seam.
    /// </summary>
    private sealed class LocalFileListing : IFileListingSource
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
}
