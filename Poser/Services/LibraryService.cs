using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Library;

namespace Poser.Services;

/// <summary>
/// Service for managing the pose library.
/// </summary>
public class LibraryService : ILibraryService
{
    private readonly IPluginLog _log;
    private readonly ConfigurationService _config;
    private readonly List<DirectoryEntry> _rootEntries = new();
    private readonly HashSet<string> _favorites = new();
    private bool _isScanning;

    public IReadOnlyList<LibrarySource> Sources => _config.Config.Library.Sources;
    public IReadOnlyList<DirectoryEntry> RootEntries => _rootEntries;
    public IReadOnlySet<string> Favorites => _favorites;
    public bool IsScanning => _isScanning;

    public event Action? OnLibraryRefreshed;
    public event Action? OnFavoritesChanged;
    public event Action<int, int>? OnScanProgress;

    public LibraryService(IPluginLog log, ConfigurationService config)
    {
        _log = log;
        _config = config;
        LoadFavorites();
    }

    public void AddSource(LibrarySource source)
    {
        _config.Config.Library.Sources.Add(source);
        _config.Save();

        if (source.Enabled)
            RefreshSource(source);
    }

    public void RemoveSource(LibrarySource source)
    {
        _config.Config.Library.Sources.Remove(source);
        _config.Save();

        // Remove any root entry for this source
        var entry = _rootEntries.FirstOrDefault(e => e.Path == source.GetFullPath());
        if (entry != null)
        {
            _rootEntries.Remove(entry);
            OnLibraryRefreshed?.Invoke();
        }
    }

    public void Refresh()
    {
        _isScanning = true;
        _rootEntries.Clear();

        var enabledSources = Sources.Where(s => s.Enabled).ToList();
        int total = enabledSources.Count;
        int current = 0;

        foreach (var source in enabledSources)
        {
            current++;
            OnScanProgress?.Invoke(current, total);
            RefreshSourceInternal(source);
        }

        _isScanning = false;
        OnLibraryRefreshed?.Invoke();
    }

    public void RefreshSource(LibrarySource source)
    {
        _isScanning = true;

        // Remove existing entry for this source
        var existing = _rootEntries.FirstOrDefault(e => e.Path == source.GetFullPath());
        if (existing != null)
            _rootEntries.Remove(existing);

        RefreshSourceInternal(source);

        _isScanning = false;
        OnLibraryRefreshed?.Invoke();
    }

    private void RefreshSourceInternal(LibrarySource source)
    {
        var fullPath = source.GetFullPath();

        if (!Directory.Exists(fullPath))
        {
            _log.Debug($"Library source path does not exist: {fullPath}");
            return;
        }

        var entry = ScanDirectory(fullPath);
        if (entry != null)
        {
            entry.Name = source.Name; // Use source name instead of folder name
            _rootEntries.Add(entry);
        }
    }

    public DirectoryEntry? ScanDirectory(string path)
    {
        if (!Directory.Exists(path))
            return null;

        var entry = new DirectoryEntry(path);

        try
        {
            // Add subdirectories
            foreach (var dir in Directory.GetDirectories(path))
            {
                var subEntry = ScanDirectory(dir);
                if (subEntry != null)
                    entry.Add(subEntry);
            }

            // Add pose files
            foreach (var file in Directory.GetFiles(path, "*.pose"))
            {
                var poseEntry = new PoseLibraryEntry(file);
                poseEntry.IsFavorite = _favorites.Contains(poseEntry.Identifier);
                entry.Add(poseEntry);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Error scanning directory {path}: {ex.Message}");
        }

        return entry;
    }

    public IEnumerable<LibraryEntry> Search(string query, bool favoritesOnly = false)
    {
        if (string.IsNullOrWhiteSpace(query) && !favoritesOnly)
            yield break;

        var terms = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pose in GetAllPoses())
        {
            if (favoritesOnly && !pose.IsFavorite)
                continue;

            if (terms.Length > 0 && !pose.MatchesSearch(terms))
                continue;

            yield return pose;
        }
    }

    public IEnumerable<PoseLibraryEntry> GetAllPoses()
    {
        foreach (var root in _rootEntries)
        {
            foreach (var pose in GetPosesRecursive(root))
                yield return pose;
        }
    }

    private IEnumerable<PoseLibraryEntry> GetPosesRecursive(DirectoryEntry dir)
    {
        foreach (var child in dir.Children)
        {
            if (child is PoseLibraryEntry pose)
                yield return pose;
            else if (child is DirectoryEntry subDir)
            {
                foreach (var subPose in GetPosesRecursive(subDir))
                    yield return subPose;
            }
        }
    }

    public void ToggleFavorite(LibraryEntry entry)
    {
        SetFavorite(entry, !entry.IsFavorite);
    }

    public void SetFavorite(LibraryEntry entry, bool isFavorite)
    {
        entry.IsFavorite = isFavorite;

        if (isFavorite)
            _favorites.Add(entry.Identifier);
        else
            _favorites.Remove(entry.Identifier);

        SaveFavorites();
        OnFavoritesChanged?.Invoke();
    }

    public bool IsFavorite(LibraryEntry entry)
    {
        return _favorites.Contains(entry.Identifier);
    }

    public void SaveFavorites()
    {
        _config.Config.Library.Favorites = new HashSet<string>(_favorites);
        _config.Save();
    }

    public void LoadFavorites()
    {
        _favorites.Clear();
        foreach (var fav in _config.Config.Library.Favorites)
            _favorites.Add(fav);

        // Update existing entries
        foreach (var pose in GetAllPoses())
            pose.IsFavorite = _favorites.Contains(pose.Identifier);
    }

    public void Dispose()
    {
        SaveFavorites();
        _rootEntries.Clear();
    }
}
