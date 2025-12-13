using System;
using System.Collections.Generic;
using Poser.Library;

namespace Poser.Services;

/// <summary>
/// Service for managing the pose library.
/// Provides file scanning, navigation, and search functionality.
/// </summary>
public interface ILibraryService : IDisposable
{
    /// <summary>
    /// All configured library sources.
    /// </summary>
    IReadOnlyList<LibrarySource> Sources { get; }

    /// <summary>
    /// The root entries of all enabled sources.
    /// </summary>
    IReadOnlyList<DirectoryEntry> RootEntries { get; }

    /// <summary>
    /// Set of favorited entry identifiers.
    /// </summary>
    IReadOnlySet<string> Favorites { get; }

    /// <summary>
    /// Whether the library is currently scanning.
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Event fired when the library is refreshed.
    /// </summary>
    event Action? OnLibraryRefreshed;

    /// <summary>
    /// Event fired when favorites change.
    /// </summary>
    event Action? OnFavoritesChanged;

    /// <summary>
    /// Event fired when scan progress updates.
    /// </summary>
    event Action<int, int>? OnScanProgress;

    /// <summary>
    /// Adds a library source.
    /// </summary>
    void AddSource(LibrarySource source);

    /// <summary>
    /// Removes a library source.
    /// </summary>
    void RemoveSource(LibrarySource source);

    /// <summary>
    /// Refreshes all library sources (rescans files).
    /// </summary>
    void Refresh();

    /// <summary>
    /// Refreshes a single source.
    /// </summary>
    void RefreshSource(LibrarySource source);

    /// <summary>
    /// Scans a directory and returns its entries.
    /// </summary>
    DirectoryEntry? ScanDirectory(string path);

    /// <summary>
    /// Searches all sources for entries matching the query.
    /// </summary>
    IEnumerable<LibraryEntry> Search(string query, bool favoritesOnly = false);

    /// <summary>
    /// Gets all entries in a flat list (for search results).
    /// </summary>
    IEnumerable<PoseLibraryEntry> GetAllPoses();

    /// <summary>
    /// Toggles favorite status for an entry.
    /// </summary>
    void ToggleFavorite(LibraryEntry entry);

    /// <summary>
    /// Sets favorite status for an entry.
    /// </summary>
    void SetFavorite(LibraryEntry entry, bool isFavorite);

    /// <summary>
    /// Checks if an entry is favorited.
    /// </summary>
    bool IsFavorite(LibraryEntry entry);

    /// <summary>
    /// Saves favorites to configuration.
    /// </summary>
    void SaveFavorites();

    /// <summary>
    /// Loads favorites from configuration.
    /// </summary>
    void LoadFavorites();
}
