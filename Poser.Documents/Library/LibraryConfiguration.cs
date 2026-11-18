using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Poser.Documents.Library;

/// <summary>
/// One folder the library lists files from.
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
/// The library's saved settings: which folders it lists, which poses are
/// favourites, and how it is shown.
/// </summary>
[Serializable]
public partial class LibraryConfiguration
{
    public List<LibrarySourceConfig> Sources { get; set; } = [];

    /// <summary>
    /// Set once the Brio and Anamnesis pose folders have been added. They are
    /// not added again after that, so a folder someone removed stays removed.
    /// </summary>
    public bool DefaultsSeeded { get; set; }

    /// <summary>A random id for this Poser install, made once. A scene records the id of
    /// the install that saved it, so a load can tell your own scenes from shared ones.</summary>
    public Guid InstallId { get; set; }

    /// <summary>Absolute file paths of favourited poses.</summary>
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Grid icon size in logical pixels; the UI clamps to 80..200.</summary>
    public int IconSize { get; set; } = 120;

    public bool UseLibraryWhenImporting { get; set; }

    /// <summary>The library's live pose preview. Off until switched on, because
    /// it keeps a hidden actor and an off-screen image for as long as the
    /// library is open.
    /// </summary>
    public bool PreviewEnabled { get; set; }

    /// <summary>The folder the last "To library" export went to; the export
    /// window picks it again next time. It is kept as a path, not as a place
    /// in the folder list, so it stays right when the list changes. Empty
    /// until the first export.
    /// </summary>
    public string LastExportSourcePath { get; set; } = "";

    /// <summary>Tile labels carry the file extension when set.</summary>
    public bool ShowFileExtensions { get; set; }

    /// <summary>
    /// Set once the Poses folder has been added to the library. It has its own
    /// flag for the same reason as <see cref="SceneRootSeeded"/>.
    /// </summary>
    public bool PoseRootSeeded { get; set; }

    /// <summary>
    /// Set once the Scenes folder has been added to the library. It has its
    /// own flag, not <see cref="DefaultsSeeded"/>, because that one is already
    /// set for anyone who used Poser before the Scenes folder existed, and
    /// they would never get it.
    /// </summary>
    public bool SceneRootSeeded { get; set; }

    /// <summary>
    /// Set once the MCDFs folder has been added to the library. It has its own
    /// flag for the same reason as <see cref="SceneRootSeeded"/>.
    /// </summary>
    public bool McdfRootSeeded { get; set; }

    /// <summary>
    /// Set once the Objects folder has been added to the library. It has its
    /// own flag for the same reason as <see cref="SceneRootSeeded"/>.
    /// </summary>
    public bool ObjectsRootSeeded { get; set; }

    /// <summary>The name of the Poses folder Poser adds to the library.</summary>
    public const string PoseSourceName = "Poser Poses";

    /// <summary>The name of the Scenes folder Poser adds to the library.</summary>
    public const string SceneSourceName = "Poser Scenes";

    /// <summary>The name of the MCDFs folder Poser adds to the library. It has
    /// no "Poser" in front, because "MCDFs" already says what it holds.</summary>
    public const string McdfSourceName = "MCDFs";

    /// <summary>The name of the Objects folder Poser adds to the library. It
    /// holds every library entry that is not a pose, a scene or an MCDF, such
    /// as actors, lights, cameras, overlays and environments.</summary>
    public const string ObjectsSourceName = "Poser Objects";

    /// <summary>
    /// Poser's own library folders, in the order the library lists them.
    /// Everything that goes through them, such as the settings page and the
    /// folder creation at startup, uses this one list so they always agree.
    /// </summary>
    public static IReadOnlyList<(string Name, string Shipped)> Homes { get; } =
    [
        (PoseSourceName, DefaultPoseRoot),
        (SceneSourceName, DefaultSceneRoot),
        (McdfSourceName, DefaultMcdfRoot),
        (ObjectsSourceName, DefaultObjectsRoot),
    ];

    /// <summary>
    /// The Poser folder in Documents, which holds each of Poser's library
    /// folders. It is in Documents, next to the other posing tools' folders,
    /// rather than with the plugin's settings, because poses, scenes and MCDFs
    /// are files people share and back up.
    /// </summary>
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

    /// <summary>The folder that holds Poser's library folders: the folder
    /// above the Poses folder if that is still called Poses, otherwise the
    /// default.</summary>
    public string ResolveRoot()
    {
        // A switched-off Poses folder still decides where Poser's folders are.
        // Folders of other tools never change it.
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

    /// <summary>Where the Poses folder is by default.</summary>
    public static string DefaultPoseRoot => HomeRoot("Poses");

    /// <summary>Where the Scenes folder is by default.</summary>
    public static string DefaultSceneRoot => HomeRoot("Scenes");

    /// <summary>Where the MCDFs folder is by default.</summary>
    public static string DefaultMcdfRoot => HomeRoot("MCDFs");

    /// <summary>Where the Objects folder is by default.</summary>
    public static string DefaultObjectsRoot => HomeRoot("Objects");

    /// <summary>
    /// The folder a save of this kind goes to: the matching Poser folder in
    /// the library's list when it is switched on, otherwise its default path.
    /// If the folder was moved, saves go to where it is now. If it was removed
    /// or switched off, saves go to the default path.
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
    public string ResolveSceneRoot() =>
        ResolveHomeRoot(SceneSourceName, DefaultSceneRoot);

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolveMcdfRoot() =>
        ResolveHomeRoot(McdfSourceName, DefaultMcdfRoot);

    /// <inheritdoc cref="ResolveHomeRoot"/>
    public string ResolveObjectsRoot() =>
        ResolveHomeRoot(ObjectsSourceName, DefaultObjectsRoot);

    /// <summary>
    /// Creates one of Poser's library folders if it is missing, and returns
    /// its path for a file dialog to open at. If creating it fails, the same
    /// path is still returned rather than Documents; code that writes a
    /// library file calls <see cref="TryEnsureDirectory"/> itself and stops
    /// when it fails. Safe to call any number of times.
    /// </summary>
    public string EnsureHomeRootExists(string sourceName, string shipped)
    {
        var root = ResolveHomeRoot(sourceName, shipped);
        _ = TryEnsureDirectory(root, out _);
        return root;
    }

    /// <summary>Creates the folder right before a library file is written
    /// into it, and says why when it cannot. It creates only the folder asked
    /// for.</summary>
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

    /// <summary>
    /// A path for a new library file, so a save never overwrites one: the
    /// cleaned-up name, or the name with the date and time added when that
    /// file already exists. The year has all four digits, because the name is
    /// read back for as long as the file exists.
    /// </summary>
    public static string NewEntryPath(string root, string name, string extension)
    {
        var cleaned = Files.FileNamePolicy.GeneratedStem(name, "Entry");
        var path = Path.Combine(root, cleaned + extension);
        if (File.Exists(path))
            path = Path.Combine(
                root,
                string.Create(CultureInfo.InvariantCulture,
                    $"{cleaned} {DateTime.Now:yyyy-MM-dd HH.mm.ss}{extension}"));
        return path;
    }

    /// <summary>Creates Poser's library folders at startup where it can. A
    /// folder that cannot be created shows up as a problem in the library's
    /// folder list; startup never fails because of it.</summary>
    public void EnsureHomeRootsExist()
    {
        // Only Poser's own folders that are switched on are created here.
        // Folders of other tools, and switched-off folders, are left alone.
        foreach (var source in Sources)
        {
            if (!source.Enabled)
                continue;
            if (IsManaged(Classify(source)))
                _ = TryEnsureDirectory(source.Path, out _);
        }
    }

    /// <summary>
    /// Adds the Brio and Anamnesis pose folders to the library the first time
    /// only, and each of Poser's own folders once, each under its own flag.
    /// </summary>
    public void EnsureDefaults() =>
        EnsureDefaults(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // Tests pass a throwaway Documents folder here.
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
        // Nothing in the interface removes the Objects folder, so a missing
        // one is put back at startup. Older versions could drop it from the
        // list when saving settings.
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
