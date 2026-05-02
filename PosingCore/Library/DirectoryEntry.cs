using System.Collections.Generic;
using System.IO;

namespace Poser.Library;

/// <summary>
/// Library entry representing a directory containing other entries.
/// </summary>
public class DirectoryEntry : LibraryEntry
{
    private readonly List<LibraryEntry> _children = new();

    /// <summary>
    /// Child entries (files and subdirectories).
    /// </summary>
    public IReadOnlyList<LibraryEntry> Children => _children;

    /// <summary>
    /// Whether this entry is a container.
    /// </summary>
    public override bool IsContainer => true;

    public DirectoryEntry(string directoryPath)
    {
        Path = directoryPath;
        Name = System.IO.Path.GetFileName(directoryPath);

        // Handle root directories (drive letters, etc.)
        if (string.IsNullOrEmpty(Name))
            Name = directoryPath;
    }

    /// <summary>
    /// Adds a child entry.
    /// </summary>
    public void Add(LibraryEntry entry)
    {
        _children.Add(entry);
    }

    /// <summary>
    /// Clears all children.
    /// </summary>
    public void Clear()
    {
        _children.Clear();
    }

    /// <summary>
    /// Gets the number of pose files in this directory (non-recursive).
    /// </summary>
    public int PoseCount
    {
        get
        {
            int count = 0;
            foreach (var child in _children)
            {
                if (child is PoseLibraryEntry)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Gets entries that match a set of filters.
    /// </summary>
    public IEnumerable<LibraryEntry> GetFilteredEntries(string[]? searchTerms = null, bool favoritesOnly = false)
    {
        foreach (var child in _children)
        {
            // Check favorites filter
            if (favoritesOnly && !child.IsFavorite && !child.IsContainer)
                continue;

            // Check search filter
            if (searchTerms != null && searchTerms.Length > 0 && !child.MatchesSearch(searchTerms))
                continue;

            yield return child;
        }
    }
}
