using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Library;

/// <summary>
/// What a scanned file IS. The browser shows one kind at a time, so this is the
/// dimension its type tabs filter on.
/// </summary>
public enum PoseLibraryEntryKind : byte
{
    /// <summary>A <c>.pose</c> or Anamnesis <c>.cmp</c>.</summary>
    Pose,

    /// <summary>A Mare character file. Carries no pose metadata at all — no
    /// author, no tags, no preview — so the scan never opens one.</summary>
    Mcdf,
}

/// <summary>Why a pose metadata probe did or did not produce metadata.</summary>
public enum PoseLibraryMetadataStatus : byte
{
    Valid,
    Corrupt,
    Future,
    Oversized,
}

/// <summary>
/// One library file the scan found. Every string the browser reads per frame is
/// minted here, at scan time, because the grid touches all of them on each
/// keystroke and may allocate nothing while doing it.
/// </summary>
public sealed class PoseLibraryEntry
{
    public required PoseLibraryEntryKind Kind { get; init; }

    /// <summary>Absolute path of the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name without its extension.</summary>
    public required string Name { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Name"/> for search.</summary>
    public required string NameLower { get; init; }

    /// <summary>Last write time pre-formatted as <c>yyyy-MM-dd HH:mm</c>.</summary>
    public required string ModifiedText { get; init; }

    public required DateTime Modified { get; init; }

    /// <summary>Index into the owning snapshot's <c>Folders</c> list.</summary>
    public required int Folder { get; init; }

    public string? Author { get; init; }

    private IReadOnlyList<string> _tags = Array.Empty<string>();
    private IReadOnlyList<string> _tagsLower = Array.Empty<string>();

    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init => _tags = Freeze(value);
    }

    /// <summary>Invariant lowercase copies of <see cref="Tags"/>, same order.</summary>
    public IReadOnlyList<string> TagsLower
    {
        get => _tagsLower;
        init => _tagsLower = Freeze(value);
    }

    public PoseLibraryMetadataStatus MetadataStatus { get; init; }

    /// <summary>Short diagnostic suitable for a future recovery surface.</summary>
    public string MetadataDetail { get; init; } = string.Empty;

    /// <summary>An Anamnesis <c>.cmp</c> file; carries no metadata.</summary>
    public bool IsLegacy { get; init; }

    /// <summary>A <c>.pose</c> file with a non-empty embedded preview image.</summary>
    public bool HasThumbnail { get; init; }

    private static IReadOnlyList<string> Freeze(IReadOnlyList<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>()).ToArray());
}

/// <summary>
/// One node of the flattened folder tree. Roots are the configured sources.
/// </summary>
public sealed class PoseLibraryFolder
{
    /// <summary>
    /// <c>"&lt;sourceIndex&gt;|&lt;relative dir&gt;"</c>; the relative part is
    /// empty for a source root.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Source name for a root, directory name for a subfolder.</summary>
    public required string Label { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Label"/> for search.</summary>
    public required string LabelLower { get; init; }

    /// <summary>0 for a source root, +1 per nesting level.</summary>
    public required int Depth { get; init; }

    /// <summary>Recursive file count at and below this folder, both kinds.
    /// </summary>
    public int Count { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Pose"/> count at and
    /// below this folder.</summary>
    public int PoseCount { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Mcdf"/> count at and
    /// below this folder. Both per-kind counts are recursive, so a folder with
    /// none of a kind has no descendant of that kind either — which is what
    /// lets a browser tab drop the whole subtree and keep a valid tree.
    /// </summary>
    public int McdfCount { get; init; }
}

/// <summary>
/// One immutable result of a scan. Readers take the whole snapshot in a single
/// reference read; it is never mutated after publication.
/// </summary>
public sealed class PoseLibrarySnapshot
{
    public required int Revision { get; init; }

    /// <summary>Sorted by folder order, then <c>NameLower</c> ordinal.</summary>
    private IReadOnlyList<PoseLibraryEntry> _entries = Array.Empty<PoseLibraryEntry>();
    private IReadOnlyList<PoseLibraryFolder> _folders = Array.Empty<PoseLibraryFolder>();

    public required IReadOnlyList<PoseLibraryEntry> Entries
    {
        get => _entries;
        init => _entries = Freeze(value);
    }

    /// <summary>Flattened depth-first in display order.</summary>
    public required IReadOnlyList<PoseLibraryFolder> Folders
    {
        get => _folders;
        init => _folders = Freeze(value);
    }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<T>()).ToArray());
}
