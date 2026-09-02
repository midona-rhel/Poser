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
    /// Set once the shipped poses root has been appended. Its OWN flag for the
    /// same reason <see cref="SceneRootSeeded"/> has one.
    /// </summary>
    public bool PoseRootSeeded { get; set; }

    /// <summary>
    /// Set once the shipped scenes root has been appended. Its OWN flag, not
    /// <see cref="DefaultsSeeded"/>: every existing configuration already has
    /// that one set, so a scenes root gated on it would never reach anybody
    /// who installed Poser before scenes had a home.
    /// </summary>
    public bool SceneRootSeeded { get; set; }

    /// <summary>
    /// Set once the shipped character-file root has been appended. Its OWN
    /// flag for the same reason <see cref="SceneRootSeeded"/> has one.
    /// </summary>
    public bool McdfRootSeeded { get; set; }

    /// <summary>
    /// Set once the shipped objects root has been appended. Its OWN flag for
    /// the same reason <see cref="SceneRootSeeded"/> has one.
    /// </summary>
    public bool ObjectsRootSeeded { get; set; }

    /// <summary>The shipped poses root's source name.</summary>
    public const string PoseSourceName = "Poser Poses";

    /// <summary>The shipped scenes root's source name.</summary>
    public const string SceneSourceName = "Poser Scenes";

    /// <summary>The shipped character-file root's source name. "MCDF" already
    /// names the format; a "Poser" prefix on it said nothing.</summary>
    public const string McdfSourceName = "MCDFs";

    /// <summary>The shipped objects root's source name: the home for every
    /// library entry that is not a pose, a scene or a character file —
    /// actors, lights, cameras, overlays, environments.</summary>
    public const string ObjectsSourceName = "Poser Objects";

    /// <summary>
    /// The shipped home sources, in the order they seat on the rail.
    /// One table so a surface that has to walk the homes — the settings page,
    /// the composition root's pre-scan creation — cannot go out of step with
    /// the seeding.
    /// </summary>
    public static IReadOnlyList<(string Name, string Shipped)> Homes { get; } =
    [
        (PoseSourceName, DefaultPoseRoot),
        (SceneSourceName, DefaultSceneRoot),
        (McdfSourceName, DefaultMcdfRoot),
        (ObjectsSourceName, DefaultObjectsRoot),
    ];

    /// <summary>
    /// Where Poser's own documents live by default: under Documents beside the
    /// other tools' roots rather than in the plugin config directory. A pose,
    /// a scene and a character file are documents a user shares and backs up,
    /// not plugin state.
    /// </summary>
    /// <summary>The one Poser folder; every home is a reserved leaf
    /// inside it.</summary>
    public static string DefaultRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Poser");

    public const string PosesLeaf = "Poses";
    public const string ScenesLeaf = "Scenes";
    public const string McdfLeaf = "MCDFs";
    public const string ObjectsLeaf = "Objects";
    public const string AutoSavesLeaf = "Auto-saves";

    private static string HomeRoot(string leaf) =>
        System.IO.Path.Combine(DefaultRoot, leaf);

    /// <summary>The root the configured homes share: the parent of the
    /// poses home when it ends in its reserved leaf, else the default.</summary>
    public string ResolveRoot()
    {
        var poses = ResolvePoseRoot().TrimEnd('\\', '/');
        var parent = System.IO.Path.GetDirectoryName(poses);
        return parent != null
            && string.Equals(
                System.IO.Path.GetFileName(poses),
                PosesLeaf, StringComparison.OrdinalIgnoreCase)
            ? parent
            : DefaultRoot;
    }

    /// <inheritdoc cref="HomeRoot"/>
    public static string DefaultPoseRoot => HomeRoot("Poses");

    /// <inheritdoc cref="HomeRoot"/>
    public static string DefaultSceneRoot => HomeRoot("Scenes");

    /// <inheritdoc cref="HomeRoot"/>
    public static string DefaultMcdfRoot => HomeRoot("MCDFs");

    /// <inheritdoc cref="HomeRoot"/>
    public static string DefaultObjectsRoot => HomeRoot("Objects");

    /// <summary>
    /// The scanned root a save of that kind should land in: the shipped home
    /// source when it is still configured and enabled, else the shipped path
    /// itself. A user who repointed that source keeps their choice; one who
    /// deleted it gets the shipped path back rather than a save that lands
    /// somewhere the matching library tab cannot see.
    /// </summary>
    public string ResolveHomeRoot(string sourceName, string shipped)
    {
        foreach (var source in Sources)
        {
            if (source.Enabled &&
                string.Equals(source.Name, sourceName, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(source.Path))
                return source.Path;
        }
        return shipped;
    }

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolvePoseRoot() =>
        ResolveHomeRoot(PoseSourceName, DefaultPoseRoot);

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolveSceneRoot() =>
        ResolveHomeRoot(SceneSourceName, DefaultSceneRoot);

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolveMcdfRoot() =>
        ResolveHomeRoot(McdfSourceName, DefaultMcdfRoot);

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolveObjectsRoot() =>
        ResolveHomeRoot(ObjectsSourceName, DefaultObjectsRoot);

    /// <summary>
    /// Re-points one home at <paramref name="path"/>, re-adding the source
    /// when it is missing so the new path is a SCANNED root and not merely a
    /// place saves disappear into. A blank path means the shipped one.
    /// </summary>
    public void SetHomeRoot(string sourceName, string shipped, string? path)
    {
        var chosen = string.IsNullOrWhiteSpace(path) ? shipped : path!.Trim();
        foreach (var source in Sources)
        {
            if (!string.Equals(source.Name, sourceName, StringComparison.Ordinal))
                continue;
            source.Path = chosen;
            source.Enabled = true;
            return;
        }
        Sources.Add(new LibrarySourceConfig { Name = sourceName, Path = chosen });
    }

    /// <summary>
    /// Makes one home exist and answers with the path a save should open at.
    /// It is a CONFIGURED library root and the scan refuses to publish a
    /// partial snapshot — a root it cannot observe aborts the whole pass — so
    /// a home that has never been created would take every tab down with it.
    /// Creation failures fall back to Documents, which always exists.
    /// Idempotent; every surface that needs a home calls it.
    /// </summary>
    public string EnsureHomeRootExists(string sourceName, string shipped)
    {
        var root = ResolveHomeRoot(sourceName, shipped);
        // A save must land where its tab can SEE: a home source that was
        // dropped or disabled comes back, enabled, the moment a save
        // needs it — the save itself is the consent.
        EnsureHomeSourceListed(sourceName, root);
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

    /// <inheritdoc cref="EnsureHomeRootExists"/>
    public string EnsurePoseRootExists() =>
        EnsureHomeRootExists(PoseSourceName, DefaultPoseRoot);

    /// <inheritdoc cref="EnsureHomeRootExists"/>
    public string EnsureSceneRootExists() =>
        EnsureHomeRootExists(SceneSourceName, DefaultSceneRoot);

    /// <inheritdoc cref="EnsureHomeRootExists"/>
    public string EnsureMcdfRootExists() =>
        EnsureHomeRootExists(McdfSourceName, DefaultMcdfRoot);

    /// <inheritdoc cref="EnsureHomeRootExists"/>
    public string EnsureObjectsRootExists() =>
        EnsureHomeRootExists(ObjectsSourceName, DefaultObjectsRoot);

    /// <summary>
    /// A collision-safe path for a new library entry: the cleaned name as
    /// is, or with a four-digit timestamp when that file already exists — a
    /// save never overwrites, and an on-disk name is parsed forever so the
    /// stamp keeps its century.
    /// </summary>
    public static string NewEntryPath(string root, string name, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var cleaned = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Entry";
        var path = Path.Combine(root, cleaned + extension);
        if (File.Exists(path))
            path = Path.Combine(
                root,
                $"{cleaned} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{extension}");
        return path;
    }

    /// <summary>
    /// Creates every home before the library service is constructed. The scan
    /// aborts on the FIRST configured root it cannot observe, so one missing
    /// home is every tab missing.
    /// </summary>
    public void EnsureHomeRootsExist()
    {
        foreach (var (name, shipped) in Homes)
            EnsureHomeRootExists(name, shipped);
    }

    /// <summary>
    /// Appends the shipped Brio and Anamnesis roots the first time only, and
    /// each Poser home under its own flag.
    /// </summary>
    public void EnsureDefaults()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        SeedHome(PoseSourceName, DefaultPoseRoot, PoseRootSeeded,
            () => PoseRootSeeded = true);
        SeedHome(SceneSourceName, DefaultSceneRoot, SceneRootSeeded,
            () => SceneRootSeeded = true);
        SeedHome(McdfSourceName, DefaultMcdfRoot, McdfRootSeeded,
            () => McdfRootSeeded = true);
        SeedHome(ObjectsSourceName, DefaultObjectsRoot, ObjectsRootSeeded,
            () => ObjectsRootSeeded = true);
        // The settings save once rebuilt the source list without the
        // objects home (it had no folder row there), deleting it on every
        // save while the seed flag kept it from ever coming back. No UI
        // can delete this home deliberately, so a missing objects home is
        // always that bug's residue: it returns at startup.
        EnsureHomeSourceListed(ObjectsSourceName, DefaultObjectsRoot);

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

    private void EnsureHomeSourceListed(string sourceName, string path)
    {
        foreach (var source in Sources)
        {
            if (!string.Equals(
                    source.Name, sourceName, StringComparison.Ordinal))
                continue;
            source.Enabled = true;
            return;
        }
        Sources.Add(new LibrarySourceConfig { Name = sourceName, Path = path });
    }

    private void SeedHome(
        string sourceName, string shipped, bool seeded, Action markSeeded)
    {
        if (seeded)
            return;
        Sources.Add(new LibrarySourceConfig { Name = sourceName, Path = shipped });
        markSeeded();
    }
}
