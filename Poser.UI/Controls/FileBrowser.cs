using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Controls;

/// <summary>
/// File dialog on the retained Crystarium.Modal glass chrome: glass
/// background with the directional border trio and black outline/shadow,
/// 44px header and footer, Tabler icons, compact 26px rows with the
/// retained scrollbar treatment, the primary Import/Save action in the
/// footer and Cancel as the secondary action. Selection semantics are
/// unchanged from the legacy window: the callback fires synchronously on
/// the draw thread once the dialog has closed.
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
            Crystarium.Modal(_id, ref _open, _title, DrawBody, DrawFooter,
                ModalSize.Large, height: 420f);

        // The callback fires AFTER the modal closed, exactly like the
        // legacy close-then-invoke ordering.
        if (!_open && _pendingSelect is { } chosen)
        {
            _pendingSelect = null;
            _onSelect?.Invoke(chosen);
        }
    }

    // ── Content ──────────────────────────────────────────────────────────

    private void DrawBody()
    {
        float s = ImGuiHelpers.GlobalScale;

        // Path bar: parent-folder action + the current location.
        if (Crystarium.IconButton(TablerIcon.ArrowUp, "Parent folder"))
        {
            var parent = Directory.GetParent(_currentPath)?.FullName;
            if (parent != null)
                NavigateTo(parent);
        }
        ImGui.SameLine(0f, 8f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(new Vector4(1f, 1f, 1f, 0.72f), _currentPath);

        if (_lastError is { } error)
            ImGui.TextColored(new Vector4(1f, 71f / 255f, 87f / 255f, 0.9f), error);

        float listHeight = ImGui.GetContentRegionAvail().Y;
        float sidebarWidth = 128f * s;

        // Favorites column: compact retained rows.
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
                NavigateTo(path);
        }
        ImGui.EndChild();

        // File list: directories first, compact rows, retained scrollbar.
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
                if (clicked || doubleClicked)
                    NavigateTo(entry.FullPath);
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
    }

    private void DrawFooter()
    {
        float s = ImGuiHelpers.GlobalScale;
        if (_isSaveMode)
        {
            ImGui.SetNextItemWidth(220f * s);
            Crystarium.TextInput($"{_id}-name", ref _fileName, "File name");
            ImGui.SameLine(0f, 8f * s);
        }
        if (Crystarium.Button("Cancel", new ButtonProps
            {
                Id = $"{_id}-cancel",
                Classes = Cls.Compact,
            }))
            _open = false;
        ImGui.SameLine(0f, 8f * s);
        bool canConfirm = _isSaveMode
            ? _fileName.Trim().Length > 0
            : _selectedFile != null;
        if (Crystarium.Button(_isSaveMode ? "Save" : "Import", new ButtonProps
            {
                Id = $"{_id}-confirm",
                Classes = Cls.Compact + Cls.Primary,
                Disabled = !canConfirm,
            }) && canConfirm)
            Confirm();
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
