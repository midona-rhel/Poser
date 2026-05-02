using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Core;
using Poser.Entities;
using Poser.Library;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Window for browsing and applying poses from the library.
/// </summary>
public class LibraryWindow : Window, IDisposable
{
    private const float DefaultWidth = 700f;
    private const float DefaultHeight = 500f;
    private const float InfoPanelWidth = 200f;
    private const float MinIconSize = 60f;
    private const float MaxIconSize = 200f;

    private readonly ILibraryService _libraryService;
    private readonly IPoseFileService _poseFileService;
    private readonly ISelectionService _selectionService;
    private readonly ConfigurationService _config;
    private readonly ITextureProvider _textureProvider;
    private readonly IEventBus _eventBus;

    // Texture cache for thumbnails
    private readonly Dictionary<string, IDalamudTextureWrap?> _textureCache = new();
    private readonly HashSet<string> _loadingTextures = new();

    // UI state
    private string _searchQuery = "";
    private bool _showFavoritesOnly;
    private int _typeFilter; // 0=All, 1=Poses, 2=Characters
    private readonly Stack<DirectoryEntry> _navigationStack = new();
    private DirectoryEntry? _currentDirectory;
    private LibraryEntry? _selectedEntry;

    // Source management modal
    private readonly Modal _sourcesModal = new("Manage Library Sources", new Vector2(500, 400));
    private int _editingSourceIndex = -1;
    private string _editSourceName = "";
    private string _editSourcePath = "";

    private static readonly string[] TypeFilterLabels = { "All", "Poses", "Characters" };

    public LibraryWindow(
        ILibraryService libraryService,
        IPoseFileService poseFileService,
        ISelectionService selectionService,
        ConfigurationService config,
        ITextureProvider textureProvider,
        IEventBus eventBus)
        : base($"Pose Library###{Poser.PluginConstants.PluginName}_library",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        _libraryService = libraryService;
        _poseFileService = poseFileService;
        _selectionService = selectionService;
        _config = config;
        _textureProvider = textureProvider;
        _eventBus = eventBus;

        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;

        _eventBus.Subscribe<LibraryRefreshedEvent>(OnLibraryRefreshed);
    }

    private void OnLibraryRefreshed(LibraryRefreshedEvent e)
    {
        // Reset navigation and clear texture cache
        _navigationStack.Clear();
        _currentDirectory = null;
        _selectedEntry = null;
        ClearTextureCache();
    }

    private void ClearTextureCache()
    {
        foreach (var texture in _textureCache.Values)
            texture?.Dispose();
        _textureCache.Clear();
        _loadingTextures.Clear();
    }

    public override void PreDraw()
    {
        base.PreDraw();

        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Text, UIColors.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, UIColors.TextDisabled);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UIColors.TitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UIColors.ButtonActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.Header, UIColors.SelectionActive);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered);

        float padding = 12f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));
    }

    public override void Draw()
    {
        // Initialize library if needed
        if (_libraryService.RootEntries.Count == 0 && !_libraryService.IsScanning)
        {
            _libraryService.Refresh();
        }

        DrawToolbar();
        ImGui.Spacing();

        DrawTypeFilterTabs();
        ImGui.Spacing();

        DrawBreadcrumb();
        ImGui.Spacing();

        DrawMainContent();

        ImGui.Spacing();
        DrawFooter();

        // Draw source management modal
        _sourcesModal.Draw(DrawSourcesModalContent);
    }

    private void DrawToolbar()
    {
        using var row = Flex.Row(gap: Flex.ItemGap);

        // Favorites toggle
        row.Fixed(Flex.LargeIconSize, (w, h) =>
        {
            var color = _showFavoritesOnly ? UIColors.Orange : UIColors.TextDisabled;
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                if (ImPoser.CenteredIconButton("fav_toggle", FontAwesomeIcon.Star, new Vector2(w, h),
                    _showFavoritesOnly ? "Show all" : "Show favorites only"))
                {
                    _showFavoritesOnly = !_showFavoritesOnly;
                }
            }
        });

        // Search box
        row.Fill((w, h) =>
        {
            ImGui.SetNextItemWidth(w);
            ImGui.InputTextWithHint("##search", "Search poses...", ref _searchQuery, 256);
        });

        // Refresh button
        row.Fixed(Flex.LargeIconSize, (w, h) =>
        {
            if (ImPoser.CenteredIconButton("refresh", FontAwesomeIcon.Sync, new Vector2(w, h), "Refresh library"))
            {
                _libraryService.Refresh();
            }
        });

        // Manage sources button
        row.Fixed(Flex.LargeIconSize, (w, h) =>
        {
            if (ImPoser.CenteredIconButton("manage_sources", FontAwesomeIcon.Cog, new Vector2(w, h), "Manage sources"))
            {
                _sourcesModal.Open();
            }
        });
    }

    private void DrawTypeFilterTabs()
    {
        using var row = Flex.Row(gap: Flex.SmallGap);

        for (int i = 0; i < TypeFilterLabels.Length; i++)
        {
            int index = i;
            row.Fixed(60f, (w, h) =>
            {
                bool isSelected = _typeFilter == index;
                using (ImRaii.PushColor(ImGuiCol.Button, isSelected ? UIColors.SelectionActive : UIColors.Button))
                {
                    if (PoserButton.DrawWithWidth($"type_{index}", TypeFilterLabels[index], w))
                    {
                        _typeFilter = index;
                    }
                }
            });
        }
    }

    private void DrawBreadcrumb()
    {
        using var row = Flex.Row(gap: Flex.SmallGap);

        row.Fill((w, h) =>
        {
            // Home button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.SmallButton(FontAwesomeIcon.Home.ToIconString()))
                {
                    _navigationStack.Clear();
                    _currentDirectory = null;
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Go to root");

            ImGui.SameLine();

            // Build path
            if (_currentDirectory != null)
            {
                var path = new List<DirectoryEntry>();
                foreach (var entry in _navigationStack)
                    path.Insert(0, entry);
                path.Add(_currentDirectory);

                for (int i = 0; i < path.Count; i++)
                {
                    var entry = path[i];

                    ImGui.TextDisabled("/");
                    ImGui.SameLine();

                    if (i == path.Count - 1)
                    {
                        ImGui.Text(entry.Name);
                    }
                    else
                    {
                        if (ImGui.SmallButton(entry.Name))
                        {
                            // Navigate to this entry
                            while (_navigationStack.Count > i)
                                _navigationStack.Pop();
                            _currentDirectory = entry;
                        }
                    }
                    ImGui.SameLine();
                }
            }
            else
            {
                ImGui.TextDisabled("/ Root");
            }
        });
    }

    private void DrawMainContent()
    {
        float scale = ImGuiHelpers.GlobalScale;
        float infoPanelWidth = InfoPanelWidth * scale;
        float availHeight = ImGui.GetContentRegionAvail().Y - (Flex.RowHeight * scale) - (8f * scale);

        // Two-column layout
        using (ImRaii.Child("##grid_area", new Vector2(-infoPanelWidth - 8f * scale, availHeight), false))
        {
            DrawGrid();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, UIColors.ControlBackground))
        using (ImRaii.Child("##info_panel", new Vector2(infoPanelWidth, availHeight), true))
        {
            DrawInfoPanel();
        }
    }

    private void DrawGrid()
    {
        float iconSize = _config.Config.Library.IconSize * ImGuiHelpers.GlobalScale;
        float spacing = 8f * ImGuiHelpers.GlobalScale;
        float labelHeight = ImGui.GetTextLineHeightWithSpacing();
        float cellSize = iconSize + labelHeight + spacing;

        float availWidth = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)((availWidth + spacing) / (cellSize + spacing)));

        IEnumerable<LibraryEntry> entries = GetFilteredEntries();

        int index = 0;
        foreach (var entry in entries)
        {
            if (index > 0 && index % columns != 0)
                ImGui.SameLine();

            DrawGridItem(entry, iconSize, labelHeight);
            index++;
        }

        // Empty state
        if (index == 0)
        {
            ImGui.TextColored(UIColors.TextDisabled, _showFavoritesOnly
                ? "No favorites yet."
                : "No poses found.");
        }
    }

    private IEnumerable<LibraryEntry> GetFilteredEntries()
    {
        IEnumerable<LibraryEntry> entries;

        // Determine base entries
        if (!string.IsNullOrEmpty(_searchQuery) || _showFavoritesOnly)
        {
            entries = _libraryService.Search(_searchQuery, _showFavoritesOnly);
        }
        else if (_currentDirectory != null)
        {
            entries = _currentDirectory.Children;
        }
        else
        {
            entries = _libraryService.RootEntries;
        }

        // Apply type filter
        if (_typeFilter == 1) // Poses only
        {
            foreach (var entry in entries)
            {
                if (entry is DirectoryEntry || entry is PoseLibraryEntry)
                    yield return entry;
            }
        }
        else if (_typeFilter == 2) // Characters only (future: CharacterLibraryEntry)
        {
            foreach (var entry in entries)
            {
                if (entry is DirectoryEntry)
                    yield return entry;
                // Skip poses in character mode
            }
        }
        else
        {
            foreach (var entry in entries)
                yield return entry;
        }
    }

    private void DrawGridItem(LibraryEntry entry, float iconSize, float labelHeight)
    {
        ImGui.PushID(entry.Path);

        float spacing = 4f * ImGuiHelpers.GlobalScale;
        float totalHeight = iconSize + labelHeight + spacing;
        var startPos = ImGui.GetCursorScreenPos();

        // Invisible button for interaction
        bool clicked = ImGui.InvisibleButton("##item", new Vector2(iconSize, totalHeight));
        bool hovered = ImGui.IsItemHovered();
        bool doubleClicked = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && hovered;
        bool isSelected = _selectedEntry == entry;

        var drawList = ImGui.GetWindowDrawList();

        // Background
        uint bgColor = isSelected ? UIColors.SelectionActiveU32
                     : hovered ? UIColors.SelectionHoveredU32
                     : UIColors.ControlBackgroundU32;
        drawList.AddRectFilled(startPos, startPos + new Vector2(iconSize, iconSize), bgColor, 4f);

        // Icon or thumbnail
        var iconPos = startPos + new Vector2(iconSize / 2, iconSize / 2);

        if (entry is DirectoryEntry)
        {
            DrawCenteredIcon(drawList, iconPos, FontAwesomeIcon.Folder, UIColors.OrangeU32);
        }
        else if (entry is PoseLibraryEntry poseEntry)
        {
            // Try to draw thumbnail
            var texture = GetOrLoadThumbnail(poseEntry);
            if (texture != null)
            {
                var texSize = new Vector2(texture.Width, texture.Height);
                float scale = Math.Min(iconSize / texSize.X, iconSize / texSize.Y);
                var drawSize = texSize * scale;
                var drawPos = startPos + (new Vector2(iconSize, iconSize) - drawSize) / 2;
                drawList.AddImage(texture.Handle, drawPos, drawPos + drawSize);
            }
            else
            {
                DrawCenteredIcon(drawList, iconPos, FontAwesomeIcon.User, UIColors.TextU32);
            }

            // Favorite star
            if (poseEntry.IsFavorite)
            {
                var starPos = startPos + new Vector2(iconSize - 14f * ImGuiHelpers.GlobalScale, 4f * ImGuiHelpers.GlobalScale);
                DrawCenteredIcon(drawList, starPos, FontAwesomeIcon.Star, UIColors.OrangeU32);
            }
        }

        // Border for selected
        if (isSelected)
        {
            drawList.AddRect(startPos, startPos + new Vector2(iconSize, iconSize), UIColors.SelectionActiveU32, 4f, ImDrawFlags.None, 2f);
        }

        // Label
        var labelPos = startPos + new Vector2(0, iconSize + spacing);
        string displayName = entry.Name;
        var textSize = ImGui.CalcTextSize(displayName);

        // Truncate if needed
        if (textSize.X > iconSize)
        {
            while (displayName.Length > 3 && ImGui.CalcTextSize(displayName + "...").X > iconSize)
                displayName = displayName[..^1];
            displayName += "...";
        }

        // Center text
        float textOffset = (iconSize - ImGui.CalcTextSize(displayName).X) / 2;
        drawList.AddText(labelPos + new Vector2(Math.Max(0, textOffset), 0), UIColors.TextU32, displayName);

        // Handle clicks
        if (clicked)
        {
            _selectedEntry = entry;
        }

        if (entry is DirectoryEntry dir)
        {
            if (doubleClicked)
            {
                if (_currentDirectory != null)
                    _navigationStack.Push(_currentDirectory);
                _currentDirectory = dir;
                _selectedEntry = null;
            }
        }
        else if (entry is PoseLibraryEntry poseEntry)
        {
            // Right-click context menu
            if (ImGui.BeginPopupContextItem("##context"))
            {
                if (ImGui.MenuItem(poseEntry.IsFavorite ? "Remove from Favorites" : "Add to Favorites"))
                {
                    _libraryService.ToggleFavorite(poseEntry);
                }

                ImGui.EndPopup();
            }

            // Double click to apply
            if (doubleClicked)
            {
                ApplyPose(poseEntry);
            }
        }

        // Tooltip on hover
        if (hovered && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.Name);
        }

        ImGui.PopID();
    }

    private IDalamudTextureWrap? GetOrLoadThumbnail(PoseLibraryEntry entry)
    {
        if (_textureCache.TryGetValue(entry.Path, out var cached))
            return cached;

        // Already loading
        if (_loadingTextures.Contains(entry.Path))
            return null;

        // Start loading
        _loadingTextures.Add(entry.Path);

        try
        {
            var base64 = entry.PreviewImageBase64;
            if (string.IsNullOrEmpty(base64))
            {
                _textureCache[entry.Path] = null;
                return null;
            }

            var imageData = Convert.FromBase64String(base64);
            var textureTask = _textureProvider.CreateFromImageAsync(imageData);

            // Non-blocking: check if already complete
            if (textureTask.IsCompleted)
            {
                var texture = textureTask.Result;
                _textureCache[entry.Path] = texture;
                _loadingTextures.Remove(entry.Path);
                return texture;
            }

            // Schedule completion
            textureTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    _textureCache[entry.Path] = t.Result;
                else
                    _textureCache[entry.Path] = null;
                _loadingTextures.Remove(entry.Path);
            });

            return null;
        }
        catch
        {
            _textureCache[entry.Path] = null;
            _loadingTextures.Remove(entry.Path);
            return null;
        }
    }

    private void DrawInfoPanel()
    {
        if (_selectedEntry == null)
        {
            ImGui.TextColored(UIColors.TextDisabled, "Select an item\nto see details");
            return;
        }

        // Preview image (large)
        if (_selectedEntry is PoseLibraryEntry poseEntry)
        {
            var texture = GetOrLoadThumbnail(poseEntry);
            float availWidth = ImGui.GetContentRegionAvail().X;

            if (texture != null)
            {
                var texSize = new Vector2(texture.Width, texture.Height);
                float scale = availWidth / texSize.X;
                var drawSize = texSize * scale;
                ImGui.Image(texture.Handle, drawSize);
                ImGui.Spacing();
            }

            // Favorite toggle
            using (var favRow = Flex.Row(gap: Flex.SmallGap))
            {
                favRow.Fill((w, h) =>
                {
                    var color = poseEntry.IsFavorite ? UIColors.Orange : UIColors.TextDisabled;
                    using (ImRaii.PushColor(ImGuiCol.Text, color))
                    {
                        if (ImPoser.CenteredIconButton("fav_info", FontAwesomeIcon.Star, new Vector2(w, h),
                            poseEntry.IsFavorite ? "Remove from favorites" : "Add to favorites"))
                        {
                            _libraryService.ToggleFavorite(poseEntry);
                        }
                    }
                });
            }

            ImGui.Spacing();
        }

        // Name
        ImGui.TextWrapped(_selectedEntry.Name);
        PoserUI.Separator();

        // Metadata
        if (_selectedEntry is PoseLibraryEntry pose)
        {
            if (!string.IsNullOrEmpty(pose.Author))
            {
                ImGui.TextColored(UIColors.TextDisabled, "Author");
                ImGui.TextWrapped(pose.Author);
                ImGui.Spacing();
            }

            if (!string.IsNullOrEmpty(pose.Description))
            {
                ImGui.TextColored(UIColors.TextDisabled, "Description");
                ImGui.TextWrapped(pose.Description);
                ImGui.Spacing();
            }

            // Tags
            if (pose.Tags.Count > 0)
            {
                ImGui.TextColored(UIColors.TextDisabled, "Tags");
                foreach (var tag in pose.Tags)
                {
                    ImGui.TextWrapped($"• {tag}");
                }
            }
        }
        else if (_selectedEntry is DirectoryEntry dir)
        {
            ImGui.TextColored(UIColors.TextDisabled, $"{dir.PoseCount} poses");
        }

        // Apply button at bottom
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetContentRegionAvail().Y - 8f);

        if (_selectedEntry is PoseLibraryEntry applyPose)
        {
            float buttonWidth = ImGui.GetContentRegionAvail().X;
            if (PoserButton.DrawWithWidth("apply", "Apply Pose", buttonWidth))
            {
                ApplyPose(applyPose);
            }
        }
    }

    private void DrawFooter()
    {
        using var row = Flex.Row(gap: Flex.ItemGap);

        // Icon size label
        row.Fixed(60f, (w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
            ImGui.TextColored(UIColors.TextDisabled, "Icon size");
        });

        // Icon size slider
        row.Fill((w, h) =>
        {
            float iconSize = _config.Config.Library.IconSize;
            if (Scrubber.Draw("icon_size", ref iconSize, MinIconSize, MaxIconSize, 10f, w, 1f, "F0", "px"))
            {
                _config.Config.Library.IconSize = iconSize;
                _config.ApplyChange();
            }
        });
    }

    private void DrawCenteredIcon(ImDrawListPtr drawList, Vector2 center, FontAwesomeIcon icon, uint color)
    {
        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var textSize = ImGui.CalcTextSize(iconStr);
        drawList.AddText(center - textSize / 2, color, iconStr);
        ImGui.PopFont();
    }

    private void ApplyPose(PoseLibraryEntry entry)
    {
        var skeleton = _selectionService.GetFirstSelected<ISkeleton>();
        if (skeleton == null)
            return;

        var poseFile = entry.GetPoseFile();
        if (poseFile == null)
            return;

        _poseFileService.ImportPose(skeleton, poseFile);
    }

    private void DrawSourcesModalContent()
    {
        var sources = _config.Config.Library.Sources;

        // Header row with Add button
        using (var headerRow = Flex.Row(gap: Flex.ItemGap))
        {
            headerRow.Fill((w, h) =>
            {
                ImGui.TextColored(UIColors.TextDisabled, "Configure library source folders");
            });

            headerRow.Fixed(80f, (w, h) =>
            {
                if (PoserButton.DrawWithWidth("add_source", "Add", w))
                {
                    // Add a new empty source
                    sources.Add(new LibrarySource
                    {
                        Name = "New Source",
                        Path = "",
                        Enabled = true
                    });
                    _config.ApplyChange();
                    _editingSourceIndex = sources.Count - 1;
                    _editSourceName = "New Source";
                    _editSourcePath = "";
                }
            });
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Sources list
        using (var listChild = ImRaii.Child("##sources_list", new Vector2(0, ImGui.GetContentRegionAvail().Y - 50f), false))
        {
            if (listChild.Success)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    DrawSourceEntry(sources, i);
                    ImGui.Spacing();
                }
            }
        }

        // Footer buttons
        ImGui.Spacing();
        using (var footerRow = Flex.Row(gap: Flex.ItemGap))
        {
            footerRow.Fill((w, h) => { }); // Spacer

            footerRow.Fixed(100f, (w, h) =>
            {
                if (PoserButton.DrawWithWidth("close_modal", "Done", w))
                {
                    _sourcesModal.Close();
                    _libraryService.Refresh(); // Refresh after changes
                }
            });
        }
    }

    private void DrawSourceEntry(List<LibrarySource> sources, int index)
    {
        var source = sources[index];
        ImGui.PushID($"source_{index}");

        bool isEditing = _editingSourceIndex == index;

        // Card background
        var startPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        float cardHeight = isEditing ? 100f : 50f;
        var endPos = startPos + new Vector2(availWidth, cardHeight);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(startPos, endPos, UIColors.ControlBackgroundU32, 4f);

        // Content padding
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);

        if (isEditing)
        {
            // Edit mode
            using (var editRow = Flex.Row(gap: Flex.SmallGap))
            {
                editRow.Fixed(60f, (w, h) =>
                {
                    float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                    if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                    ImGui.Text("Name");
                });

                editRow.Fill((w, h) =>
                {
                    ImGui.SetNextItemWidth(w - 16f);
                    ImGui.InputText("##edit_name", ref _editSourceName, 256);
                });
            }

            ImGui.Spacing();

            using (var pathRow = Flex.Row(gap: Flex.SmallGap))
            {
                pathRow.Fixed(60f, (w, h) =>
                {
                    float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                    if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                    ImGui.Text("Path");
                });

                pathRow.Fill((w, h) =>
                {
                    ImGui.SetNextItemWidth(w - 16f);
                    if (string.IsNullOrEmpty(_editSourcePath) && source.RootFolder.HasValue)
                    {
                        // Show full path hint
                        var fullPath = source.GetFullPath();
                        ImGui.InputTextWithHint("##edit_path", fullPath, ref _editSourcePath, 512);
                    }
                    else
                    {
                        ImGui.InputText("##edit_path", ref _editSourcePath, 512);
                    }
                });
            }

            ImGui.Spacing();

            using (var actionRow = Flex.Row(gap: Flex.SmallGap))
            {
                actionRow.Fill((w, h) => { }); // Spacer

                actionRow.Fixed(60f, (w, h) =>
                {
                    if (PoserButton.DrawWithWidth("cancel", "Cancel", w))
                    {
                        _editingSourceIndex = -1;
                    }
                });

                actionRow.Fixed(60f, (w, h) =>
                {
                    if (PoserButton.DrawWithWidth("save", "Save", w))
                    {
                        source.Name = _editSourceName;
                        if (!string.IsNullOrWhiteSpace(_editSourcePath))
                        {
                            source.Path = _editSourcePath;
                            source.RootFolder = null; // Clear special folder, use absolute path
                        }
                        _config.ApplyChange();
                        _editingSourceIndex = -1;
                    }
                });
            }
        }
        else
        {
            // View mode
            using (var viewRow = Flex.Row(gap: Flex.ItemGap))
            {
                // Enable checkbox
                viewRow.Fixed(Flex.LargeIconSize, (w, h) =>
                {
                    bool enabled = source.Enabled;
                    if (ImGui.Checkbox("##enabled", ref enabled))
                    {
                        source.Enabled = enabled;
                        _config.ApplyChange();
                    }
                });

                // Name and path
                viewRow.Fill((w, h) =>
                {
                    ImGui.Text(source.Name);
                    var fullPath = source.GetFullPath();
                    ImGui.TextColored(UIColors.TextDisabled, fullPath);
                });

                // Edit button
                viewRow.Fixed(Flex.LargeIconSize, (w, h) =>
                {
                    if (ImPoser.CenteredIconButton("edit", FontAwesomeIcon.Edit, new Vector2(w, h), "Edit source"))
                    {
                        _editingSourceIndex = index;
                        _editSourceName = source.Name;
                        _editSourcePath = source.RootFolder.HasValue ? "" : source.Path;
                    }
                });

                // Delete button
                viewRow.Fixed(Flex.LargeIconSize, (w, h) =>
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, UIColors.Red))
                    {
                        if (ImPoser.CenteredIconButton("delete", FontAwesomeIcon.Trash, new Vector2(w, h), "Remove source"))
                        {
                            sources.RemoveAt(index);
                            _config.ApplyChange();
                        }
                    }
                });
            }
        }

        // Reserve space for the card
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cardHeight - 8f);
        ImGui.PopID();
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(1);
        ImGui.PopStyleColor(14);
        base.PostDraw();
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<LibraryRefreshedEvent>(OnLibraryRefreshed);
        ClearTextureCache();
        GC.SuppressFinalize(this);
    }
}
