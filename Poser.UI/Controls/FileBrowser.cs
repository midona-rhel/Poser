using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// File dialog as a FLOATING glass window (not a modal — it neither dims
/// nor blocks the UI and can be moved aside): glass chrome with the
/// directional border trio, 44px header and bottom-anchored 44px footer
/// with the primary Import/Save action and Cancel beside it, an editable
/// outlined path input with a plain square up button, a separated
/// favorites column, and compact 26px Tabler rows with the retained
/// scrollbar treatment. Navigation is DEFERRED to after the row
/// enumeration. The selection callback fires after the window closes.
/// </summary>
public class FileBrowser
{
    private readonly string _title;
    private readonly string[] _extensions;
    private readonly bool _isSaveMode;
    private readonly string _id;

    private readonly record struct Entry(string Name, string FullPath, bool IsDirectory);

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

    public FileBrowser(string title, string[] extensions, bool isSaveMode = false)
    {
        _title = title;
        _extensions = extensions;
        _isSaveMode = isSaveMode;
        _id = $"##file-browser-{Guid.NewGuid():N}";
        InitializeFavorites();
    }

    public bool IsOpen => _open;

    public void Open(string initialPath, Action<string> onSelect)
    {
        _onSelect = onSelect;
        _selectedFile = null;
        _fileName = string.Empty;
        _lastError = null;
        if (string.IsNullOrEmpty(initialPath) || !Directory.Exists(initialPath))
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        NavigateTo(initialPath);
        _open = true;
    }

    /// <summary>Call every frame from a top-level draw.</summary>
    public void Draw()
    {
        if (_open)
            DrawWindow();

        // The callback fires AFTER the window closed, exactly like the
        // legacy close-then-invoke ordering.
        if (!_open && _pendingSelect is { } chosen)
        {
            _pendingSelect = null;
            _onSelect?.Invoke(chosen);
        }
    }

    // A floating glass window, NOT a modal: it neither dims nor blocks the
    // rest of the UI and can be moved aside.
    private void DrawWindow()
    {
        float s = ImGuiHelpers.GlobalScale;
        var size = new Vector2(680f, 440f) * s;
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos((display - size) / 2f, ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool visible = ImGui.Begin($"{_title}{_id}", ref _open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoResize);
        if (visible)
        {
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            var dl = ImGui.GetWindowDrawList();
            // The window itself is NoBackground, so the surface fill is
            // OURS to draw — DrawSurface provides only blur, ring, and
            // borders (popup hosts got their fill from the popup bg).
            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(GlassChrome.BackgroundColor), 10f * s);
            GlassChrome.DrawSurface(dl, min, max, 10f);

            float header = 44f * s;
            float footer = 44f * s;
            uint hairline = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f));

            // 44px header: title + plain close icon.
            ImGui.SetCursorScreenPos(new Vector2(
                min.X + 16f * s, min.Y + (header - ImGui.GetTextLineHeight()) / 2f));
            ImGui.TextColored(new Vector4(1f, 1f, 1f, 0.9f), _title);
            if (SquareIconButton($"{_id}-close", TablerIcon.X,
                    new Vector2(max.X - (16f + 24f) * s, min.Y + (header - 24f * s) / 2f), s))
                _open = false;
            dl.AddRectFilled(
                new Vector2(min.X + 1f, min.Y + header),
                new Vector2(max.X - 1f, min.Y + header + 1f), hairline);

            // Body between header and footer.
            ImGui.SetCursorScreenPos(new Vector2(min.X + 16f * s, min.Y + header + 8f * s));
            ImGui.BeginChild("##fb-outer",
                new Vector2(max.X - min.X - 32f * s,
                    max.Y - min.Y - header - footer - 16f * s),
                false, ImGuiWindowFlags.NoSavedSettings);
            DrawBody();
            ImGui.EndChild();

            // 44px footer band with the actions bottom-anchored.
            float footerTop = max.Y - footer;
            dl.AddRectFilled(
                new Vector2(min.X + 1f, footerTop),
                new Vector2(max.X - 1f, footerTop + 1f), hairline);
            DrawFooter(min, max, footerTop, s);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Plain square Picto icon button: hover fill, no outline.</summary>
    private static bool SquareIconButton(string id, TablerIcon icon, Vector2 pos, float s)
    {
        var sizePx = new Vector2(24f, 24f) * s;
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, sizePx);
        if (ImGui.IsItemHovered())
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + sizePx,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f)), 6f * s);
        ImGui.SetCursorScreenPos(pos + (sizePx - new Vector2(16f, 16f) * s) / 2f);
        Crystarium.Icon(icon, 16f, new Vector4(1f, 1f, 1f, ImGui.IsItemHovered() ? 1f : 0.8f));
        return clicked;
    }

    // ── Content ──────────────────────────────────────────────────────────

    private void DrawBody()
    {
        float s = ImGuiHelpers.GlobalScale;

        // Path row: plain square up button + an EDITABLE outlined path
        // input (the input chrome carries the outline). Both are placed
        // EXPLICITLY on one shared row top so they center against each
        // other instead of trailing SameLine's item baseline.
        var rowTop = ImGui.GetCursorScreenPos();
        float inputHeight = 24f * s; // theme row height, the input's own height
        if (SquareIconButton($"{_id}-up", TablerIcon.ArrowUp,
                new Vector2(rowTop.X, rowTop.Y + (inputHeight - 24f * s) / 2f), s))
        {
            var parent = Directory.GetParent(_currentPath)?.FullName;
            if (parent != null)
                _pendingNavigate = parent;
        }
        ImGui.SetCursorScreenPos(new Vector2(rowTop.X + (24f + 8f) * s, rowTop.Y));
        float pathWidth = ImGui.GetContentRegionAvail().X / s;
        if (Crystarium.TextInput($"{_id}-path", ref _pathEdit, new TextInputProps
            {
                Placeholder = "Path",
                Style = new TextInputStyle { Width = Sizing.Fixed(pathWidth) },
            })
            && Directory.Exists(_pathEdit)
            && !string.Equals(_pathEdit, _currentPath, StringComparison.OrdinalIgnoreCase))
            _pendingNavigate = _pathEdit;
        ImGui.Dummy(new Vector2(0f, 4f * s));

        if (_lastError is { } error)
            ImGui.TextColored(new Vector4(1f, 71f / 255f, 87f / 255f, 0.9f), error);

        float listHeight = ImGui.GetContentRegionAvail().Y;
        float sidebarWidth = 128f * s;

        Crystarium.PushScrollbarStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##fb-favorites", new Vector2(sidebarWidth, listHeight),
            false, ImGuiWindowFlags.NoSavedSettings);
        foreach (var (name, path) in _favorites)
        {
            if (Crystarium.SidebarRow($"##fb-fav-{path}", name, new SidebarRowProps
                {
                    Icon = TablerIcon.Folder,
                    NoExpanderSlot = true,
                    Selected = string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase),
                    Width = 128f,
                }))
                _pendingNavigate = path;
        }
        ImGui.EndChild();

        // 1px separator between the favorites and the file list.
        ImGui.SameLine(0f, 4f * s);
        var sepTop = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(sepTop,
            sepTop + new Vector2(MathF.Max(1f, 1f * s), listHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f)));
        ImGui.SameLine(0f, 8f * s);

        ImGui.BeginChild("##fb-entries",
            new Vector2(ImGui.GetContentRegionAvail().X, listHeight),
            false, ImGuiWindowFlags.NoSavedSettings);
        // 12px stable scrollbar gutter, the shell's retained treatment.
        float rowWidth = ImGui.GetContentRegionAvail().X / s - 12f;
        foreach (var entry in _entries)
        {
            bool clicked = Crystarium.SidebarRow($"##fb-{entry.FullPath}", entry.Name,
                new SidebarRowProps
                {
                    Icon = entry.IsDirectory ? TablerIcon.Folder : TablerIcon.FileText,
                    NoExpanderSlot = true,
                    Selected = !entry.IsDirectory && string.Equals(
                        entry.FullPath, _selectedFile, StringComparison.OrdinalIgnoreCase),
                    Width = rowWidth,
                });
            bool doubleClicked = ImGui.IsItemHovered()
                && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
            if (entry.IsDirectory)
            {
                // DEFERRED: navigating re-fills _entries, so it must never
                // happen inside this enumeration.
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
            ImGui.TextColored(new Vector4(1f, 1f, 1f, 0.4f), "  This folder is empty.");
        ImGui.EndChild();
        ImGui.PopStyleVar();
        Crystarium.PopScrollbarStyle();

        if (_pendingNavigate is { } target)
        {
            _pendingNavigate = null;
            NavigateTo(target);
        }
    }

    private void DrawFooter(Vector2 min, Vector2 max, float footerTop, float s)
    {
        float buttonY = footerTop + (44f * s - 24f * s) / 2f;
        var confirmSize = Crystarium.MeasureButton(_isSaveMode ? "Save" : "Import", Cls.Compact);
        var cancelSize = Crystarium.MeasureButton("Cancel", Cls.Compact);
        float x = max.X - 16f * s - confirmSize.X;

        bool canConfirm = _isSaveMode
            ? _fileName.Trim().Length > 0
            : _selectedFile != null;
        ImGui.SetCursorScreenPos(new Vector2(x, buttonY));
        if (Crystarium.Button(_isSaveMode ? "Save" : "Import", new ButtonProps
            {
                Id = $"{_id}-confirm",
                Classes = Cls.Compact + Cls.Primary,
                Disabled = !canConfirm,
            }) && canConfirm)
            Confirm();

        x -= 8f * s + cancelSize.X;
        ImGui.SetCursorScreenPos(new Vector2(x, buttonY));
        if (Crystarium.Button("Cancel", new ButtonProps
            {
                Id = $"{_id}-cancel",
                Classes = Cls.Compact,
            }))
            _open = false;

        if (_isSaveMode)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X + 16f * s, buttonY));
            Crystarium.TextInput($"{_id}-name", ref _fileName, new TextInputProps
            {
                Placeholder = "File name",
                Style = new TextInputStyle
                {
                    Width = Sizing.Fixed((x - min.X) / s - 32f),
                },
            });
        }
    }

    // ── Behavior (unchanged from the legacy window) ──────────────────────

    private void Confirm()
    {
        string path;
        if (_isSaveMode)
        {
            var name = _fileName.Trim();
            if (name.Length == 0)
                return;
            if (_extensions.Length > 0 && !_extensions.Any(extension =>
                    name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
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
                _entries.Add(new Entry(info.Name, directory, IsDirectory: true));
            }
            foreach (var file in Directory.GetFiles(_currentPath)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.Hidden) != 0)
                    continue;
                if (_extensions.Length > 0 && !_extensions.Any(extension =>
                        string.Equals(info.Extension, extension, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _entries.Add(new Entry(info.Name, file, IsDirectory: false));
            }
        }
        catch (Exception ex)
        {
            _lastError = $"This folder could not be read: {ex.Message}";
        }
    }

    private void InitializeFavorites()
    {
        void AddSpecial(string name, Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                _favorites.Add((name, path));
        }

        AddSpecial("Desktop", Environment.SpecialFolder.Desktop);
        AddSpecial("Documents", Environment.SpecialFolder.MyDocuments);
        AddSpecial("Pictures", Environment.SpecialFolder.MyPictures);
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            _favorites.Add(("Downloads", downloads));
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.IsReady)
                    _favorites.Add((drive.Name, drive.RootDirectory.FullName));
        }
        catch
        {
            // Unreadable drive enumeration must not break the dialog.
        }
    }
}
