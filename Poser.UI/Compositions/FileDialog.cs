using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;
// LegacyCrystarium declares imperative ActionBar/Button METHODS, so the
// declarative prop bags of the same names are reached through aliases.
using UiActionBar = Poser.UI.ActionBar;
using UiButton = Poser.UI.Button;

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

public static partial class LegacyCrystarium
{
    /// <summary>
    /// The file surface, DECLARED: one retained root paints the whole frame —
    /// the title bar, the navigation band, the quick rail beside the explorer,
    /// the optional preview column and the footer — inside the same movable,
    /// non-modal window the imperative dialog always opened. Navigation and
    /// callbacks preserve the legacy close-before-invoke ordering.
    ///
    /// <para>THE PUBLIC SHAPE IS FROZEN: the constructor, <see cref="Open"/>
    /// and <see cref="Draw"/> are what four call sites already speak, so the
    /// rebuild is entirely behind them.</para>
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

        private readonly UiRoot _root = new();
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

        /// <summary>The double-click detector. The runtime dispatches CLICKS,
        /// so the second one inside ImGui's own double-click interval on the
        /// same row is what "double-click" means here — which keeps the gesture
        /// out of the kernel for the one surface that wants it.</summary>
        private string? _lastClickPath;
        private double _lastClickAt;

        // ── hoisted handlers ─────────────────────────────────────────────────
        // A build path may allocate no delegate, so every callback the tree
        // names is a field closing over `this` and dispatching against the
        // per-frame state the build wrote.
        private readonly Action _close;
        private readonly Action _goBack;
        private readonly Action _goForward;
        private readonly Action _goUp;
        private readonly Action _confirm;
        private readonly Action<int> _pickEntry;
        private readonly Action<int> _pickQuick;

        private readonly PathIsland _pathIsland;
        private readonly NameIsland _nameIsland;
        private readonly PreviewPainter _previewPainter = new();

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
            _pathIsland = new PathIsland(this);
            _nameIsland = new NameIsland(this);
            _close = () => _open = false;
            _goBack = Back;
            _goForward = Forward;
            _goUp = Up;
            _confirm = Confirm;
            _pickEntry = PickEntry;
            _pickQuick = PickQuick;
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

        /// <summary>
        /// The frame, rendered into a caller-owned box. The window is the
        /// PRODUCT's host; the chassis has to be inspectable without one, which
        /// is what the conformance fixture calls.
        ///
        /// <para>The glass is NOT painted here, which is the one thing this
        /// surface does differently from Settings: <c>FloatingSurface.Window</c>
        /// already draws the chrome for every window it hosts, so
        /// <see cref="WindowChassis.Render"/> — the entry for a surface that
        /// owns its own window paint — would draw a second shadow over the
        /// first.</para>
        /// </summary>
        internal void RenderFrame(Vector2 origin, Vector2 size)
        {
            // Asking the provider is a SELECTION-change edge, not a frame one,
            // and it must not happen inside a build: a build is pure over the
            // state the frame started with.
            if (!string.Equals(_previewPath, _selectedPath, StringComparison.Ordinal))
            {
                _previewPath = _selectedPath;
                _preview = _selectedPath is { } path && !_selectedIsDirectory
                    ? FilePreview?.Invoke(path)
                    : null;
            }

            var props = new Props(this);
            _root.Render(
                origin, size, in props, static (in Props p) => p.Dialog.Build());
        }

        private void DrawFrame(FloatingSurfaceFrame frame) =>
            RenderFrame(frame.Min, frame.Size);

        /// <summary>Everything one frame's build is TOLD; the dialog reference
        /// is what the static builder reaches its state through.</summary>
        private readonly record struct Props(FileDialog Dialog);

        // ── the frame ────────────────────────────────────────────────────────

        /// <summary>
        /// The frame is the SHARED chassis, told this surface's slots: the
        /// title bar and the footer band are its statement, the navigation row
        /// is the band it offers, the quick menu is its rail, and the explorer
        /// and preview are its body.
        /// </summary>
        private UiNode Build()
        {
            Theme theme = Crystarium.ActiveTheme;
            bool canConfirm = _isSaveMode
                ? _fileName.Trim().Length > 0
                : SelectedFile is not null;
            return new WindowChassis
            {
                Title = _title,
                OnClose = _close,
                CloseHelp = "Close",
                Band = Navigation(theme),
                Rail = new ScrollArea
                {
                    Height = UiDim.Fill,
                    Style = new() { Layout = new() { Width = UiDim.Fill } },
                    CapChildHitWidth = true,
                    Children = QuickRows(),
                    Key = "quick",
                },
                RailWidth = theme.FileDialog.RailWidth,
                Body = Body(theme),
                // The footer's left slot STRETCHES: the name a save is about,
                // or the name a load has chosen, reaching the actions.
                FooterFill = _isSaveMode
                    ? Crystarium.Native(
                        _nameIsland,
                        UiDim.Fill,
                        UiDim.Fixed(theme.Controls.ComfortableHeight),
                        "name")
                    : new Label
                    {
                        Text = SelectedFile is { } chosen
                            ? Path.GetFileName(chosen)
                            : "No file selected",
                        Sheet = SelectedFile is null
                            ? SheetFamily.Hint
                            : SheetFamily.FormValue,
                        Preview = true,
                    },
                FooterRight =
                [
                    new UiButton { Label = "Cancel", OnClick = _close, Key = "cancel" },
                    new UiButton
                    {
                        Label = _isSaveMode ? "Save" : "Load",
                        Style = ButtonStyle.Primary,
                        OnClick = _confirm,
                        Disabled = !canConfirm,
                        Key = "confirm",
                    },
                ],
            };
        }

        /// <summary>
        /// The navigation band: the history pair, the parent step, and the path
        /// editor filling everything they leave. It is the SAME action bar the
        /// title and the footer wear — a band padded to the header inset with a
        /// stretching middle — minus the rule, because the rule above it
        /// already separated the chrome.
        /// </summary>
        private UiNode Navigation(Theme theme) => new UiActionBar
        {
            Left =
            [
                new IconAction
                {
                    Icon = TablerIcon.ArrowBackUp,
                    OnClick = _goBack,
                    Disabled = _back.Count == 0,
                    Size = NavActionSize,
                    Help = "Back",
                    Key = "back",
                },
                // A redo arrow IS the undo arrow reflected, and the registry
                // carries one of them.
                new IconAction
                {
                    Icon = TablerIcon.ArrowBackUp,
                    FlipX = true,
                    OnClick = _goForward,
                    Disabled = _forward.Count == 0,
                    Size = NavActionSize,
                    Help = "Forward",
                    Key = "forward",
                },
                new IconAction
                {
                    Icon = TablerIcon.ArrowUp,
                    OnClick = _goUp,
                    Disabled = Source.Parent(_currentPath) is null,
                    Size = NavActionSize,
                    Help = "Open the parent folder",
                    Key = "up",
                },
            ],
            Fill = Crystarium.Native(
                _pathIsland,
                UiDim.Fill,
                UiDim.Fixed(theme.Controls.ComfortableHeight),
                "path"),
            Key = "nav",
        };

        /// <summary>The chassis' BODY slot: the explorer, and the preview
        /// column that exists only while the provider answered.</summary>
        private UiNode Body(Theme theme)
        {
            bool preview = _preview is not null;
            return new Row
            {
                Style = new()
                {
                    Layout = new() { Width = UiDim.Fill, Height = UiDim.Fill },
                },
                Children =
                [
                    new ScrollArea
                    {
                        Height = UiDim.Fill,
                        Style = new()
                        {
                            Layout = new()
                            {
                                Width = UiDim.Fill,
                                Padding = new EdgeInsets(
                                    theme.Page.Inset, theme.Page.Inset,
                                    theme.Page.Inset, theme.Page.Inset),
                            },
                        },
                        CapChildHitWidth = true,
                        Children = EntryRows(theme),
                        Key = "entries",
                    },
                    preview ? Rule() : UiNode.None,
                    preview ? Preview(theme) : UiNode.None,
                ],
            };
        }

        private static UiNode Rule() => new Element
        {
            Sheet = SheetFamily.BarRule,
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(1f),
                    Height = UiDim.Fill,
                },
            },
        };

        private UiChildren QuickRows()
        {
            FrameArena arena = FrameArena.Require();
            Span<UiNode> rows = arena.ScratchNodes(_quick.Count);
            for (int i = 0; i < _quick.Count; i++)
            {
                FileQuickEntry entry = _quick[i];
                rows[i] = new Element
                {
                    Sheet = SheetFamily.NavRow,
                    Selected = string.Equals(
                        entry.Path, _currentPath, StringComparison.OrdinalIgnoreCase),
                    Index = i,
                    On = new Listeners { OnPick = _pickQuick },
                    Key = entry.Path,
                    Children =
                    [
                        new Stack
                        {
                            Sheet = SheetFamily.NavIconSlot,
                            Children = new Glyph
                            {
                                Icon = entry.Icon,
                                Size = Crystarium.ActiveTheme.Controls.SmallIconSize,
                            },
                        },
                        new Label
                        {
                            Text = entry.Name,
                            Sheet = SheetFamily.NavLabel,
                            Preview = true,
                        },
                    ],
                };
            }

            return UiChildren.Create(rows);
        }

        private UiChildren EntryRows(Theme theme)
        {
            if (_lastError is { } error)
                return new Element
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Width = UiDim.Fill,
                            Height = UiDim.Fixed(theme.Controls.ListRowHeight),
                        },
                    },
                    Children = new Label
                    {
                        Text = error,
                        Sheet = SheetFamily.Hint,
                        Style = new()
                        {
                            Colors = new() { Foreground = theme.Danger },
                        },
                    },
                };

            if (_entries.Count == 0)
                return new Element
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Width = UiDim.Fill,
                            Height = UiDim.Fixed(theme.Controls.ListRowHeight),
                        },
                    },
                    Children = new Label
                    {
                        Text = "This folder is empty.",
                        Sheet = SheetFamily.Hint,
                    },
                };

            FrameArena arena = FrameArena.Require();
            Span<UiNode> rows = arena.ScratchNodes(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                FileListingEntry entry = _entries[i];
                rows[i] = new Element
                {
                    Sheet = SheetFamily.NavRow,
                    Selected = string.Equals(
                        entry.FullPath, _selectedPath, StringComparison.OrdinalIgnoreCase),
                    Index = i,
                    On = new Listeners { OnPick = _pickEntry },
                    // A listing reorders under every navigation, so a row keyed
                    // by position would hand its neighbour's hover to whatever
                    // slid into its place.
                    Key = entry.FullPath,
                    Children =
                    [
                        new Stack
                        {
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Flow = UiFlow.Stack,
                                    Justify = UiAlign.Center,
                                    Align = UiAlign.Center,
                                    Width = UiDim.Fixed(EntryIconSlot),
                                    Height = UiDim.Fill,
                                },
                            },
                            Children = new Glyph
                            {
                                Icon = entry.IsDirectory
                                    ? TablerIcon.Folder
                                    : TablerIcon.FileText,
                                Size = theme.Controls.SmallIconSize,
                            },
                        },
                        new Label
                        {
                            Text = entry.Name,
                            Sheet = SheetFamily.NavLabel,
                            Preview = true,
                        },
                        new Label
                        {
                            Text = Modified(entry.Modified),
                            Sheet = SheetFamily.Readout,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fixed(ModifiedColumnWidth),
                                    Justify = UiAlign.End,
                                    Margin = new EdgeInsets(
                                        theme.Spacing.Two, 0f, 0f, 0f),
                                },
                            },
                        },
                    ],
                };
            }

            return UiChildren.Create(rows);
        }

        /// <summary>
        /// The preview column, which exists only while the provider answered.
        /// The image is a hook rather than the element's own texture slot: the
        /// base paints a host image on a SQUARE, and a portrait render fitted
        /// into a tall column is the one geometry that square cannot express.
        /// </summary>
        private UiNode Preview(Theme theme)
        {
            FilePreviewResult result = _preview!.Value;
            _previewPainter.Bind(result.Texture, result.Size);
            return new Column
            {
                Style = new()
                {
                    Layout = new()
                    {
                        Width = UiDim.Fixed(theme.FileDialog.PreviewWidth),
                        Height = UiDim.Fill,
                        Padding = new EdgeInsets(
                            theme.Page.Inset, theme.Page.Inset,
                            theme.Page.Inset, theme.Page.Inset),
                        Align = UiAlign.Center,
                    },
                },
                Children =
                [
                    new Element
                    {
                        Style = new()
                        {
                            Layout = new()
                            {
                                Width = UiDim.Fill,
                                Height = UiDim.Fill,
                            },
                        },
                        Painter = _previewPainter,
                    },
                    string.IsNullOrEmpty(result.Caption)
                        ? UiNode.None
                        : new Element
                        {
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fill,
                                    Height = UiDim.Fixed(PreviewCaptionHeight),
                                    Justify = UiAlign.Center,
                                    Align = UiAlign.Center,
                                },
                            },
                            Children = new Label
                            {
                                Text = result.Caption!,
                                Sheet = SheetFamily.Caption,
                            },
                        },
                ],
                Key = "preview",
            };
        }

        private string? SelectedFile =>
            _selectedIsDirectory ? null : _selectedPath;

        private static string Modified(DateTime stamp) =>
            stamp == default ? "—" : stamp.ToString("yyyy-MM-dd HH:mm");

        // ── dispatch ─────────────────────────────────────────────────────────

        private void PickQuick(int index)
        {
            if ((uint)index >= (uint)_quick.Count)
                return;
            Travel(_quick[index].Path);
        }

        private void PickEntry(int index)
        {
            if ((uint)index >= (uint)_entries.Count)
                return;
            FileListingEntry entry = _entries[index];
            double now = ImGui.GetTime();
            bool second = string.Equals(
                    _lastClickPath, entry.FullPath, StringComparison.Ordinal)
                && now - _lastClickAt <= ImGui.GetIO().MouseDoubleClickTime;
            _lastClickPath = entry.FullPath;
            _lastClickAt = now;

            _selectedPath = entry.FullPath;
            _selectedIsDirectory = entry.IsDirectory;
            if (!entry.IsDirectory && _isSaveMode)
                _fileName = entry.Name;
            if (!second)
                return;

            _lastClickPath = null;
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
            _lastClickPath = null;
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

        // ── native islands ───────────────────────────────────────────────────

        /// <summary>
        /// The path editor. A NATIVE island because a text field's caret,
        /// selection, clipboard and IME composition are ImGui's own retained
        /// state — the same seam the picker's filter rides — and the non-search
        /// variant of it, because a path is typed, not searched.
        /// </summary>
        private sealed class PathIsland : INativeElement
        {
            private readonly FileDialog _owner;
            private readonly Action<string> _onChange;

            internal PathIsland(FileDialog owner)
            {
                _owner = owner;
                _onChange = next => _owner._pathEdit = next;
            }

            public void Draw(string id, Vector2 min, Vector2 max)
            {
                float scale = ImGuiHelpers.GlobalScale;
                TextInput(
                    id,
                    _owner._pathEdit,
                    _onChange,
                    new ControlStyle
                    {
                        Height = UiHeight.Comfortable,
                        Width = UiWidth.Fixed(MathF.Max(1f, (max.X - min.X) / scale)),
                    },
                    placeholder: "Path");
                // Committing is leaving the field having edited it — Enter
                // deactivates a native InputText, so one test covers both the
                // keypress and the click away.
                if (ImGui.IsItemDeactivatedAfterEdit())
                    _owner.CommitPath();
            }
        }

        /// <summary>The save-mode file name. Same seam, no commit edge: the
        /// primary button is the commit.</summary>
        private sealed class NameIsland : INativeElement
        {
            private readonly FileDialog _owner;
            private readonly Action<string> _onChange;

            internal NameIsland(FileDialog owner)
            {
                _owner = owner;
                _onChange = next => _owner._fileName = next;
            }

            public void Draw(string id, Vector2 min, Vector2 max)
            {
                float scale = ImGuiHelpers.GlobalScale;
                TextInput(
                    id,
                    _owner._fileName,
                    _onChange,
                    new ControlStyle
                    {
                        Height = UiHeight.Comfortable,
                        Width = UiWidth.Fixed(MathF.Max(1f, (max.X - min.X) / scale)),
                    },
                    placeholder: "File name");
            }
        }

        /// <summary>
        /// The preview image, aspect-fitted into whatever box the column left.
        /// One instance per dialog, rebound each frame: a hook allocated per
        /// frame would put the panel's cost on every warm frame of a surface
        /// that usually has no preview at all.
        /// </summary>
        private sealed class PreviewPainter : IPainter
        {
            private nint _texture;
            private Vector2 _source;

            public bool NeedsHit => false;

            internal void Bind(nint texture, Vector2 source)
            {
                _texture = texture;
                _source = source;
            }

            public PaintResult Paint(in PaintContext context)
            {
                Vector2 box = context.Size;
                if (_texture == 0
                    || box.X <= 0f || box.Y <= 0f
                    || _source.X <= 0f || _source.Y <= 0f)
                    return default;

                float fit = MathF.Min(box.X / _source.X, box.Y / _source.Y);
                Vector2 size = _source * fit;
                Vector2 min = Crystarium.ActiveTheme.Optical.Snap(
                    context.Min + (box - size) * 0.5f);
                context.DrawList.AddImage(
                    new ImTextureID(_texture),
                    min,
                    min + size,
                    Vector2.Zero,
                    Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(Vector4.One));
                return default;
            }
        }
    }

    /// <summary>
    /// The real disk. The hidden-attribute suppression lives here because it is
    /// the FILESYSTEM's notion of what exists; everything the dialog decides —
    /// order, extension filter, the error line — is above the seam.
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
}
