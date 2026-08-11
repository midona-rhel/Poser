using System;
using System.Collections.Generic;
using System.IO;

namespace Poser.Library;

/// <summary>
/// One configured root the library scans.
/// </summary>
[Serializable]
public class LibrarySourceConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Persisted state of the pose library: where to scan, what is favourited and
/// how the browser is presented.
/// </summary>
[Serializable]
public class LibraryConfiguration
{
    public List<LibrarySourceConfig> Sources { get; set; } = [];

    /// <summary>
    /// Set once the shipped defaults have been appended. Guards re-adding a
    /// source the user has deliberately deleted.
    /// </summary>
    public bool DefaultsSeeded { get; set; }

    /// <summary>Absolute file paths of favourited poses.</summary>
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Grid icon size in logical pixels; the UI clamps to 80..200.</summary>
    public int IconSize { get; set; } = 120;

    public bool UseLibraryWhenImporting { get; set; }

    /// <summary>The browser's live pose preview, off until asked for: it costs
    /// a hidden actor and a render target for as long as the library is open.
    /// </summary>
    public bool PreviewEnabled { get; set; }

    /// <summary>The source folder the last "To library" export landed in, by
    /// PATH — a path survives source-list edits where an index cannot. The
    /// export modal preselects it next time; empty until a first export.
    /// </summary>
    public string LastExportSourcePath { get; set; } = "";

    /// <summary>Tile labels carry the file extension when set.</summary>
    public bool ShowFileExtensions { get; set; }

    /// <summary>
    /// Appends the shipped Brio and Anamnesis roots the first time only.
    /// </summary>
    public void EnsureDefaults()
    {
        if (DefaultsSeeded)
            return;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Sources.Add(new LibrarySourceConfig
        {
            Name = "Brio Poses",
            Path = System.IO.Path.Combine(documents, "Brio", "Poses")
        });

        Sources.Add(new LibrarySourceConfig
        {
            Name = "Anamnesis Poses",
            Path = System.IO.Path.Combine(documents, "Anamnesis", "Poses")
        });

        DefaultsSeeded = true;
    }
}
