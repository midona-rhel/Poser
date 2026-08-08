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

/// <summary>One listing row, as the dialog sees it.</summary>
internal readonly record struct FileListingEntry(
    string Name, string FullPath, bool IsDirectory, DateTime Modified);

/// <summary>One quick-menu destination.</summary>
internal readonly record struct FileQuickEntry(
    string Name, string Path, TablerIcon Icon);

/// <summary>
/// THE DIALOG'S VIEW OF THE DISK, and the only one. Everything the frame draws
/// is what this seam reported, so a conformance fixture injects a fake listing
/// and the capture performs no filesystem I/O at all. Ordering, extension
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
        /// <c>yyyy-MM-dd HH:mm</c> in the mono readout face, which is the one
        /// string it ever holds.</summary>
        private const float ModifiedColumnWidth = 104f;

        /// <summary>A row's leading mark, on the list-row band.</summary>
        private const float EntryIconSlot = 22f;

        /// <summary>A rail row's glyph slot: a 2px left margin, then a
        /// row-height square; the label starts where it ends.</summary>
        private const float QuickIconMargin = 2f;

        /// <summary>The row highlight's corner.</summary>
        private const float RowPillRadius = 5f;

        /// <summary>The preview panel's caption band.</summary>
        private const float PreviewCaptionHeight = 20f;

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

        /// <summary>The listing seam. Defaulted to the real filesystem; a
        /// fixture replaces it before the first <see cref="Rehome"/>.</summary>
        internal IFileListingSource Source = LocalFileListing.Instance;

        public FileDialog(
            string title,
            string[] extensions,
            bool isSaveMode = false)
        {
            _title = title;
            _extensions = extensions;
            _isSaveMode = isSaveMode;
            _id = $"##file-dialog-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// The preview seam. The dialog knows nothing about what a pose or a
        /// character file LOOKS like — the game-side provider renders it — so
        /// the surface asks for a texture whenever the selection changes and
        /// grows its right column only while one comes back. A dialog with no
        /// provider never shows the column at all.
        /// </summary>
        public Func<string, FilePreviewResult?>? FilePreview;

        public bool IsOpen => _open;

        private string SurfaceId => $"{_title}{_id}";

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
                    Crystarium.ActiveTheme.FileDialog.Width,
                    Crystarium.ActiveTheme.FileDialog.Height,
                    DrawFrame);

            if (!_open && _pendingSelect is { } chosen)
            {
                _pendingSelect = null;
                _onSelect?.Invoke(chosen);
            }
        }

        /// <summary>
        /// Points the dialog at a folder and clears everything a session owns.
        /// Split out of <see cref="Open"/> because a fixture wants the state
        /// without the window claim: claiming the exclusive chain for a surface
        /// nobody draws would occlude the rest of a capture.
        /// </summary>
        internal void Rehome(string initialPath)
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
                Source.QuickAccess(_quick);
            if (string.IsNullOrEmpty(initialPath)
                || !Source.DirectoryExists(initialPath))
                initialPath = Source.DefaultPath;
            NavigateTo(initialPath);
        }

        private void DrawFrame(FloatingSurfaceFrame frame) =>
            RenderFrame(frame.Min, frame.Size, hostPaintsChrome: true);

        /// <summary>
        /// The frame, rendered into a caller-owned box. The window is the
        /// PRODUCT's host; the chassis has to be inspectable without one, which
        /// is what the conformance fixture calls.
        /// </summary>
        /// <param name="hostPaintsChrome"><c>FloatingSurface.Window</c> already
        /// draws the glass for every window it hosts, so the product path tells
        /// the frame not to draw a second shadow over the first.</param>
        internal void RenderFrame(
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
                    RailWidth = theme.FileDialog.RailWidth,
                    BandHeight = theme.Floating.ModalBarHeight,
                    HostPaintsChrome = hostPaintsChrome,
                    FooterRight = right =>
                    {
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
                $"{_id}-nav",
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
                        Source.Parent(_currentPath) is null, square);
                },
                null,
                ActionBarSeparator.None);

            // The editor takes the band's whole middle: past the three actions,
            // stopping one gap short of the trailing inset — where the bar's
            // (empty) right cluster stands.
            float control = theme.Controls.ComfortableHeight * scale;
            float pathX = band.Min.X + inset + (NavActionSize * scale + gap) * 3f;
            float pathWidth = band.Max.X - inset - gap - pathX;
            ImGui.SetCursorScreenPos(new Vector2(
                pathX, band.Min.Y + (band.Size.Y - control) * 0.5f));
            TextInput(
                $"{_id}-path",
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
        /// this owns the inset and the rows.</summary>
        private void DrawQuick(WindowFrameRect rail, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
            float inset = theme.Page.Inset;
            int picked = -1;
            ImGui.SetCursorScreenPos(rail.Min + new Vector2(inset * scale));
            ScrollRegion(
                $"{_id}-quick",
                rail.Size.X / scale - inset * 2f,
                rail.Size.Y / scale - inset * 2f,
                region =>
                {
                    float width = RowWidth(region) * scale;
                    for (int i = 0; i < _quick.Count; i++)
                    {
                        FileQuickEntry entry = _quick[i];
                        var hit = Row(
                            $"{_id}-quick-{entry.Path}",
                            width,
                            region.ContentWidth * scale,
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
                            new Vector2(hit.ScreenMax.X - labelX, height),
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

        /// <summary>The body slot: the explorer, and the preview column that
        /// exists only while the provider answered.</summary>
        private void DrawBody(WindowFrameRect body, float scale)
        {
            float right = body.Max.X;
            if (_preview is { } preview)
            {
                Theme previewTheme = Crystarium.ActiveTheme;
                float rule = MathF.Max(1f, scale);
                float column = previewTheme.FileDialog.PreviewWidth * scale;
                right = body.Max.X - column - rule;
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(right, body.Min.Y),
                    new Vector2(right + rule, body.Max.Y),
                    ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
                DrawPreview(
                    new WindowFrameRect(
                        new Vector2(right + rule, body.Min.Y), body.Max),
                    preview,
                    scale);
            }

            DrawEntries(
                new WindowFrameRect(
                    body.Min, new Vector2(right, body.Max.Y)),
                scale);
        }

        /// <summary>
        /// The explorer. NO right padding on the region: the bar sits on the
        /// window edge and IS the right inset; a row's own trailing padding is
        /// what keeps its content clear of the bar while its highlight bleeds
        /// under it.
        /// </summary>
        private void DrawEntries(WindowFrameRect body, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
            float inset = theme.Page.Inset;
            int picked = -1;
            bool second = false;
            ImGui.SetCursorScreenPos(body.Min + new Vector2(inset * scale));
            ScrollRegion(
                $"{_id}-entries",
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
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        FileListingEntry entry = _entries[i];
                        var hit = Row(
                            $"{_id}-entry-{entry.FullPath}",
                            width,
                            region.ContentWidth * scale,
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

                        float readoutWidth = ModifiedColumnWidth * scale;
                        float readoutX = hit.ScreenMax.X - readoutWidth;
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
                    $"{_id}-name",
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
        /// gutter is padding, so the highlight paints under the bar while the
        /// hit rect stops at the content edge.</summary>
        private static float RowWidth(ScrollRegionScope region) =>
            region.ContentWidth + Crystarium.ActiveTheme.Scrollbar.GutterWidth;

        /// <summary>
        /// One list row: the reserve at the CONTENT width, the highlight at the
        /// full row width. Rows stack flush at the row height — the ambient
        /// vertical spacing is the surrounding flow's, not the list's.
        /// </summary>
        private static InteractionResult Row(
            string id, float width, float hitWidth, bool selected, float scale)
        {
            Theme theme = Crystarium.ActiveTheme;
            float height = theme.Controls.ListRowHeight * scale;
            var spacing = ImGui.GetStyle().ItemSpacing;
            ImGui.PushStyleVar(
                ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
            var hit = Interactive.Reserve(
                id,
                new Vector2(MathF.Max(1f, hitWidth), height),
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
                    new Vector2(hit.ScreenMin.X + width, hit.ScreenMax.Y),
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

        private static string Modified(DateTime stamp) =>
            stamp == default ? "—" : stamp.ToString("yyyy-MM-dd HH:mm");

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
            if (Source.Parent(_currentPath) is { } parent)
                Travel(parent);
        }

        /// <summary>The path editor's commit. A draft that does not name a
        /// folder is left alone — the field keeps what was typed, and the
        /// listing keeps what it had.</summary>
        private void CommitPath()
        {
            string next = _pathEdit.Trim();
            if (next.Length == 0 || !Source.DirectoryExists(next))
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
                Source.Enumerate(_currentPath, _scratch);
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
