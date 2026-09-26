using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Poser.Files;

namespace Poser.Library;

public sealed record LibrarySourceSpec(string Name, string Path, bool Enabled);

/// <summary>Bounded filesystem traversal. The caller owns scheduling and publication.</summary>
public sealed class LibraryScanner
{
    private const string PoseExtension = ".pose";
    private const string LegacyExtension = ".cmp";
    private const string McdfExtension = ".mcdf";
    private static readonly string SceneExtension = SceneFile.Extension;
    private static readonly string ActorExtension = SceneFile.ActorEntryExtension;
    private const string LightExtension = ".xivl";
    private const string CameraExtension = ".xivc";
    private static readonly string EnvironmentExtension =
        SceneFile.EnvironmentEntryExtension;
    private static readonly string GroupExtension =
        SceneFile.GroupEntryExtension;
    private static readonly string WorldObjectExtension =
        SceneFile.WorldObjectEntryExtension;
    private static readonly string PropExtension =
        SceneFile.PropEntryExtension;
    private static readonly string OverlayExtension =
        SceneFile.OverlayEntryExtension;

    private readonly Func<string, bool>? _observeDirectory;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly Func<string, IEnumerable<string>> _enumerateDirectories;
    private readonly int _maxFiles;
    private readonly int _maxFolders;
    private readonly int _maxSources;
    public LibraryScanner(
        Func<string, bool>? observeDirectory = null,
        Func<string, IEnumerable<string>>? enumerateFiles = null,
        Func<string, IEnumerable<string>>? enumerateDirectories = null,
        int maxFiles = PoseLibraryLimits.MaxFiles,
        int maxFolders = PoseLibraryLimits.MaxFolders,
        int maxSources = PoseLibraryLimits.MaxSources)
    {
        _observeDirectory = observeDirectory;
        _enumerateFiles = enumerateFiles ?? Directory.EnumerateFiles;
        _enumerateDirectories = enumerateDirectories ?? Directory.EnumerateDirectories;
        _maxFiles = Math.Clamp(maxFiles, 1, PoseLibraryLimits.MaxFiles);
        _maxFolders = Math.Clamp(maxFolders, 1, PoseLibraryLimits.MaxFolders);
        _maxSources = Math.Clamp(maxSources, 1, PoseLibraryLimits.MaxSources);
    }

    public PoseLibrarySnapshot Scan(
        IReadOnlyList<LibrarySourceSpec> configuredSources, long generation, int revision,
        CancellationToken cancellation)
    {
        var folders = new List<PoseLibraryFolder>();
        var entries = new List<PoseLibraryEntry>();
        var sources = configuredSources.Take(_maxSources).ToArray();
        var skippedSources = Math.Max(0, configuredSources.Count - sources.Length);

        var sourceSnapshots = new List<PoseLibrarySourceSnapshot>(sources.Length);
        var failedSources = skippedSources;
        var readySources = 0;
        for (var i = 0; i < sources.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = sources[i];
            if (!source.Enabled)
            {
                sourceSnapshots.Add(new PoseLibrarySourceSnapshot
                {
                    Index = i,
                    Name = source.Name,
                    Path = source.Path,
                    Enabled = false,
                    Health = PoseLibrarySourceHealth.Disabled,
                    Detail = "Source is disabled."
                });
                continue;
            }

            var observation = ObserveSource(source.Path);
            if (observation.Health != PoseLibrarySourceHealth.Ready)
            {
                failedSources++;
                sourceSnapshots.Add(new PoseLibrarySourceSnapshot
                {
                    Index = i,
                    Name = source.Name,
                    Path = source.Path,
                    Enabled = true,
                    Health = observation.Health,
                    Detail = observation.Detail
                });
                continue;
            }

            // Validate each source before reserving aggregate capacity. A huge
            // or broken source leaves the remaining budget for later roots.
            var folderCount = 0;
            var fileCount = 0;
            try
            {
                var root = BuildNode(
                    i,
                    source.Name,
                    source.Path,
                    "",
                    0,
                    isRoot: true,
                    cancellation,
                    ref folderCount,
                    ref fileCount);
                if (root is not null)
                {
                    if (entries.Count + root.Count > _maxFiles
                        || folders.Count + CountFolders(root) > _maxFolders)
                        throw new ScanAbortException(
                            $"Source '{source.Path}' exceeds the remaining library capacity " +
                            $"({_maxFiles} files / {_maxFolders} folders overall). Disable another source or narrow this root.");
                    // The entire tree was validated before appending. Flatten
                    // assigns indexes directly into the aggregate folder list.
                    Flatten(root, folders, entries, cancellation);
                }
                readySources++;
                sourceSnapshots.Add(new PoseLibrarySourceSnapshot
                {
                    Index = i,
                    Name = source.Name,
                    Path = source.Path,
                    Enabled = true,
                    Health = PoseLibrarySourceHealth.Ready
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ScanAbortException ex)
            {
                failedSources++;
                sourceSnapshots.Add(new PoseLibrarySourceSnapshot
                {
                    Index = i,
                    Name = source.Name,
                    Path = source.Path,
                    Enabled = true,
                    Health = ex.Health,
                    Detail = BoundDetail(ex.Message)
                });
            }
        }

        entries.Sort(static (a, b) =>
        {
            var byFolder = a.Folder.CompareTo(b.Folder);
            return byFolder != 0 ? byFolder : string.CompareOrdinal(a.NameLower, b.NameLower);
        });

        cancellation.ThrowIfCancellationRequested();
        return new PoseLibrarySnapshot
            {
                Revision = revision,
                Generation = generation,
                TerminalResult = failedSources == 0
                    ? PoseLibraryScanResult.Success
                    : readySources == 0
                        ? PoseLibraryScanResult.Failure
                        : PoseLibraryScanResult.PartialFailure,
                Entries = entries,
                Folders = folders,
                Sources = sourceSnapshots,
                SkippedSourceCount = skippedSources
            };
    }

    private sealed class ScanNode
    {
        public required int SourceIndex { get; init; }
        public required string RelativePath { get; init; }
        public required string Label { get; init; }
        public required int Depth { get; init; }
        public List<string> Files { get; } = [];
        public List<ScanNode> Children { get; } = [];
        public int Count { get; set; }
        public int PoseCount { get; set; }
        public int McdfCount { get; set; }
        public int SceneCount { get; set; }
        public int ObjectsCount { get; set; }
    }

    private static int CountFolders(ScanNode node) =>
        1 + node.Children.Sum(CountFolders);

    /// <summary>
    /// Builds one directory subtree. Any traversal failure or bound breach
    /// rejects this source, because publishing a partial tree is misleading.
    /// </summary>
    private ScanNode? BuildNode(
        int sourceIndex,
        string label,
        string directory,
        string relativePath,
        int depth,
        bool isRoot,
        CancellationToken cancellation,
        ref int folderCount,
        ref int fileCount)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > PoseLibraryLimits.MaxDepth)
            throw new ScanAbortException($"Folder '{directory}' exceeds the nesting limit.");
        var observation = ObserveSource(directory);
        if (observation.Health != PoseLibrarySourceHealth.Ready)
            throw new ScanAbortException(
                $"Folder '{directory}': {observation.Detail}", observation.Health);
        if (++folderCount > _maxFolders)
            throw new ScanAbortException($"Source traversal at '{directory}' exceeds the folder limit.");

        var node = new ScanNode
        {
            SourceIndex = sourceIndex,
            RelativePath = relativePath,
            Label = label,
            Depth = depth
        };

        var files = new List<string>();
        try
        {
            foreach (var file in _enumerateFiles(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsLibraryFile(file))
                    continue;
                if (++fileCount > _maxFiles)
                    throw new ScanAbortException($"Source traversal at '{directory}' exceeds the file limit.");
                files.Add(file);
            }
        }
        catch (Exception ex)
        {
            if (ex is ScanAbortException or OperationCanceledException)
                throw;
            throw new ScanAbortException($"Reading files in '{directory}' failed: {ex.Message}", ex);
        }

        node.Files.AddRange(files);

        var subdirectories = new List<string>();
        try
        {
            foreach (var subdirectory in _enumerateDirectories(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (folderCount + subdirectories.Count + 1 > _maxFolders)
                    throw new ScanAbortException($"Source traversal at '{directory}' exceeds the folder limit.");
                subdirectories.Add(subdirectory);
            }
        }
        catch (Exception ex)
        {
            if (ex is ScanAbortException or OperationCanceledException)
                throw;
            throw new ScanAbortException($"Reading folders in '{directory}' failed: {ex.Message}", ex);
        }

        subdirectories.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var subdirectory in subdirectories)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Path.GetFileName(subdirectory);
            if (string.IsNullOrEmpty(name))
                continue;
            // Quarantined files are evidence, not library content: the
            // quarantine verb moves a bad file here precisely so the next
            // complete pass publishes without it.
            if (name.Equals(
                    PoseLibraryFileActions.QuarantineFolderName,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var childRelative = relativePath.Length == 0 ? name : Path.Combine(relativePath, name);
            var child = BuildNode(
                sourceIndex,
                name,
                subdirectory,
                childRelative,
                depth + 1,
                isRoot: false,
                cancellation,
                ref folderCount,
                ref fileCount);
            if (child is not null)
                node.Children.Add(child);
        }

        node.Count = node.Files.Count;
        foreach (var file in node.Files)
        {
            switch (KindOf(file))
            {
                case PoseLibraryEntryKind.Mcdf:
                case PoseLibraryEntryKind.Chara:
                    node.McdfCount++;
                    break;
                case PoseLibraryEntryKind.Scene:
                    node.SceneCount++;
                    break;
                case PoseLibraryEntryKind.Actor:
                case PoseLibraryEntryKind.Light:
                case PoseLibraryEntryKind.Camera:
                case PoseLibraryEntryKind.Environment:
                case PoseLibraryEntryKind.Overlay:
                case PoseLibraryEntryKind.Group:
                case PoseLibraryEntryKind.WorldObject:
                case PoseLibraryEntryKind.Prop:
                    node.ObjectsCount++;
                    break;
                default:
                    node.PoseCount++;
                    break;
            }
        }

        foreach (var child in node.Children)
        {
            node.Count += child.Count;
            node.PoseCount += child.PoseCount;
            node.McdfCount += child.McdfCount;
            node.SceneCount += child.SceneCount;
            node.ObjectsCount += child.ObjectsCount;
        }

        return !isRoot && node.Count == 0 ? null : node;
    }
    private void Flatten(
        ScanNode node,
        List<PoseLibraryFolder> folders,
        List<PoseLibraryEntry> entries,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var folderIndex = folders.Count;
        folders.Add(new PoseLibraryFolder
        {
            Key = $"{node.SourceIndex}|{node.RelativePath}",
            Label = node.Label,
            LabelLower = node.Label.ToLowerInvariant(),
            Depth = node.Depth,
            Count = node.Count,
            PoseCount = node.PoseCount,
            McdfCount = node.McdfCount,
            SceneCount = node.SceneCount,
            ObjectsCount = node.ObjectsCount
        });

        foreach (var file in node.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            entries.Add(CreateEntry(file, folderIndex));
        }

        foreach (var child in node.Children)
            Flatten(child, folders, entries, cancellation);
    }

    /// <summary>One entry from the directory listing alone: name, kind,
    /// stamp. Only shared .xivw entries need their small scene document read
    /// here to distinguish scenery, furniture and VFX. Other metadata stays
    /// lazy; packages and thumbnails are never opened by the background scan.</summary>
    private static PoseLibraryEntry CreateEntry(string filePath, int folderIndex)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        DateTime modified;
        try
        {
            modified = File.GetLastWriteTime(filePath);
        }
        catch (Exception)
        {
            modified = default;
        }
        var kind = KindOf(filePath);
        var isLegacy = kind == PoseLibraryEntryKind.Pose
            && Path.GetExtension(filePath).Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase);
        var worldKind = kind == PoseLibraryEntryKind.WorldObject
            ? Services.WorldAsset.KindOf(SceneFileStore.Default.Read(filePath).Scene?.WorldObjects?.FirstOrDefault()?.Path)
            : Services.WorldAssetKind.Scenery;
        return new PoseLibraryEntry
        {
            Kind = kind,
            WorldKind = worldKind,
            FilePath = filePath,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            ModifiedText = modified.ToString(
                LibraryStamp.DateTimeFormat, CultureInfo.InvariantCulture),
            Modified = modified,
            Folder = folderIndex,
            Author = null,
            AuthorLower = string.Empty,
            Tags = [],
            TagsLower = [],
            MetadataStatus = PoseLibraryMetadataStatus.Valid,
            MetadataDetail = string.Empty,
            IsLegacy = isLegacy,
            // A pose file may carry a thumbnail; the tile asks the cache,
            // which reads the file only when the tile is on screen.
            HasThumbnail = kind == PoseLibraryEntryKind.Pose && !isLegacy,
            SceneContents = string.Empty,
            ScenePlace = string.Empty,
            SceneCapturedAt = null
        };
    }

    /// <summary>What a scene holds, for its tile once it is selected.</summary>
    public static string DescribeScene(SceneMetadataReadOutcome metadata)
    {
        var parts = new List<string>(4);
        if (metadata.ActorCount > 0)
            parts.Add($"{metadata.ActorCount} actor{(metadata.ActorCount == 1 ? "" : "s")}");
        if (metadata.PropCount > 0)
            parts.Add($"{metadata.PropCount} object{(metadata.PropCount == 1 ? "" : "s")}");
        if (metadata.LightCount > 0)
            parts.Add($"{metadata.LightCount} light{(metadata.LightCount == 1 ? "" : "s")}");
        if (metadata.CameraCount > 0)
            parts.Add($"{metadata.CameraCount} camera{(metadata.CameraCount == 1 ? "" : "s")}");
        return parts.Count == 0 ? "Empty scene" : string.Join(", ", parts);
    }

    private static bool IsLibraryFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(PoseExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(McdfExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".chara", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(SceneExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(ActorExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LightExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(CameraExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(EnvironmentExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(OverlayExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(GroupExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(WorldObjectExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(PropExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static PoseLibraryEntryKind KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".chara", StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Chara;
        if (extension.Equals(McdfExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Mcdf;
        if (extension.Equals(SceneExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Scene;
        if (extension.Equals(ActorExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Actor;
        if (extension.Equals(LightExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Light;
        if (extension.Equals(CameraExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Camera;
        if (extension.Equals(EnvironmentExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Environment;
        if (extension.Equals(OverlayExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Overlay;
        if (extension.Equals(GroupExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Group;
        if (extension.Equals(WorldObjectExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.WorldObject;
        if (extension.Equals(PropExtension, StringComparison.OrdinalIgnoreCase))
            return PoseLibraryEntryKind.Prop;
        return PoseLibraryEntryKind.Pose;
    }

    private SourceObservation ObserveSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(
                PoseLibrarySourceHealth.Invalid,
                "Source path is blank.");

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new(
                PoseLibrarySourceHealth.Invalid,
                BoundDetail("Source path is invalid: " + ex.Message));
        }

        if (_observeDirectory is not null)
        {
            try
            {
                return _observeDirectory(path)
                    ? new(PoseLibrarySourceHealth.Ready, string.Empty)
                    : new(PoseLibrarySourceHealth.Missing,
                        "Source folder does not exist.");
            }
            catch (UnauthorizedAccessException ex)
            {
                return new(PoseLibrarySourceHealth.Denied,
                    BoundDetail("Access to source folder was denied: " + ex.Message));
            }
            catch (Exception ex)
            {
                return new(PoseLibrarySourceHealth.Failed,
                    BoundDetail("Observing source folder failed: " + ex.Message));
            }
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
                return new(
                    PoseLibrarySourceHealth.Failed,
                    "Configured source path is a file, not a folder.");
            return new(PoseLibrarySourceHealth.Ready, string.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return new(
                PoseLibrarySourceHealth.Missing,
                "Source folder does not exist.");
        }
        catch (FileNotFoundException)
        {
            return new(
                PoseLibrarySourceHealth.Missing,
                "Source folder does not exist.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(
                PoseLibrarySourceHealth.Denied,
                BoundDetail("Access to source folder was denied: " + ex.Message));
        }
        catch (Exception ex)
        {
            return new(
                PoseLibrarySourceHealth.Failed,
                BoundDetail("Observing source folder failed: " + ex.Message));
        }
    }

    private static string BoundDetail(string detail) =>
        detail.Length <= 4096 ? detail : detail[..4096];

    private readonly record struct SourceObservation(
        PoseLibrarySourceHealth Health,
        string Detail);

    private sealed class ScanAbortException : Exception
    {
        public PoseLibrarySourceHealth Health { get; }

        public ScanAbortException(string message,
            PoseLibrarySourceHealth health = PoseLibrarySourceHealth.Failed)
            : base(message)
        {
            Health = health;
        }

        public ScanAbortException(string message, Exception inner)
            : base(message, inner)
        {
            Health = inner is UnauthorizedAccessException
                ? PoseLibrarySourceHealth.Denied
                : PoseLibrarySourceHealth.Failed;
        }
    }
}
