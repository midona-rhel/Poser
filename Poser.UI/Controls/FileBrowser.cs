using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

/// <summary>
/// Reusable file browser control for selecting files.
/// </summary>
public class FileBrowser
{
    private readonly Modal _modal;
    private readonly string _title;
    private readonly string[] _extensions;
    private readonly bool _isSaveMode;

    private string _currentPath = "";
    private string _fileName = "";
    private List<FileSystemEntry> _entries = new();
    private int _selectedIndex = -1;
    private Action<string>? _onSelect;
    private string _lastError = "";

    // Favorites/Quick Access
    private readonly List<FavoriteEntry> _favorites = new();

    private const float SidebarWidth = 120f;

    public FileBrowser(string title, string[] extensions, bool isSaveMode = false)
    {
        _title = title;
        _extensions = extensions;
        _isSaveMode = isSaveMode;
        _modal = new Modal(title, new Vector2(600, 400));

        InitializeFavorites();
    }

    private void InitializeFavorites()
    {
        _favorites.Clear();

        // Add standard Windows folders
        AddFavorite("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), FontAwesomeIcon.Desktop);
        AddFavorite("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FontAwesomeIcon.FileAlt);
        AddFavorite("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FontAwesomeIcon.Image);

        var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        AddFavorite("Downloads", downloadsPath, FontAwesomeIcon.Download);

        // Add drives
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    string name = string.IsNullOrEmpty(drive.VolumeLabel)
                        ? drive.Name.TrimEnd('\\')
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                    AddFavorite(name, drive.RootDirectory.FullName, FontAwesomeIcon.Hdd);
                }
            }
        }
        catch
        {
            // Ignore drive enumeration errors
        }
    }

    private void AddFavorite(string name, string path, FontAwesomeIcon icon)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            _favorites.Add(new FavoriteEntry { Name = name, Path = path, Icon = icon });
        }
    }

    public bool IsOpen => _modal.IsOpen;

    /// <summary>
    /// Opens the file browser with a callback for when a file is selected.
    /// </summary>
    public void Open(string initialPath, Action<string> onSelect)
    {
        _onSelect = onSelect;
        _selectedIndex = -1;
        _fileName = "";
        _lastError = "";

        if (string.IsNullOrEmpty(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        NavigateTo(initialPath);
        _modal.Open();
    }

    /// <summary>
    /// Draws the file browser modal. Call this every frame.
    /// </summary>
    public void Draw()
    {
        _modal.Draw(_title, DrawContent);
    }

    private void DrawContent(ImDrawListPtr drawList)
    {
        float scale = ImGuiHelpers.GlobalScale;

        DrawPathBar();
        ImGui.Spacing();

        // Calculate heights
        float bottomBarHeight = _isSaveMode
            ? (Norvrandt.Sheet.CurrentTheme.RowHeight * scale * 2 + Norvrandt.Sheet.CurrentTheme.RowSpacing * scale * 2)
            : (Norvrandt.Sheet.CurrentTheme.RowHeight * scale + Norvrandt.Sheet.CurrentTheme.RowSpacing * scale);
        float listHeight = ImGui.GetContentRegionAvail().Y - bottomBarHeight;

        // Two-column layout
        float sidebarWidthScaled = SidebarWidth * scale;

        // Sidebar (no border)
        using (ImRaii.Child("##sidebar", new Vector2(sidebarWidthScaled, listHeight), false))
        {
            DrawFavorites();
        }

        ImGui.SameLine();

        // File list with ControlBackground
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Norvrandt.Sheet.CurrentTheme.SurfaceSunken))
        using (ImRaii.Child("##file_list", new Vector2(-1, listHeight), true))
        {
            DrawFileEntries();
        }

        ImGui.Spacing();
        DrawBottomBar();
    }

    private void DrawPathBar()
    {
        using var row = Flex.Row(gap: Theme.Spacing.Sm);

        // Up button
        row.Fixed(Norvrandt.Sheet.CurrentTheme.LargeIcon, (w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button(FontAwesomeIcon.ArrowUp.ToIconString()))
                {
                    NavigateUp();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Go up one folder");
        });

        // Path display
        row.Fill(w =>
        {
            ImGui.SetNextItemWidth(w);
            string path = _currentPath;
            if (ImGui.InputText("##path", ref path, 512, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (Directory.Exists(path))
                {
                    NavigateTo(path);
                }
            }
        });

        // Refresh button
        row.Fixed(Norvrandt.Sheet.CurrentTheme.LargeIcon, (w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button(FontAwesomeIcon.Sync.ToIconString()))
                {
                    RefreshEntries();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh");
        });
    }

    private void DrawFavorites()
    {
        ImGui.TextColored(Theme.Palette.Gray, "Quick Access");
        ImGui.Spacing();

        foreach (var fav in _favorites)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(Theme.Palette.Orange, fav.Icon.ToIconString());
            }

            ImGui.SameLine();

            bool isCurrentPath = _currentPath.Equals(fav.Path, StringComparison.OrdinalIgnoreCase);
            if (isCurrentPath)
            {
                ImGui.TextColored(Norvrandt.Sheet.CurrentTheme.Accent, fav.Name);
            }
            else
            {
                if (ImGui.Selectable(fav.Name))
                {
                    NavigateTo(fav.Path);
                }
            }
        }
    }

    private void DrawFileEntries()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            bool isSelected = i == _selectedIndex;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var icon = entry.IsDirectory ? FontAwesomeIcon.Folder : FontAwesomeIcon.File;
                var iconColor = entry.IsDirectory ? Theme.Palette.Orange : Norvrandt.Sheet.CurrentTheme.Text;
                ImGui.TextColored(iconColor, icon.ToIconString());
            }

            ImGui.SameLine();

            if (ImGui.Selectable(entry.Name, isSelected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selectedIndex = i;
                if (!entry.IsDirectory)
                {
                    _fileName = entry.Name;
                }

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (entry.IsDirectory)
                    {
                        NavigateTo(entry.FullPath);
                    }
                    else
                    {
                        SelectFile(entry.FullPath);
                    }
                }
            }
        }
    }

    private void DrawBottomBar()
    {
        // File name input (for save mode)
        if (_isSaveMode)
        {
            using var row = Flex.Row(gap: Norvrandt.Sheet.CurrentTheme.ItemGap);
            row.Label("Name");
            row.Fill(w =>
            {
                ImGui.SetNextItemWidth(w);
                ImGui.InputText("##filename", ref _fileName, 256);
            });
        }

        // Error message
        if (!string.IsNullOrEmpty(_lastError))
        {
            ImGui.TextColored(Theme.Palette.Red, _lastError);
        }

        // Buttons
        using (var row = Flex.Row(gap: Theme.Spacing.Sm))
        {
            row.Spacer();

            row.Fixed(Norvrandt.Sheet.CurrentTheme.ButtonMin, (w, h) =>
            {
                if (Crystarium.Button("Cancel", new ButtonProps { Id = "cancel", Style = new ButtonStyle { Width = Sizing.Fixed(w / ImGuiHelpers.GlobalScale) } }))
                {
                    _modal.Close();
                }
            });

            row.Fixed(Norvrandt.Sheet.CurrentTheme.ButtonMin, (w, h) =>
            {
                string buttonText = _isSaveMode ? "Save" : "Open";
                bool canSelect = !string.IsNullOrEmpty(_fileName);

                using (ImRaii.Disabled(!canSelect))
                {
                    if (Crystarium.Button(buttonText, new ButtonProps { Id = "select", Style = new ButtonStyle { Width = Sizing.Fixed(w / ImGuiHelpers.GlobalScale) } }))
                    {
                        string fullPath = Path.Combine(_currentPath, _fileName);

                        if (_isSaveMode)
                        {
                            // Add extension if missing
                            if (_extensions.Length > 0 && !_extensions.Any(e => fullPath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                            {
                                fullPath += _extensions[0];
                            }
                            SelectFile(fullPath);
                        }
                        else
                        {
                            if (File.Exists(fullPath))
                            {
                                SelectFile(fullPath);
                            }
                            else
                            {
                                _lastError = "File does not exist";
                            }
                        }
                    }
                }
            });
        }
    }

    private void NavigateTo(string path)
    {
        if (!Directory.Exists(path))
            return;

        _currentPath = path;
        _selectedIndex = -1;
        RefreshEntries();
    }

    private void NavigateUp()
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent != null)
        {
            NavigateTo(parent.FullName);
        }
    }

    private void RefreshEntries()
    {
        _entries.Clear();
        _lastError = "";

        try
        {
            // Add directories first
            foreach (var dir in Directory.GetDirectories(_currentPath))
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                _entries.Add(new FileSystemEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = true
                });
            }

            // Add files with matching extensions
            foreach (var file in Directory.GetFiles(_currentPath))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                // Filter by extension
                if (_extensions.Length > 0)
                {
                    bool matches = _extensions.Any(e =>
                        info.Extension.Equals(e, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                        continue;
                }

                _entries.Add(new FileSystemEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false
                });
            }

            // Sort: directories first, then by name
            _entries = _entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            _lastError = $"Error: {ex.Message}";
        }
    }

    private void SelectFile(string path)
    {
        _modal.Close();
        _onSelect?.Invoke(path);
    }

    private struct FileSystemEntry
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
    }

    private struct FavoriteEntry
    {
        public string Name;
        public string Path;
        public FontAwesomeIcon Icon;
    }
}
