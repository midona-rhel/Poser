using System;

namespace Poser.Library;

/// <summary>The shared ordering choices for file-backed browser surfaces.</summary>
public enum LibraryBrowseSort : byte
{
    Name,
    ModifiedNewest,
    ModifiedOldest,
}

public static class LibraryBrowseOrdering
{
    /// <summary>Compares modification stamps and leaves equal stamps tied so
    /// each browser can retain its established name or section ordering.</summary>
    public static int CompareModified(
        DateTime left,
        DateTime right,
        LibraryBrowseSort sort) => sort switch
        {
            LibraryBrowseSort.ModifiedNewest => right.CompareTo(left),
            LibraryBrowseSort.ModifiedOldest => left.CompareTo(right),
            _ => 0,
        };
}
