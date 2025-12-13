using System;

namespace Poser.Library;

/// <summary>
/// Configuration for a library source directory.
/// </summary>
public class LibrarySource
{
    /// <summary>
    /// Display name of the source.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Root directory path. Can be absolute or relative to a special folder.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// If set, Path is relative to this special folder.
    /// </summary>
    public Environment.SpecialFolder? RootFolder { get; set; }

    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets the full resolved path.
    /// </summary>
    public string GetFullPath()
    {
        if (RootFolder.HasValue)
        {
            var rootPath = Environment.GetFolderPath(RootFolder.Value);
            return System.IO.Path.Combine(rootPath, Path);
        }
        return Path;
    }

    /// <summary>
    /// Creates a default source for the Poser poses folder.
    /// </summary>
    public static LibrarySource CreatePoserPoses()
    {
        return new LibrarySource
        {
            Name = "Poser Poses",
            Path = "Poser\\Poses",
            RootFolder = Environment.SpecialFolder.MyDocuments,
            Enabled = true
        };
    }

    /// <summary>
    /// Creates a default source for Brio poses folder (compatibility).
    /// </summary>
    public static LibrarySource CreateBrioPoses()
    {
        return new LibrarySource
        {
            Name = "Brio Poses",
            Path = "Brio\\Poses",
            RootFolder = Environment.SpecialFolder.MyDocuments,
            Enabled = true
        };
    }

    /// <summary>
    /// Creates a default source for Anamnesis poses folder (compatibility).
    /// </summary>
    public static LibrarySource CreateAnamnesisPoses()
    {
        return new LibrarySource
        {
            Name = "Anamnesis Poses",
            Path = "Anamnesis\\Poses",
            RootFolder = Environment.SpecialFolder.MyDocuments,
            Enabled = true
        };
    }
}
