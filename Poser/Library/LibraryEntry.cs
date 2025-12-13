using System;
using System.Collections.Generic;

namespace Poser.Library;

/// <summary>
/// Base class for all library entries (files, directories, sources).
/// </summary>
public abstract class LibraryEntry
{
    /// <summary>
    /// Display name of the entry.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Full file system path.
    /// </summary>
    public string Path { get; protected set; } = "";

    /// <summary>
    /// Unique identifier for favorites/persistence.
    /// </summary>
    public virtual string Identifier => Path;

    /// <summary>
    /// Whether this entry is a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Tags associated with this entry.
    /// </summary>
    public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this entry is a container (directory/source).
    /// </summary>
    public virtual bool IsContainer => false;

    /// <summary>
    /// Checks if this entry matches a search query.
    /// </summary>
    public virtual bool MatchesSearch(string[] queryTerms)
    {
        if (queryTerms.Length == 0)
            return true;

        var nameLower = Name.ToLowerInvariant();

        foreach (var term in queryTerms)
        {
            var termLower = term.ToLowerInvariant();

            // Check name
            if (nameLower.Contains(termLower))
                continue;

            // Check tags
            bool tagMatch = false;
            foreach (var tag in Tags)
            {
                if (tag.Contains(termLower, StringComparison.OrdinalIgnoreCase))
                {
                    tagMatch = true;
                    break;
                }
            }

            if (!tagMatch)
                return false;
        }

        return true;
    }
}
