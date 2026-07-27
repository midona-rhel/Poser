using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Movable, non-modal file window. Navigation and callbacks preserve the
    /// legacy close-before-invoke ordering; all chrome and layout are shared.
    /// </summary>
    public sealed class FileDialog
    {
        private readonly string _title;
        private readonly string[] _extensions;
        private readonly bool _isSaveMode;
        private readonly string _id;

        private readonly record struct Entry(
            string Name,
            string FullPath,
            bool IsDirectory);

        private bool _open;
        private string _currentPath = string.Empty;
        private string _fileName = string.Empty;
        private string? _selectedFile;
        private string? _lastError;
        private string? _pendingSelect;
        private string? _pendingNavigate;
        private string _pathEdit = string.Empty;
        private Action<string>? _onSelect;
        private readonly List<(string Name, string Path)> _favorites = new();
        private readonly List<Entry> _entries = new();

        public FileDialog(
            string title,
            string[] extensions,
            bool isSaveMode = false)
        {
            _title = title;
            _extensions = extensions;
            _isSaveMode = isSaveMode;
            _id = $"##file-dialog-{Guid.NewGuid():N}";
            InitializeFavorites();
        }

        public bool IsOpen => _open;

        public void Open(string initialPath, Action<string> onSelect)
        {
            _onSelect = onSelect;
            _selectedFile = null;
            _fileName = string.Empty;
            _lastError = null;
            if (string.IsNullOrEmpty(initialPath)
                || !Directory.Exists(initialPath))
            {
                initialPath = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments);
            }
            NavigateTo(initialPath);
            _open = true;
        }

        public void Draw()
        {
            if (_open)
                DrawWindow();

            if (!_open && _pendingSelect is { } chosen)
            {
                _pendingSelect = null;
                _onSelect?.Invoke(chosen);
            }
        }

        private void DrawWindow()
        {
            FloatingSurface.Window(
                $"{_title}{_id}",
                ref _open,
                Theme.Metrics.FileDialog.Width,
                Theme.Metrics.FileDialog.Height,
                DrawFrame);
        }

        private void DrawFrame(FloatingSurfaceFrame frame)
        {
            float scale = frame.Scale;
            float barHeight = Theme.Metrics.Floating.ModalBar * scale;
            float headerInset = Theme.Metrics.Floating.HeaderInset * scale;
            float closeSize = Theme.Metrics.Floating.CloseAction * scale;
            var drawList = ImGui.GetWindowDrawList();

            DrawTextCentered(
                new Vector2(frame.Min.X + headerInset, frame.Min.Y),
                new Vector2(
                    frame.Size.X
                        - headerInset * 2f
                        - Theme.Metrics.Floating.CloseAction * scale,
                    barHeight),
                Theme.Metrics.Typography.SurfaceTitle,
                FontWeight.Medium,
                FormValueColor,
                _title);

            ImGui.SetCursorScreenPos(new Vector2(
                frame.Max.X
                    - Theme.Metrics.Floating.CloseInset * scale
                    - closeSize,
                frame.Min.Y + (barHeight - closeSize) * 0.5f));
            if (FloatingSurface.CloseButton($"{_id}-close"))
                _open = false;

            DrawHairline(
                drawList,
                new Vector2(frame.Min.X, frame.Min.Y + barHeight),
                new Vector2(frame.Max.X, frame.Min.Y + barHeight),
                scale);

            float footerTop = frame.Max.Y - barHeight;
            DrawHairline(
                drawList,
                new Vector2(frame.Min.X, footerTop),
                new Vector2(frame.Max.X, footerTop),
                scale);

            float bodyInset = Theme.Metrics.Floating.ModalBodyPadding * scale;
            float bodyVertical = Theme.Metrics.Space.Four * scale;
            var bodyMin = new Vector2(
                frame.Min.X + bodyInset,
                frame.Min.Y + barHeight + bodyVertical);
            var bodyMax = new Vector2(
                frame.Max.X - bodyInset,
                footerTop - bodyVertical);
            DrawBody(bodyMin, bodyMax, scale);
            DrawFooter(frame.Min, frame.Max, footerTop, scale);
        }

        private void DrawBody(Vector2 min, Vector2 max, float scale)
        {
            float control = Theme.Metrics.Control.Comfortable * scale;
            float gap = Theme.Metrics.Page.ActionGap * scale;
            ImGui.SetCursorScreenPos(min);
            if (IconButton(
                    TablerIcon.ArrowUp,
                    new ButtonProps
                    {
                        Id = $"{_id}-up",
                        Classes = Cls.Comfortable,
                        Tooltip = "Open the parent folder",
                    }))
            {
                var parent = Directory.GetParent(_currentPath)?.FullName;
                if (parent != null)
                    _pendingNavigate = parent;
            }

            float pathX = min.X + control + gap;
            float pathWidth = max.X - pathX;
            ImGui.SetCursorScreenPos(new Vector2(pathX, min.Y));
            if (TextInput(
                    $"{_id}-path",
                    ref _pathEdit,
                    new TextInputProps
                    {
                        Classes = Cls.Comfortable,
                        Placeholder = "Path",
                        Style = new TextInputStyle
                        {
                            Width = Sizing.Fixed(pathWidth / scale),
                        },
                    })
                && Directory.Exists(_pathEdit)
                && !string.Equals(
                    _pathEdit,
                    _currentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingNavigate = _pathEdit;
            }

            float y = min.Y
                + (Theme.Metrics.Control.Comfortable
                    + Theme.Metrics.Space.Two) * scale;
            if (_lastError is { } error)
            {
                DrawText(
                    new Vector2(min.X, y),
                    max.X - min.X,
                    Theme.Metrics.Typography.Caption,
                    FontWeight.Regular,
                    Norvrandt.Sheet.CurrentTheme.Danger,
                    error);
                y += Theme.Metrics.Page.StatusLine * scale;
            }

            float listHeight = MathF.Max(0f, max.Y - y);
            float favoritesWidth =
                Theme.Metrics.FileDialog.FavoritesWidth * scale;
            ImGui.SetCursorScreenPos(new Vector2(min.X, y));
            ScrollRegion(
                $"{_id}-favorites",
                Theme.Metrics.FileDialog.FavoritesWidth,
                listHeight / scale,
                region =>
                {
                    foreach (var (name, path) in _favorites)
                    {
                        if (region.ListRow(
                                $"{_id}-favorite-{path}",
                                name,
                                TablerIcon.Folder,
                                selected: string.Equals(
                                    path,
                                    _currentPath,
                                    StringComparison.OrdinalIgnoreCase)))
                            _pendingNavigate = path;
                    }
                });

            float separatorX = min.X
                + favoritesWidth
                + Theme.Metrics.Space.Two * scale;
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(separatorX, y),
                new Vector2(
                    separatorX + MathF.Max(1f, scale),
                    y + listHeight),
                ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));

            float entriesX = separatorX
                + MathF.Max(1f, scale)
                + Theme.Metrics.Space.Four * scale;
            float entriesWidth = MathF.Max(0f, max.X - entriesX);
            ImGui.SetCursorScreenPos(new Vector2(entriesX, y));
            ScrollRegion(
                $"{_id}-entries",
                entriesWidth / scale,
                listHeight / scale,
                region =>
                {
                    foreach (var entry in _entries)
                    {
                        bool clicked = region.ListRow(
                            $"{_id}-entry-{entry.FullPath}",
                            entry.Name,
                            entry.IsDirectory
                                ? TablerIcon.Folder
                                : TablerIcon.FileText,
                            selected: !entry.IsDirectory
                                && string.Equals(
                                    entry.FullPath,
                                    _selectedFile,
                                    StringComparison.OrdinalIgnoreCase));
                        bool doubleClicked =
                            region.LastRowDoubleClicked();
                        if (entry.IsDirectory)
                        {
                            if (clicked || doubleClicked)
                                _pendingNavigate = entry.FullPath;
                            continue;
                        }
                        if (clicked)
                        {
                            _selectedFile = entry.FullPath;
                            if (_isSaveMode)
                                _fileName = entry.Name;
                        }
                        if (doubleClicked)
                        {
                            _selectedFile = entry.FullPath;
                            if (_isSaveMode)
                                _fileName = entry.Name;
                            else
                                Confirm();
                        }
                    }
                    if (_entries.Count == 0 && _lastError == null)
                        region.Empty("This folder is empty.");
                });

            if (_pendingNavigate is { } target)
            {
                _pendingNavigate = null;
                NavigateTo(target);
            }
        }

        private void DrawFooter(
            Vector2 min,
            Vector2 max,
            float footerTop,
            float scale)
        {
            string confirmLabel = _isSaveMode ? "Save" : "Import";
            var confirmSize = MeasureButton(
                confirmLabel,
                Cls.Comfortable + Cls.Primary);
            var cancelSize = MeasureButton("Cancel", Cls.Comfortable);
            float gap = Theme.Metrics.Page.ActionGap * scale;
            float inset = Theme.Metrics.Floating.HeaderInset * scale;
            float y = footerTop
                + (Theme.Metrics.Floating.ModalBar
                    - Theme.Metrics.Control.Comfortable) * 0.5f * scale;
            float confirmX = max.X - inset - confirmSize.X;

            bool canConfirm = _isSaveMode
                ? _fileName.Trim().Length > 0
                : _selectedFile != null;
            ImGui.SetCursorScreenPos(new Vector2(confirmX, y));
            if (Button(
                    confirmLabel,
                    new ButtonProps
                    {
                        Id = $"{_id}-confirm",
                        Classes = Cls.Comfortable + Cls.Primary,
                        Disabled = !canConfirm,
                    })
                && canConfirm)
                Confirm();

            float cancelX = confirmX - gap - cancelSize.X;
            ImGui.SetCursorScreenPos(new Vector2(cancelX, y));
            if (Button(
                    "Cancel",
                    new ButtonProps
                    {
                        Id = $"{_id}-cancel",
                        Classes = Cls.Comfortable,
                    }))
                _open = false;

            if (_isSaveMode)
            {
                float available = MathF.Max(
                    0f,
                    cancelX - gap - (min.X + inset));
                float width = MathF.Min(
                    Theme.Metrics.FileDialog.FileNameWidth * scale,
                    available);
                ImGui.SetCursorScreenPos(new Vector2(min.X + inset, y));
                TextInput(
                    $"{_id}-name",
                    ref _fileName,
                    new TextInputProps
                    {
                        Classes = Cls.Comfortable,
                        Placeholder = "File name",
                        Style = new TextInputStyle
                        {
                            Width = Sizing.Fixed(width / scale),
                        },
                    });
            }
        }

        private static void DrawHairline(
            ImDrawListPtr drawList,
            Vector2 min,
            Vector2 max,
            float scale)
        {
            drawList.AddRectFilled(
                min,
                new Vector2(max.X, max.Y + MathF.Max(1f, scale)),
                ImGui.ColorConvertFloat4ToU32(FormSeparatorColor));
        }

        private void Confirm()
        {
            string path;
            if (_isSaveMode)
            {
                var name = _fileName.Trim();
                if (name.Length == 0)
                    return;
                if (_extensions.Length > 0
                    && !_extensions.Any(extension =>
                        name.EndsWith(
                            extension,
                            StringComparison.OrdinalIgnoreCase)))
                    name += _extensions[0];
                path = Path.Combine(_currentPath, name);
            }
            else
            {
                if (_selectedFile is not { } selected)
                    return;
                path = selected;
            }
            _open = false;
            _pendingSelect = path;
        }

        private void NavigateTo(string path)
        {
            _currentPath = path;
            _pathEdit = path;
            _selectedFile = null;
            RefreshEntries();
        }

        private void RefreshEntries()
        {
            _entries.Clear();
            _lastError = null;
            try
            {
                foreach (var directory in Directory.GetDirectories(_currentPath)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    _entries.Add(new Entry(
                        info.Name,
                        directory,
                        IsDirectory: true));
                }
                foreach (var file in Directory.GetFiles(_currentPath)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.Hidden) != 0)
                        continue;
                    if (_extensions.Length > 0
                        && !_extensions.Any(extension =>
                            string.Equals(
                                info.Extension,
                                extension,
                                StringComparison.OrdinalIgnoreCase)))
                        continue;
                    _entries.Add(new Entry(
                        info.Name,
                        file,
                        IsDirectory: false));
                }
            }
            catch (Exception ex)
            {
                _lastError =
                    $"This folder could not be read: {ex.Message}";
            }
        }

        private void InitializeFavorites()
        {
            void AddSpecial(
                string name,
                Environment.SpecialFolder folder)
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path)
                    && Directory.Exists(path))
                    _favorites.Add((name, path));
            }

            AddSpecial("Desktop", Environment.SpecialFolder.Desktop);
            AddSpecial("Documents", Environment.SpecialFolder.MyDocuments);
            AddSpecial("Pictures", Environment.SpecialFolder.MyPictures);
            var downloads = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloads))
                _favorites.Add(("Downloads", downloads));
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        _favorites.Add((
                            drive.Name,
                            drive.RootDirectory.FullName));
                    }
                }
            }
            catch
            {
                // Unreadable drive enumeration must not break the dialog.
            }
        }
    }
}
