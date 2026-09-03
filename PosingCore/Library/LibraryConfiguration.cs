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
    public LibrarySourceKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Persisted state of the pose library: where to scan, what is favourited and
/// how the browser is presented.
/// </summary>
[Serializable]
public partial class LibraryConfiguration
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
        // The managed root is a configuration choice even if its pose source
        // is temporarily disabled. External source selection never changes it.
        var poses = DefaultPoseRoot;
        foreach (var source in Sources)
            if (Classify(source) == LibrarySourceKind.PoserPoses && !string.IsNullOrWhiteSpace(source.Path))
            {
                poses = source.Path;
                break;
            }
        poses = poses.TrimEnd('\\', '/');
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
                Classify(source) == HomeKind(sourceName) &&
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
            if (Classify(source) != HomeKind(sourceName))
                continue;
            source.Kind = HomeKind(sourceName);
            source.Path = chosen;
            source.Enabled = true;
            return;
        }
        Sources.Add(new LibrarySourceConfig { Name = sourceName, Path = chosen, Kind = HomeKind(sourceName) });
    }

    /// <summary>
    /// Makes one home exist and answers with the configured path a file dialog
    /// should open at. Failure is deliberately not redirected to Documents:
    /// the caller that writes a library file must use
    /// <see cref="TryEnsureDirectory"/> and stop when it fails.
    /// Idempotent; every surface that needs a home calls it.
    /// </summary>
    public string EnsureHomeRootExists(string sourceName, string shipped)
    {
        var root = ResolveHomeRoot(sourceName, shipped);
        _ = TryEnsureDirectory(root, out _);
        return root;
    }

    /// <summary>Checked command used immediately before a library write. The
    /// requested path is the only path this method creates or approves.</summary>
    public static bool TryEnsureDirectory(string path, out string detail)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "The library folder path is blank.";
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(path);
            detail = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message.Length <= 4096
                ? $"Could not create library folder '{path}': {ex.Message}"
                : $"Could not create library folder '{path}': {ex.Message[..4096]}";
            return false;
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

    /// <summary>Best-effort startup preparation. A home creation failure is
    /// left visible to the library source-health scan and never throws from
    /// service registration.</summary>
    public void EnsureHomeRootsExist()
    {
        // Startup creates only configured, enabled managed homes. Optional
        // external sources and disabled user choices require an explicit action.
        foreach (var source in Sources)
        {
            if (!source.Enabled)
                continue;
            if (IsManaged(Classify(source)))
                _ = TryEnsureDirectory(source.Path, out _);
        }
    }

    /// <summary>
    /// Appends the shipped Brio and Anamnesis roots the first time only, and
    /// each Poser home under its own flag.
    /// </summary>
    public void EnsureDefaults() =>
        EnsureDefaults(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // The production seeding sequence, with a disposable Documents location
    // supplied by focused filesystem tests.
    internal void EnsureDefaults(string documents)
    {
        var root = Path.Combine(documents, "Poser");

        SeedHome(PoseSourceName, Path.Combine(root, PosesLeaf), PoseRootSeeded,
            () => PoseRootSeeded = true);
        SeedHome(SceneSourceName, Path.Combine(root, ScenesLeaf), SceneRootSeeded,
            () => SceneRootSeeded = true);
        SeedHome(McdfSourceName, Path.Combine(root, McdfLeaf), McdfRootSeeded,
            () => McdfRootSeeded = true);
        SeedHome(ObjectsSourceName, Path.Combine(root, ObjectsLeaf), ObjectsRootSeeded,
            () => ObjectsRootSeeded = true);
        // The settings save once rebuilt the source list without the
        // objects home (it had no folder row there), deleting it on every
        // save while the seed flag kept it from ever coming back. No UI
        // can delete this home deliberately, so a missing objects home is
        // always that bug's residue: it returns at startup.
        EnsureHomeSourceListed(ObjectsSourceName, Path.Combine(root, ObjectsLeaf));

        if (DefaultsSeeded)
            return;

        SeedReference("Brio Poses", Path.Combine(documents, "Brio", "Poses"), LibrarySourceKind.Brio);
        SeedReference("Anamnesis Poses", Path.Combine(documents, "Anamnesis", "Poses"), LibrarySourceKind.Anamnesis);

        DefaultsSeeded = true;
    }

    private void SeedReference(string name, string path, LibrarySourceKind kind)
    {
        foreach (var source in Sources)
            if (Classify(source) == kind || (source.Name == name && SamePath(source.Path, path)))
                return;
        Sources.Add(new LibrarySourceConfig { Name = name, Path = path, Kind = kind });
    }

    private void EnsureHomeSourceListed(string sourceName, string path)
    {
        foreach (var source in Sources)
        {
            if (Classify(source) != HomeKind(sourceName)
                && !(source.Kind == LibrarySourceKind.Legacy && source.Name == sourceName
                    && SamePath(source.Path, path)))
                continue;
            return;
        }
        Sources.Add(new LibrarySourceConfig { Name = sourceName, Path = path, Kind = HomeKind(sourceName) });
    }

    private void SeedHome(
        string sourceName, string shipped, bool seeded, Action markSeeded)
    {
        if (seeded)
            return;
        EnsureHomeSourceListed(sourceName, shipped);
        markSeeded();
    }
}
