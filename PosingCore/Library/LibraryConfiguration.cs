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
    /// Set once the shipped scenes root has been appended. Its OWN flag, not
    /// <see cref="DefaultsSeeded"/>: every existing configuration already has
    /// that one set, so a scenes root gated on it would never reach anybody
    /// who installed Poser before scenes had a home.
    /// </summary>
    public bool SceneRootSeeded { get; set; }

    /// <summary>The shipped scenes root's source name.</summary>
    public const string SceneSourceName = "Poser Scenes";

    /// <summary>
    /// Where a saved scene goes by default, and therefore the ONE place the
    /// library's Scenes tab is guaranteed to be looking. Under Documents
    /// beside the other tools' roots rather than in the plugin config
    /// directory: a scene is a document a user shares and backs up, not
    /// plugin state.
    /// </summary>
    public static string DefaultSceneRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Poser",
        "Scenes");

    /// <summary>
    /// The scanned root a scene save should land in: the shipped scenes source
    /// when it is still configured and enabled, else the shipped path itself.
    /// A user who repointed or renamed that source keeps their choice; one who
    /// deleted it gets the shipped path back rather than a save that lands
    /// somewhere the Scenes tab cannot see.
    /// </summary>
    public string ResolveSceneRoot()
    {
        foreach (var source in Sources)
        {
            if (source.Enabled &&
                string.Equals(source.Name, SceneSourceName, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(source.Path))
                return source.Path;
        }
        return DefaultSceneRoot;
    }

    /// <summary>
    /// Makes the scenes root exist and answers with the path a save should
    /// open at. It is a CONFIGURED library root and the scan refuses to
    /// publish a partial snapshot — a root it cannot observe aborts the whole
    /// pass — so a shipped root that has never been created would take the
    /// Poses tab down with it. Creation failures fall back to Documents, which
    /// always exists. Idempotent; every surface that needs the root calls it.
    /// </summary>
    public string EnsureSceneRootExists()
    {
        var root = ResolveSceneRoot();
        try
        {
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>
    /// Appends the shipped Brio and Anamnesis roots the first time only, and
    /// the scenes root under its own flag.
    /// </summary>
    public void EnsureDefaults()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (!SceneRootSeeded)
        {
            Sources.Add(new LibrarySourceConfig
            {
                Name = SceneSourceName,
                Path = DefaultSceneRoot
            });
            SceneRootSeeded = true;
        }

        if (DefaultsSeeded)
            return;

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
