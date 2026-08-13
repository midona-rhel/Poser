using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Poser.Config;
using Poser.Files;

namespace Poser.Library;

/// <inheritdoc cref="IPoseLibraryService"/>
public sealed class PoseLibraryService : IPoseLibraryService
{
    private const string PoseExtension = ".pose";
    private const string LegacyExtension = ".cmp";
    private const string McdfExtension = ".mcdf";

    private static readonly PoseLibrarySnapshot EmptySnapshot = new()
    {
        Revision = 0,
        Entries = [],
        Folders = []
    };

    private readonly ConfigurationService _config;
    private readonly AtomicPoseFileStore _poseStore;
    private readonly object _sync = new();

    private PoseLibrarySnapshot _snapshot = EmptySnapshot;
    private string _sourceSignature;
    private CancellationTokenSource? _scanCancellation;
    private long _generation;
    private bool _scanning;
    private bool _scanQueued;
    private bool _disposed;

    public PoseLibraryService(ConfigurationService config)
        : this(config, AtomicPoseFileStore.Default)
    {
    }

    internal PoseLibraryService(
        ConfigurationService config,
        AtomicPoseFileStore poseStore)
    {
        _config = config;
        _poseStore = poseStore;
        _sourceSignature = BuildSourceSignature();
        _config.OnConfigurationChanged += OnConfigurationChanged;
    }

    public PoseLibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool IsScanning
    {
        get
        {
            lock (_sync)
                return _scanning;
        }
    }

    public void RequestScan()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _generation++;
            if (_scanning)
            {
                _scanQueued = true;
                _scanCancellation?.Cancel();
                return;
            }

            _scanning = true;
            _scanCancellation = new CancellationTokenSource();
        }

        _ = Task.Run(ScanLoop);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _scanQueued = false;
            _scanCancellation?.Cancel();
        }

        _config.OnConfigurationChanged -= OnConfigurationChanged;
    }

    // A config save fires for every setting; only a change to which roots are
    // scanned, their names (the root folder labels), or their order
    // invalidates the snapshot.
    private void OnConfigurationChanged()
    {
        var signature = BuildSourceSignature();
        lock (_sync)
        {
            if (_disposed || string.Equals(signature, _sourceSignature, StringComparison.Ordinal))
                return;
            _sourceSignature = signature;
        }

        RequestScan();
    }

    private string BuildSourceSignature()
    {
        var builder = new StringBuilder();
        foreach (var source in _config.Config.Library.Sources)
        {
            if (!source.Enabled)
                continue;

            builder.Append(source.Name);
            builder.Append('\0');
            builder.Append(source.Path);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private void ScanLoop()
    {
        while (true)
        {
            CancellationToken token;
            long generation;
            lock (_sync)
            {
                if (_disposed || _scanCancellation is null)
                {
                    _scanning = false;
                    return;
                }

                _scanQueued = false;
                token = _scanCancellation.Token;
                generation = _generation;
            }

            try
            {
                RunScan(generation, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation abandons the whole pass; no partial result is
                // ever handed to the reader.
            }
            catch (ScanAbortException)
            {
                // IO, traversal, and bound failures retain the last snapshot.
            }
            catch (Exception)
            {
                // A scan never surfaces: a failed pass leaves the previous
                // snapshot in place rather than tearing down the browser.
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    _scanning = false;
                    _scanCancellation?.Dispose();
                    _scanCancellation = null;
                    return;
                }

                if (_scanQueued)
                {
                    _scanCancellation?.Dispose();
                    _scanCancellation = new CancellationTokenSource();
                    continue;
                }

                _scanning = false;
                _scanCancellation?.Dispose();
                _scanCancellation = null;
                return;
            }
        }
    }

    private void RunScan(long generation, CancellationToken cancellation)
    {
        var folders = new List<PoseLibraryFolder>();
        var entries = new List<PoseLibraryEntry>();
        var folderCount = 0;
        var fileCount = 0;

        var sources = _config.Config.Library.Sources
            .Select(source => new SourceSpec(source.Name, source.Path, source.Enabled))
            .ToArray();

        for (var i = 0; i < sources.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = sources[i];
            if (!source.Enabled || string.IsNullOrWhiteSpace(source.Path))
                continue;

            if (!SafeDirectoryExists(source.Path))
                continue;

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
                Flatten(root, folders, entries, cancellation);
        }

        entries.Sort(static (a, b) =>
        {
            var byFolder = a.Folder.CompareTo(b.Folder);
            return byFolder != 0 ? byFolder : string.CompareOrdinal(a.NameLower, b.NameLower);
        });

        cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposed || generation != _generation || cancellation.IsCancellationRequested)
                return;

            // Single reference swap is the last step, so a reader either sees
            // the whole previous snapshot or the whole new one.
            var revision = _snapshot.Revision + 1;
            Volatile.Write(ref _snapshot, new PoseLibrarySnapshot
            {
                Revision = revision,
                Entries = entries,
                Folders = folders
            });
        }
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
    }

    private readonly record struct SourceSpec(string Name, string Path, bool Enabled);

    /// <summary>
    /// Builds one directory subtree. Any traversal failure or bound breach
    /// aborts the pass, because publishing a partial tree is misleading.
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
            throw new ScanAbortException("The pose library directory depth exceeded its bound.");
        if (++folderCount > PoseLibraryLimits.MaxFolders)
            throw new ScanAbortException("The pose library folder count exceeded its bound.");

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
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsLibraryFile(file))
                    continue;
                if (++fileCount > PoseLibraryLimits.MaxFiles)
                    throw new ScanAbortException("The pose library file count exceeded its bound.");
                files.Add(file);
            }
        }
        catch (Exception ex)
        {
            if (ex is ScanAbortException or OperationCanceledException)
                throw;
            throw new ScanAbortException($"Enumerating pose library files failed: {ex.Message}", ex);
        }

        node.Files.AddRange(files);

        var subdirectories = new List<string>();
        try
        {
            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (folderCount + subdirectories.Count + 1 > PoseLibraryLimits.MaxFolders)
                    throw new ScanAbortException("The pose library folder count exceeded its bound.");
                subdirectories.Add(subdirectory);
            }
        }
        catch (Exception ex)
        {
            if (ex is ScanAbortException or OperationCanceledException)
                throw;
            throw new ScanAbortException($"Enumerating pose library folders failed: {ex.Message}", ex);
        }

        subdirectories.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var subdirectory in subdirectories)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Path.GetFileName(subdirectory);
            if (string.IsNullOrEmpty(name))
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
            if (KindOf(file) == PoseLibraryEntryKind.Mcdf)
                node.McdfCount++;
            else
                node.PoseCount++;
        }

        foreach (var child in node.Children)
        {
            node.Count += child.Count;
            node.PoseCount += child.PoseCount;
            node.McdfCount += child.McdfCount;
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
            McdfCount = node.McdfCount
        });

        foreach (var file in node.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            entries.Add(CreateEntry(file, folderIndex));
        }

        foreach (var child in node.Children)
            Flatten(child, folders, entries, cancellation);
    }

    private PoseLibraryEntry CreateEntry(string filePath, int folderIndex)
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

        string? author = null;
        string? version = null;
        IReadOnlyList<string> tags = [];
        IReadOnlyList<string> tagsLower = [];
        var hasThumbnail = false;
        var status = PoseLibraryMetadataStatus.Valid;
        var detail = string.Empty;

        // A .cmp has no header and an .mcdf is a compressed archive: opening
        // either would cost a read that can never answer.
        if (kind == PoseLibraryEntryKind.Pose && !isLegacy)
        {
            var metadata = _poseStore.ReadMetadata(filePath);
            if (metadata.Succeeded)
            {
                author = metadata.Author;
                version = metadata.Version;
                tags = metadata.Tags;
                tagsLower = tags.Select(tag => tag.ToLowerInvariant()).ToArray();
                hasThumbnail = metadata.HasThumbnail;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    status = PoseLibraryMetadataStatus.Future;
                    detail = $"Pose version '{version}' is not supported.";
                }
            }
            else
            {
                status = metadata.Failure?.Kind == PoseFileStoreFailureKind.SizeLimit
                    ? PoseLibraryMetadataStatus.Oversized
                    : PoseLibraryMetadataStatus.Corrupt;
                detail = metadata.Failure?.Detail ?? "The pose metadata could not be read.";
            }
        }

        return new PoseLibraryEntry
        {
            Kind = kind,
            FilePath = filePath,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            ModifiedText = modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Modified = modified,
            Folder = folderIndex,
            Author = author,
            Tags = tags,
            TagsLower = tagsLower,
            MetadataStatus = status,
            MetadataDetail = detail,
            IsLegacy = isLegacy,
            HasThumbnail = hasThumbnail
        };
    }

    private static bool IsLibraryFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(PoseExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(McdfExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static PoseLibraryEntryKind KindOf(string path) =>
        Path.GetExtension(path).Equals(McdfExtension, StringComparison.OrdinalIgnoreCase)
            ? PoseLibraryEntryKind.Mcdf
            : PoseLibraryEntryKind.Pose;

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class ScanAbortException : Exception
    {
        public ScanAbortException(string message)
            : base(message)
        {
        }

        public ScanAbortException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
