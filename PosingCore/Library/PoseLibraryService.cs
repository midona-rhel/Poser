using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Poser.Config;

namespace Poser.Library;

/// <inheritdoc cref="IPoseLibraryService"/>
public sealed class PoseLibraryService : IPoseLibraryService
{
    private const string PoseExtension = ".pose";
    private const string LegacyExtension = ".cmp";

    private static readonly PoseLibrarySnapshot EmptySnapshot = new()
    {
        Revision = 0,
        Entries = [],
        Folders = []
    };

    private readonly ConfigurationService _config;
    private readonly object _sync = new();

    private PoseLibrarySnapshot _snapshot = EmptySnapshot;
    private string _sourceSignature;
    private bool _scanning;
    private bool _scanQueued;
    private bool _disposed;

    public PoseLibraryService(ConfigurationService config)
    {
        _config = config;
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

            if (_scanning)
            {
                _scanQueued = true;
                return;
            }

            _scanning = true;
        }

        Task.Run(ScanLoop);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _scanQueued = false;
        }

        _config.OnConfigurationChanged -= OnConfigurationChanged;
    }

    // A config save fires for every setting; only a change to which roots are
    // scanned, their names (the root folder labels), or their order
    // invalidates the snapshot.
    private void OnConfigurationChanged()
    {
        var signature = BuildSourceSignature();
        if (string.Equals(signature, _sourceSignature, StringComparison.Ordinal))
            return;

        _sourceSignature = signature;
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
            try
            {
                RunScan();
            }
            catch (Exception)
            {
                // A scan never surfaces: a failed pass leaves the previous
                // snapshot in place rather than tearing down the browser.
            }

            lock (_sync)
            {
                if (_disposed || !_scanQueued)
                {
                    _scanning = false;
                    return;
                }

                _scanQueued = false;
            }
        }
    }

    private void RunScan()
    {
        var folders = new List<PoseLibraryFolder>();
        var entries = new List<PoseLibraryEntry>();

        var sources = _config.Config.Library.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!source.Enabled || string.IsNullOrWhiteSpace(source.Path))
                continue;

            if (!SafeDirectoryExists(source.Path))
                continue;

            var root = BuildNode(i, source.Name, source.Path, "", 0, isRoot: true);
            if (root is not null)
                Flatten(root, folders, entries);
        }

        entries.Sort(static (a, b) =>
        {
            var byFolder = a.Folder.CompareTo(b.Folder);
            return byFolder != 0 ? byFolder : string.CompareOrdinal(a.NameLower, b.NameLower);
        });

        // Single reference swap is the last step, so a reader either sees the
        // whole previous snapshot or the whole new one.
        var revision = Volatile.Read(ref _snapshot).Revision + 1;
        Volatile.Write(ref _snapshot, new PoseLibrarySnapshot
        {
            Revision = revision,
            Entries = entries,
            Folders = folders
        });
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
    }

    /// <summary>
    /// Builds one directory subtree. Returns null for a subfolder holding no
    /// pose at or below it; a source root is always kept so a configured but
    /// empty root still lists.
    /// </summary>
    private static ScanNode? BuildNode(
        int sourceIndex,
        string label,
        string directory,
        string relativePath,
        int depth,
        bool isRoot)
    {
        var node = new ScanNode
        {
            SourceIndex = sourceIndex,
            RelativePath = relativePath,
            Label = label,
            Depth = depth
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsPoseFile(file))
                    node.Files.Add(file);
            }
        }
        catch (Exception)
        {
            // Unreadable directory contributes nothing.
        }

        var subdirectories = new List<string>();
        try
        {
            subdirectories.AddRange(Directory.EnumerateDirectories(directory));
        }
        catch (Exception)
        {
        }

        subdirectories.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var subdirectory in subdirectories)
        {
            var name = Path.GetFileName(subdirectory);
            if (string.IsNullOrEmpty(name))
                continue;

            var childRelative = relativePath.Length == 0 ? name : Path.Combine(relativePath, name);
            var child = BuildNode(sourceIndex, name, subdirectory, childRelative, depth + 1, isRoot: false);
            if (child is not null)
                node.Children.Add(child);
        }

        node.Count = node.Files.Count;
        foreach (var child in node.Children)
            node.Count += child.Count;

        return !isRoot && node.Count == 0 ? null : node;
    }

    private static void Flatten(
        ScanNode node,
        List<PoseLibraryFolder> folders,
        List<PoseLibraryEntry> entries)
    {
        var folderIndex = folders.Count;
        folders.Add(new PoseLibraryFolder
        {
            Key = $"{node.SourceIndex}|{node.RelativePath}",
            Label = node.Label,
            LabelLower = node.Label.ToLowerInvariant(),
            Depth = node.Depth,
            Count = node.Count
        });

        foreach (var file in node.Files)
            entries.Add(CreateEntry(file, folderIndex));

        foreach (var child in node.Children)
            Flatten(child, folders, entries);
    }

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

        var isLegacy = Path.GetExtension(filePath).Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase);

        string? author = null;
        IReadOnlyList<string> tags = [];
        IReadOnlyList<string> tagsLower = [];
        var hasThumbnail = false;

        if (!isLegacy)
            ReadPoseMetadata(filePath, out author, out tags, out tagsLower, out hasThumbnail);

        return new PoseLibraryEntry
        {
            FilePath = filePath,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            ModifiedText = modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Modified = modified,
            Folder = folderIndex,
            Author = author,
            Tags = tags,
            TagsLower = tagsLower,
            IsLegacy = isLegacy,
            HasThumbnail = hasThumbnail
        };
    }

    /// <summary>
    /// Reads the header fields only. The bone dictionaries are never
    /// materialized and the preview image is never retained — the scan answers
    /// whether one exists, nothing more.
    /// </summary>
    private static void ReadPoseMetadata(
        string filePath,
        out string? author,
        out IReadOnlyList<string> tags,
        out IReadOnlyList<string> tagsLower,
        out bool hasThumbnail)
    {
        author = null;
        tags = [];
        tagsLower = [];
        hasThumbnail = false;

        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(File.ReadAllBytes(filePath)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (root.TryGetProperty("Author", out var authorElement)
                && authorElement.ValueKind == JsonValueKind.String)
            {
                author = authorElement.GetString();
            }

            if (root.TryGetProperty("Tags", out var tagsElement)
                && tagsElement.ValueKind == JsonValueKind.Array)
            {
                var values = new List<string>(tagsElement.GetArrayLength());
                var lowered = new List<string>(tagsElement.GetArrayLength());
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    if (tag.ValueKind != JsonValueKind.String)
                        continue;

                    var value = tag.GetString();
                    if (string.IsNullOrEmpty(value))
                        continue;

                    values.Add(value);
                    lowered.Add(value.ToLowerInvariant());
                }

                tags = values;
                tagsLower = lowered;
            }

            if (root.TryGetProperty("Base64Image", out var imageElement)
                && imageElement.ValueKind == JsonValueKind.String)
            {
                // ValueEquals compares against the raw UTF-8 span, so the
                // encoded image is never allocated as a string.
                hasThumbnail = !imageElement.ValueEquals(string.Empty);
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable pose still lists, just without metadata.
        }
    }

    private static bool IsPoseFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(PoseExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase);
    }

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
}
