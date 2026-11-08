using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Domain.Library;

/// <summary>
/// File type used to group and filter Library entries.
/// </summary>
public enum PoseLibraryEntryKind : byte
{
    /// <summary>A <c>.pose</c> or Anamnesis <c>.cmp</c>.</summary>
    Pose,

    /// <summary>
    /// A Mare character file, without pose author, tags, or thumbnail metadata.
    /// </summary>
        Mcdf,

    /// <summary>
    /// An .xivs scene. The scanner reads its metadata with the scene codec.
    /// </summary>
        Scene,

    /// <summary>An <c>.xiva</c> actor entry: the scene container restricted
    /// to one actor. Read through the same codec as a scene.</summary>
    Actor,

    /// <summary>An <c>.xivl</c> light document.</summary>
    Light,

    /// <summary>An <c>.xivc</c> camera document.</summary>
    Camera,

    /// <summary>An <c>.xive</c> environment entry: the scene container
    /// restricted to the environment configuration. Read through the same
    /// codec as a scene.</summary>
    Environment,

    /// <summary>An <c>.xivo</c> overlay entry: the scene container
    /// restricted to one overlay node.</summary>
    Overlay,

    /// <summary>
    /// An .xivg file containing a named group and its members.
    /// </summary>
        Group,

    /// <summary>An <c>.xivw</c> world-object entry: one object saved as a
    /// spawnable copy. Read through the same codec as a scene.</summary>
    WorldObject,
    Prop,
    Chara,
}

/// <summary>
/// Compact date formats for Library labels. File and folder names use separate
/// formats with four-digit years so they can be parsed without century ambiguity.
/// </summary>
public static class LibraryStamp
{
    /// <summary>Day and time: every tile, rail entry and metadata line.</summary>
    public const string DateTimeFormat = "yy-MM-dd HH:mm";

    /// <summary>Day alone: an auto-save rail day, a scene section heading.
    /// </summary>
    public const string DateFormat = "yy-MM-dd";
}

/// <summary>Why a pose metadata probe did or did not produce metadata.</summary>
public enum PoseLibraryMetadataStatus : byte
{
    Valid,
    Corrupt,
    Future,
    Oversized,
}

/// <summary>What the most recent library pass learned about one configured
/// source. Disabled and invalid are configuration states; the remaining
/// states describe an enabled source's filesystem health.</summary>
public enum PoseLibrarySourceHealth : byte
{
    Unscanned,
    Ready,
    Missing,
    Denied,
    Failed,
    Disabled,
    Invalid,
}

/// <summary>The terminal result of a library pass. Initial is distinct from a
/// successful empty library so the UI can tell "not scanned" from "nothing
/// found".</summary>
public enum PoseLibraryScanResult : byte
{
    Initial,
    Success,
    PartialFailure,
    Failure,
}

/// <summary>An immutable description of a configured library source and the
/// latest health observed for it.</summary>
public sealed class PoseLibrarySourceSnapshot
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool Enabled { get; init; }
    public required PoseLibrarySourceHealth Health { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// A scanned Library file. Search keys and display labels are prepared during
/// the scan to avoid repeatedly formatting them while drawing or filtering.
/// </summary>
public sealed class PoseLibraryEntry
{
    public required PoseLibraryEntryKind Kind { get; init; }
    public Scene.WorldAssetKind WorldKind { get; init; }

    /// <summary>Absolute path of the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name without its extension.</summary>
    public required string Name { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Name"/> for search.</summary>
    public required string NameLower { get; init; }

    /// <summary>Last write time pre-formatted through
    /// <see cref="LibraryStamp.DateTimeFormat"/>.</summary>
    public required string ModifiedText { get; init; }

    public required DateTime Modified { get; init; }

    /// <summary>Index into the owning snapshot's <c>Folders</c> list.</summary>
    public required int Folder { get; init; }

    public string? Author { get; init; }

    /// <summary>Invariant lowercase copy of <see cref="Author"/> for search;
    /// empty when the file names no author.</summary>
    public string AuthorLower { get; init; } = string.Empty;

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

    /// <summary>
    /// Explanation of a metadata read failure.
    /// </summary>
    public string MetadataDetail { get; init; } = string.Empty;

    /// <summary>An Anamnesis <c>.cmp</c> file; carries no metadata.</summary>
    public bool IsLegacy { get; init; }

    /// <summary>What a <see cref="PoseLibraryEntryKind.Scene"/> entry holds,
    /// pre-formatted at scan time (e.g. "3 actors, 2 lights"). Empty for
    /// every other kind.</summary>
    public string SceneContents { get; init; } = string.Empty;

    /// <summary>
    /// Place recorded in the scene, or empty if the file has no place information.
    /// </summary>
    public string ScenePlace { get; init; } = string.Empty;

    /// <summary>
    /// Capture time recorded in the scene. If absent, the browser falls back to
    /// Modified, which can change when the file is copied or edited.
    /// </summary>
    public DateTimeOffset? SceneCapturedAt { get; init; }

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

    /// <summary>
    /// Total file count in this folder and all its subfolders.
    /// </summary>
    public int Count { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Pose"/> count at and
    /// below this folder.</summary>
    public int PoseCount { get; init; }

    /// <summary>
    /// MCDF file count in this folder and all its subfolders.
    /// </summary>
    public int McdfCount { get; init; }

    /// <summary>Recursive <see cref="PoseLibraryEntryKind.Scene"/> count at
    /// and below this folder, on the same recursive contract.</summary>
    public int SceneCount { get; init; }

    /// <summary>
    /// Object-entry count in this folder and all its subfolders, for the Objects tab.
    /// </summary>
    public int ObjectsCount { get; init; }
}

/// <summary>
/// Result of a Library scan. Collections are copied before the result is shared
/// so readers can use the snapshot while another scan runs.
/// </summary>
public sealed class PoseLibrarySnapshot
{
    public required int Revision { get; init; }

    /// <summary>
    /// Scan request number used to reject results from an older scan.
    /// </summary>
    public required long Generation { get; init; }

    public required PoseLibraryScanResult TerminalResult { get; init; }

    /// <summary>
    /// Configured sources omitted because the scan reached its source limit.
    /// </summary>
    public int SkippedSourceCount { get; init; }

    /// <summary>Sorted by folder order, then <c>NameLower</c> ordinal.</summary>
    private IReadOnlyList<PoseLibraryEntry> _entries = Array.Empty<PoseLibraryEntry>();
    private IReadOnlyList<PoseLibraryFolder> _folders = Array.Empty<PoseLibraryFolder>();
    private IReadOnlyList<PoseLibrarySourceSnapshot> _sources =
        Array.Empty<PoseLibrarySourceSnapshot>();

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

    /// <summary>Configured sources in their persisted order, including
    /// disabled and invalid entries.</summary>
    public required IReadOnlyList<PoseLibrarySourceSnapshot> Sources
    {
        get => _sources;
        init => _sources = Freeze(value);
    }

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<T>()).ToArray());
}
